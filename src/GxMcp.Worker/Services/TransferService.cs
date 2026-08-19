using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
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
        private readonly WriteService _writeService;

        public TransferService(KbService kb, ObjectService objects, IndexCacheService indexCache = null,
            WriteService writeService = null)
        {
            _kb = kb;
            _objects = objects;
            _indexCache = indexCache;
            _writeService = writeService;
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
            ImportFidelityPlan fidelityPlan;
            try
            {
                fidelityPlan = CaptureImportFidelity(svc, model, file, options);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "TransferImportVerificationUnavailable",
                    message: "The XPZ could not be inspected for WebForm preservation; no import was attempted. " + ex.Message,
                    hint: "Retry after the XPZ is readable by the GeneXus SDK.");
            }

            bool ok = svc.ImportFile(file, model, options);

            if (!ok)
            {
                return McpResponse.Ok(
                    code: "TransferImportDeclined",
                    result: new JObject
                    {
                        ["success"] = false,
                        ["file"] = file,
                        ["source"] = "sdk:IKnowledgeManagerService.ImportFile",
                        ["fidelityVerified"] = false,
                        ["fidelity"] = new JObject { ["objectsChecked"] = 0 }
                    });
            }

            var fidelity = VerifyImportedWebForms(fidelityPlan);
            if (!fidelity.Verified)
            {
                return McpResponse.Err(
                    code: "TransferImportFidelityFailed",
                    message: "The XPZ import completed, but one or more WebForm parts did not survive the SDK import unchanged.",
                    hint: "The affected existing objects were restored when possible; inspect the fidelity block before retrying.",
                    extra: new JObject
                    {
                        ["imported"] = true,
                        ["file"] = file,
                        ["fidelityVerified"] = false,
                        ["fidelity"] = fidelity.Result
                    });
            }

            return McpResponse.Ok(
                code: "TransferImported",
                result: new JObject
                {
                    ["success"] = true,
                    ["file"] = file,
                    ["source"] = "sdk:IKnowledgeManagerService.ImportFile",
                    ["fidelityVerified"] = true,
                    ["fidelity"] = fidelity.Result
                });
        }

        private ImportFidelityPlan CaptureImportFidelity(IKnowledgeManagerService svc, KBModel model,
            string file, ImportOptions options)
        {
            var plan = new ImportFidelityPlan();
            // IExportItem.Object is guarded by PrepareImport in GeneXus 18 U5.
            // Read the source WebForm from the XPZ package before preparing the
            // item; otherwise the SDK exposes only its normalized projection and
            // the fidelity check becomes circular (issue #102).
            var exportedWebForms = ReadExportWebForms(file);
            var prepared = svc.PrepareImport(file, model, options);
            var exploreOptions = new ExploreExportOptions();
            svc.ExploreExport(file, model, exploreOptions, out var exportedObjects, out _, out _);
            var candidates = AsEnumerable(exportedObjects).ToList();
            if (candidates.Count == 0)
                candidates = AsEnumerable(prepared?.Items).ToList();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in candidates)
            {
                var item = raw as IExportItem;
                if (item == null) continue;

                // Object is intentionally accessed only after PrepareImport: U5
                // throws when the guarded getter is used earlier.
                item.PrepareImport(item.BaseModel ?? model, model, prepared);
                var source = item.Object;
                string sourceType = source?.TypeDescriptor?.Name;
                if (source == null) continue;
                var sourcePart = WebFormXmlHelper.GetWebFormPart(source);
                if (sourcePart == null) continue;

                string expectedXml = null;
                if (!string.IsNullOrWhiteSpace(source.Name))
                    exportedWebForms.TryGetValue(source.Name, out expectedXml);
                if (string.IsNullOrWhiteSpace(expectedXml))
                    expectedXml = WebFormXmlHelper.ReadEditableXml(source);
                if (string.IsNullOrWhiteSpace(expectedXml)) continue;

                string typeFilter = sourceType ?? source.TypeDescriptor?.Name;
                string partName = sourcePart.TypeDescriptor?.Name ?? "WebForm";
                string key = (typeFilter ?? string.Empty) + "|" + source.Name + "|" + partName;
                if (!seen.Add(key)) continue;

                var existing = _objects?.FindObjectFresh(source.Name, typeFilter);
                plan.Items.Add(new ImportWebFormSnapshot
                {
                    Name = source.Name,
                    TypeFilter = typeFilter,
                    PartName = partName,
                    ExpectedXml = expectedXml,
                    ExistingBefore = existing != null,
                    BeforeXml = existing == null ? null : WebFormXmlHelper.ReadEditableXml(existing)
                });
            }

            return plan;
        }

        internal static Dictionary<string, string> ReadExportWebForms(string file)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return result;

            using (var archive = ZipFile.OpenRead(file))
            {
                foreach (var entry in archive.Entries.Where(e =>
                    e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                {
                    XDocument document;
                    try
                    {
                        using (var stream = entry.Open())
                            document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var obj in document.Descendants("Object"))
                    {
                        string name = (string)obj.Attribute("name");
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        foreach (var source in obj.Descendants("Part").Elements("Source"))
                        {
                            string xml = source.Value;
                            if (string.IsNullOrWhiteSpace(xml)) continue;
                            xml = xml.TrimStart();
                            if (xml.StartsWith("<GxMultiForm", StringComparison.OrdinalIgnoreCase)
                                || xml.StartsWith("<BODY", StringComparison.OrdinalIgnoreCase)
                                || xml.StartsWith("<Layout", StringComparison.OrdinalIgnoreCase))
                            {
                                result[name] = xml;
                                break;
                            }
                        }
                    }
                }
            }

            return result;
        }

        private ImportFidelityResult VerifyImportedWebForms(ImportFidelityPlan plan)
        {
            var mismatches = new JArray();
            int repaired = 0;
            bool rollbackAttempted = false;
            bool rollbackSucceeded = true;

            foreach (var expected in plan.Items)
            {
                var current = _objects?.FindObjectFresh(expected.Name, expected.TypeFilter);
                string actualXml = current == null ? string.Empty : WebFormXmlHelper.ReadEditableXml(current);
                string diff;
                if (XmlEquivalence.AreEquivalent(expected.ExpectedXml, actualXml, out diff)) continue;

                var mismatch = new JObject
                {
                    ["name"] = expected.Name,
                    ["part"] = expected.PartName,
                    ["initialDiff"] = diff ?? "n/a"
                };

                bool repairedHere = false;
                if (_writeService != null)
                {
                    string writeRaw = _writeService.WriteObject(
                        expected.Name,
                        expected.PartName,
                        expected.ExpectedXml,
                        expected.TypeFilter,
                        autoValidate: true,
                        preferFastSourceSave: false,
                        autoInjectVariables: true,
                        dryRun: false,
                        explicitBase64: false,
                        strictVerify: true);
                    JObject write = ParseObject(writeRaw);
                    repairedHere = IsSuccessfulWrite(write);
                    mismatch["repairResponse"] = write;

                    if (repairedHere)
                    {
                        var repairedObject = _objects?.FindObjectFresh(expected.Name, expected.TypeFilter);
                        string repairedXml = repairedObject == null ? string.Empty : WebFormXmlHelper.ReadEditableXml(repairedObject);
                        string repairedDiff;
                        repairedHere = XmlEquivalence.AreEquivalent(expected.ExpectedXml, repairedXml, out repairedDiff);
                        if (!repairedHere) mismatch["repairDiff"] = repairedDiff ?? "n/a";
                    }
                }

                if (repairedHere)
                {
                    repaired++;
                    mismatch["repaired"] = true;
                    continue;
                }

                mismatch["repaired"] = false;
                mismatches.Add(mismatch);
                rollbackAttempted = true;
                if (!TryRestoreImportedObject(expected)) rollbackSucceeded = false;
            }

            var result = new JObject
            {
                ["objectsChecked"] = plan.Items.Count,
                ["repaired"] = repaired,
                ["mismatches"] = mismatches,
                ["rollbackAttempted"] = rollbackAttempted,
                ["rollbackSucceeded"] = rollbackSucceeded
            };
            return new ImportFidelityResult
            {
                Verified = mismatches.Count == 0,
                Result = result
            };
        }

        private bool TryRestoreImportedObject(ImportWebFormSnapshot expected)
        {
            if (!expected.ExistingBefore || string.IsNullOrWhiteSpace(expected.BeforeXml) || _writeService == null)
                return false;

            string raw = _writeService.WriteObject(
                expected.Name,
                expected.PartName,
                expected.BeforeXml,
                expected.TypeFilter,
                autoValidate: true,
                preferFastSourceSave: false,
                autoInjectVariables: true,
                dryRun: false,
                explicitBase64: false,
                strictVerify: true);
            return IsSuccessfulWrite(ParseObject(raw));
        }

        private static JObject ParseObject(string raw)
        {
            try { return string.IsNullOrWhiteSpace(raw) ? new JObject() : JObject.Parse(raw); }
            catch { return new JObject { ["raw"] = raw }; }
        }

        private static bool IsSuccessfulWrite(JObject response)
        {
            string status = response?["status"]?.ToString();
            return string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "partial", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class ImportFidelityPlan
        {
            public List<ImportWebFormSnapshot> Items { get; } = new List<ImportWebFormSnapshot>();
        }

        private sealed class ImportWebFormSnapshot
        {
            public string Name { get; set; }
            public string TypeFilter { get; set; }
            public string PartName { get; set; }
            public string ExpectedXml { get; set; }
            public bool ExistingBefore { get; set; }
            public string BeforeXml { get; set; }
        }

        private sealed class ImportFidelityResult
        {
            public bool Verified { get; set; }
            public JObject Result { get; set; }
        }

        // The SDK's incremental defaults can normalize visual XML while importing.
        // FullOverwrite is the lossless baseline; the post-import verifier still
        // repairs any SDK projection drift that survives the import call.
        internal static ImportOptions SilentImportOptions(JObject args)
        {
            ImportOptions o = null;
            // FullOverwrite is the SDK mode that preserves the complete exported
            // WebForm payload (including GxWidth/GxHeight) when overwriting an
            // existing object.  Default performs incremental integration and was
            // the direct cause of the reported WebForm regression.
            try { o = ImportOptions.FullOverwrite; } catch { }
            if (o == null)
            {
                try { o = ImportOptions.Default; } catch { }
            }
            if (o == null) o = new ImportOptions();

            try { o.AutomaticBackup = false; } catch { }
            try { o.RollBackOnError = true; } catch { }
            try { o.AutomaticRollbackOnCancel = true; } catch { }

            SetEnumValue(o, "ClassConflicts", ResolveImportClassConflicts(args));
            SetEnumValue(o, "ThemeImportBehavior", ResolveImportThemeBehavior(args));

            return o;
        }

        internal static string ResolveImportClassConflicts(JObject args)
        {
            return string.Equals(args?["classConflicts"]?.ToString(), "UseExisting", StringComparison.OrdinalIgnoreCase)
                ? "UseExisting"
                : "UseFromExport";
        }

        internal static string ResolveImportThemeBehavior(JObject args)
        {
            return string.Equals(args?["themeImportBehavior"]?.ToString(), "IncrementalIntegration", StringComparison.OrdinalIgnoreCase)
                ? "IncrementalIntegration"
                : "Overwrite";
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
