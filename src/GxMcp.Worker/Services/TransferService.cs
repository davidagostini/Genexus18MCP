using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_transfer — real XPZ export / import over the SDK's
    /// <c>IKnowledgeManagerService</c> (P0 #1). Unlike genexus_io / genexus_kb_import
    /// (filesystem part-file copies that don't resolve dependencies), this is the IDE
    /// Export/Import code path: dependency-aware, identity-mapped.
    ///
    /// Actions:
    ///   • export  — targets[] + outputFile → dependency-aware .xpz. Read of KB, writes a file.
    ///   • inspect — explore an .xpz (ExploreExport) without importing. Read-only.
    ///   • import  — apply an .xpz into the active KB. DESTRUCTIVE; dryRun defaults true
    ///               (dryRun=true is an inspect); dryRun=false requires confirm=true.
    ///
    /// <c>IKnowledgeManagerService</c> implements <c>IGxService</c> → resolved via the
    /// generic <see cref="SdkServiceResolver"/>. Missing service → clean <c>*Unavailable</c>.
    /// </summary>
    public class TransferService
    {
        private readonly KbService _kb;
        private readonly ObjectService _objects;
        private readonly IndexCacheService _indexCache;

        public TransferService(KbService kb, ObjectService objects, IndexCacheService indexCache = null)
        {
            _kb = kb;
            _objects = objects;
            _indexCache = indexCache;
        }

        public string Run(JObject args)
        {
            string action = (args?["action"]?.ToString() ?? "").Trim().ToLowerInvariant();
            if (action != "export" && action != "import" && action != "inspect")
                return McpResponse.Err(
                    code: "BadAction",
                    message: "Unknown action '" + action + "'. Expected export, inspect, or import.",
                    hint: "genexus_transfer action=export|inspect|import.");

            if (!KbModelGuard.TryGetDesignModel(_kb, out var model, out var kbErr))
                return kbErr;

            var svc = SdkServiceResolver.Resolve<IKnowledgeManagerService>();
            if (svc == null)
                return McpResponse.Err(
                    code: "KnowledgeManagerServiceUnavailable",
                    message: "The GeneXus SDK's IKnowledgeManagerService is not registered in this worker session.",
                    hint: "Restart the worker (genexus_worker_reload mode=hard) and retry.");

            try
            {
                if (action == "export") return Export(svc, model, args);
                if (action == "inspect") return Inspect(svc, model, args, isDryRunImport: false);
                return Import(svc, model, args);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "TransferFailed", message: ex.Message, hint: "Check the worker log for the full stack trace.");
            }
        }

        private string Export(IKnowledgeManagerService svc, KBModel model, JObject args)
        {
            string outputFile = args?["outputFile"]?.ToString();
            if (string.IsNullOrWhiteSpace(outputFile))
                return McpResponse.Err(code: "BadArgs", message: "action=export requires outputFile.", hint: "Pass outputFile=<absolute .xpz path>.");

            var targets = args?["targets"] as JArray;
            if (targets == null || targets.Count == 0)
                return McpResponse.Err(code: "BadArgs", message: "action=export requires targets[] (object names).", hint: "Pass targets=[\"ObjName1\",\"ObjName2\"].");

            string typeFilter = args?["type"]?.ToString();
            bool includeDependencies = args?["includeDependencies"]?.ToObject<bool?>()
                                    ?? args?["withDependencies"]?.ToObject<bool?>()
                                    ?? false;

            var objs = new List<KBObject>();
            var missing = new JArray();
            var lookupErrors = new JArray();
            foreach (var t in targets)
            {
                string name = t?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                KBObject o = null;
                try { o = _objects?.FindObject(name, typeFilter); }
                catch (Exception ex) { lookupErrors.Add(new JObject { ["name"] = name, ["error"] = ex.Message }); continue; }
                if (o == null) missing.Add(name); else objs.Add(o);
            }

            if (objs.Count == 0)
                return McpResponse.Err(
                    code: "ObjectsNotFound",
                    message: "None of the requested objects were found.",
                    hint: "Check the names (genexus_query).",
                    target: string.Join(",", missing),
                    errorExtra: lookupErrors.Count > 0 ? new JObject { ["lookupErrors"] = lookupErrors } : null);

            int seedCount = objs.Count;
            var resolvedDependencies = new List<string>();

            if (includeDependencies && _indexCache != null)
            {
                var index = _indexCache.GetIndex();
                if (index != null && index.Objects != null)
                {
                    var visitedGuids = new HashSet<Guid>(objs.Select(o => o.Guid));
                    var visitedNames = new HashSet<string>(objs.Select(o => o.Name), StringComparer.OrdinalIgnoreCase);
                    var queue = new Queue<KBObject>(objs);

                    while (queue.Count > 0)
                    {
                        var current = queue.Dequeue();
                        string typeName = current.TypeDescriptor?.Name ?? "Object";
                        string storageKey = typeName + ":" + current.Name;

                        SearchIndex.IndexEntry entry = null;
                        if (!index.Objects.TryGetValue(storageKey, out entry))
                        {
                            entry = index.Objects.Values.FirstOrDefault(e => string.Equals(e.Name, current.Name, StringComparison.OrdinalIgnoreCase));
                        }

                        if (entry != null)
                        {
                            var depNames = new List<string>();
                            if (entry.Calls != null) depNames.AddRange(entry.Calls);
                            if (entry.Tables != null) depNames.AddRange(entry.Tables);

                            foreach (var depName in depNames)
                            {
                                if (string.IsNullOrWhiteSpace(depName) || visitedNames.Contains(depName)) continue;
                                visitedNames.Add(depName);

                                KBObject depObj = null;
                                try { depObj = _objects?.FindObject(depName); } catch { }
                                if (depObj != null && !visitedGuids.Contains(depObj.Guid))
                                {
                                    visitedGuids.Add(depObj.Guid);
                                    objs.Add(depObj);
                                    resolvedDependencies.Add(depObj.Name);
                                    queue.Enqueue(depObj);
                                }
                            }
                        }
                    }
                }
            }

            var options = SilentExportOptions();
            bool ok = svc.Export(model, objs, outputFile, options);

            return McpResponse.Ok(
                code: ok ? "TransferExported" : "TransferExportDeclined",
                result: new JObject
                {
                    ["success"] = ok,
                    ["outputFile"] = outputFile,
                    ["exportedCount"] = objs.Count,
                    ["seedCount"] = seedCount,
                    ["includeDependencies"] = includeDependencies,
                    ["dependenciesAdded"] = resolvedDependencies.Count,
                    ["resolvedDependencies"] = new JArray(resolvedDependencies),
                    ["notFound"] = missing,
                    ["lookupErrors"] = lookupErrors,
                    ["dependencyAware"] = true,
                    ["source"] = "sdk:IKnowledgeManagerService.Export"
                });
        }

        private string Inspect(IKnowledgeManagerService svc, KBModel model, JObject args, bool isDryRunImport)
        {
            string file = args?["file"]?.ToString() ?? args?["inputPath"]?.ToString();
            if (string.IsNullOrWhiteSpace(file))
                return McpResponse.Err(code: "BadArgs", message: "action=inspect requires file.", hint: "Pass file=<absolute .xpz path>.");
            if (!System.IO.File.Exists(file))
                return McpResponse.Err(code: "FileNotFound", message: "XPZ file not found: " + file, hint: "Pass an absolute path to an existing .xpz.");

            var opts = new ExploreExportOptions();
            svc.ExploreExport(file, model, opts, out var objects, out var actions, out var idMap);

            var items = new JArray();
            foreach (var o in AsEnumerable(objects))
            {
                string label = null;
                try { label = (o as KBObject)?.Name ?? o?.ToString(); } catch { label = o?.ToString(); }
                if (label != null) items.Add(label);
            }

            return McpResponse.Ok(
                code: isDryRunImport ? "TransferImportPreview" : "TransferInspected",
                result: new JObject
                {
                    ["file"] = file,
                    ["objectCount"] = Count(objects),
                    ["actionCount"] = Count(actions),
                    ["objects"] = items,
                    ["wouldImport"] = isDryRunImport,
                    ["source"] = "sdk:IKnowledgeManagerService.ExploreExport"
                });
        }

        private string Import(IKnowledgeManagerService svc, KBModel model, JObject args)
        {
            string file = args?["file"]?.ToString() ?? args?["inputPath"]?.ToString();
            if (string.IsNullOrWhiteSpace(file))
                return McpResponse.Err(code: "BadArgs", message: "action=import requires file.", hint: "Pass file=<absolute .xpz path>.");
            if (!System.IO.File.Exists(file))
                return McpResponse.Err(code: "FileNotFound", message: "XPZ file not found: " + file, hint: "Pass an absolute path to an existing .xpz.");

            // dryRun defaults TRUE — an import mutates the KB. dryRun=true previews via ExploreExport.
            bool dryRun = args?["dryRun"]?.ToObject<bool?>() ?? true;
            if (dryRun) return Inspect(svc, model, args, isDryRunImport: true);

            bool confirm = args?["confirm"]?.ToObject<bool?>() ?? false;
            if (!confirm)
                return McpResponse.Err(
                    code: "ConfirmRequired",
                    message: "action=import with dryRun=false requires confirm=true (it mutates the KB).",
                    hint: "Preview first with dryRun=true, then pass confirm=true to apply.");

            var options = SilentImportOptions(args);
            bool ok = svc.ImportFile(file, model, options);

            return McpResponse.Ok(
                code: ok ? "TransferImported" : "TransferImportDeclined",
                result: new JObject
                {
                    ["success"] = ok,
                    ["file"] = file,
                    ["source"] = "sdk:IKnowledgeManagerService.ImportFile"
                });
        }

        // A fresh ImportOptions with lossless defaults (FullOverwrite + UseFromExport);
        // prevents dropping WebForm dimensions or remapping Theme classes (Issue #102).
        private static ImportOptions SilentImportOptions(JObject args)
        {
            ImportOptions o = null;
            try { o = ImportOptions.FullOverwrite; } catch { }
            if (o == null)
            {
                try { o = ImportOptions.Default; } catch { }
            }
            if (o == null) o = new ImportOptions();

            try { o.AutomaticBackup = false; } catch { }
            try { o.RollBackOnError = true; } catch { }
            try { o.AutomaticRollbackOnCancel = true; } catch { }

            string classConflictArg = args?["classConflicts"]?.ToString();
            string classConflictVal = string.Equals(classConflictArg, "UseExisting", StringComparison.OrdinalIgnoreCase)
                ? "UseExisting"
                : "UseFromExport";
            SetEnumValue(o, "ClassConflicts", classConflictVal);

            string themeBehaviorArg = args?["themeImportBehavior"]?.ToString();
            string themeBehaviorVal = string.Equals(themeBehaviorArg, "IncrementalIntegration", StringComparison.OrdinalIgnoreCase)
                ? "IncrementalIntegration"
                : "Overwrite";
            SetEnumValue(o, "ThemeImportBehavior", themeBehaviorVal);

            return o;
        }

        private static void SetEnumValue(object target, string propertyName, string enumValueName)
        {
            try
            {
                if (target == null) return;
                var prop = target.GetType().GetProperty(propertyName);
                if (prop != null && prop.PropertyType.IsEnum)
                {
                    var val = Enum.Parse(prop.PropertyType, enumValueName, true);
                    prop.SetValue(target, val, null);
                }
            }
            catch { }
        }

        // A fresh ExportOptions with the dialog-free defaults the SDK uses for silent exports;
        // falls back to a plain instance if the static isn't available.
        private static ExportOptions SilentExportOptions()
        {
            try { var d = ExportOptions.SilentDefault; if (d != null) return d; } catch { }
            var o = new ExportOptions();
            try { o.IncludeReferencesDependencies = true; } catch { }
            try { o.ExportCurrentVersion = true; } catch { }
            try
            {
                var ucProp = typeof(ExportOptions).GetProperty("IncludeCustomUserControls");
                if (ucProp != null) ucProp.SetValue(o, true, null);
            }
            catch { }
            return o;
        }

        private static IEnumerable<object> AsEnumerable(object o)
        {
            if (o is IEnumerable e) foreach (var x in e) yield return x;
        }

        private static int Count(object o)
        {
            try { if (o is ICollection c) return c.Count; int n = 0; foreach (var _ in AsEnumerable(o)) n++; return n; }
            catch { return 0; }
        }
    }
}
