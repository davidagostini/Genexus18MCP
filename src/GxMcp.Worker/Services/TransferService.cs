using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;
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
        private const int MaxExportXmlEntries = 2048;
        private const long MaxExportXmlEntryBytes = 8L * 1024 * 1024;
        private const long MaxExportXmlBytes = 64L * 1024 * 1024;

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
                            entry = index.FindByName(current.Name).FirstOrDefault();
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

            ExportPackageSummary packageSummary;
            try
            {
                packageSummary = ReadExportPackageSummary(file);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "TransferInspectionUnavailable",
                    message: "The XPZ package could not be read for inspection. " + ex.Message,
                    hint: "Retry with a complete XPZ produced by GeneXus Export.");
            }

            var opts = new ExploreExportOptions();
            svc.ExploreExport(file, model, opts, out var objects, out var actions, out var idMap);

            var items = new JArray();
            var details = new JArray();
            foreach (var packageObject in packageSummary.Objects)
            {
                items.Add(packageObject.Name);
                details.Add(new JObject
                {
                    ["name"] = packageObject.Name,
                    ["type"] = packageObject.Type,
                    ["partCount"] = packageObject.PartCount,
                    ["hasPayload"] = packageObject.NonEmptyPartCount > 0
                });
            }

            foreach (var o in AsEnumerable(objects))
            {
                string label = null;
                try { label = (o as KBObject)?.Name ?? o?.ToString(); } catch { label = o?.ToString(); }
                if (label != null && !items.Any(i => string.Equals(i?.ToString(), label, StringComparison.OrdinalIgnoreCase)))
                {
                    items.Add(label);
                    details.Add(new JObject
                    {
                        ["name"] = label,
                        ["type"] = o?.GetType()?.Name,
                        ["source"] = "sdk"
                    });
                }
            }

            string packageValidationError = ValidateImportPackage(packageSummary);

            return McpResponse.Ok(
                code: isDryRunImport ? "TransferImportPreview" : "TransferInspected",
                result: new JObject
                {
                    ["file"] = file,
                    ["objectCount"] = packageSummary.Objects.Count > 0 ? packageSummary.Objects.Count : Count(objects),
                    ["actionCount"] = Count(actions),
                    ["objects"] = items,
                    ["objectDetails"] = details,
                    ["wouldImport"] = isDryRunImport,
                    ["preflight"] = new JObject
                    {
                        ["validForImport"] = string.IsNullOrWhiteSpace(packageValidationError),
                        ["reason"] = packageValidationError
                    },
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

            ImportOptions options;
            try
            {
                options = SilentImportOptions(args);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "InvalidTransferOptions",
                    message: "The requested XPZ import options could not be applied; no import was attempted. " + ex.Message,
                    hint: "Use the supported conflict/theme option values or omit them to use the safe defaults.");
            }

            ExportPackageSummary packageSummary;
            try
            {
                packageSummary = ReadExportPackageSummary(file);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "TransferImportVerificationUnavailable",
                    message: "The XPZ package could not be read before mutation; no import was attempted. " + ex.Message,
                    hint: "Retry with a complete XPZ produced by GeneXus Export, and verify that the file is readable by the GeneXus SDK.");
            }

            string packageValidationError = ValidateImportPackage(packageSummary);
            if (!string.IsNullOrWhiteSpace(packageValidationError))
                return McpResponse.Err(
                    code: "TransferImportVerificationUnavailable",
                    message: packageValidationError + " No import was attempted.",
                    hint: "Export the object again from GeneXus and retry only after inspect reports readable object payloads.");

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
            if (candidates.Count == 0 && exportedWebForms.Count > 0)
                throw new InvalidDataException("The XPZ contains raw WebForm payloads but the SDK exposed no import candidates for them.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool sawWebFormCandidate = false;

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
                sawWebFormCandidate = true;

                if (string.IsNullOrWhiteSpace(source.Name)
                    || !exportedWebForms.TryGetValue(source.Name, out string expectedXml)
                    || string.IsNullOrWhiteSpace(expectedXml))
                    throw new InvalidDataException(
                        "The XPZ contains a WebForm candidate without a readable raw WebForm payload for '"
                        + (source.Name ?? "<unnamed>") + "'. Fidelity verification cannot use the SDK projection as a baseline.");

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

            if (sawWebFormCandidate && plan.Items.Count == 0)
                throw new InvalidDataException("The XPZ exposed WebForm objects but no raw WebForm payload could be mapped.");

            return plan;
        }

        internal static Dictionary<string, string> ReadExportWebForms(string file)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return result;

            int xmlEntries = 0;
            long totalBytes = 0;

            using (var archive = ZipFile.OpenRead(file))
            {
                foreach (var entry in archive.Entries.Where(e =>
                    e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                {
                    if (++xmlEntries > MaxExportXmlEntries)
                        throw new InvalidDataException("The XPZ contains too many XML entries for safe fidelity inspection.");
                    if (entry.Length > MaxExportXmlEntryBytes || (totalBytes += entry.Length) > MaxExportXmlBytes)
                        throw new InvalidDataException("The XPZ XML payload exceeds the safe fidelity-inspection limit.");

                    XDocument document;
                    using (var stream = entry.Open())
                    using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        MaxCharactersFromEntities = 0
                    }))
                        document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);

                    foreach (var obj in document.Descendants().Where(e => string.Equals(e.Name.LocalName, "Object", StringComparison.OrdinalIgnoreCase)))
                    {
                        string name = obj.Attributes().FirstOrDefault(a =>
                            string.Equals(a.Name.LocalName, "name", StringComparison.OrdinalIgnoreCase))?.Value;
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        foreach (var source in obj.Descendants().Where(e =>
                            string.Equals(e.Name.LocalName, "Source", StringComparison.OrdinalIgnoreCase)
                            && e.Parent != null
                            && string.Equals(e.Parent.Name.LocalName, "Part", StringComparison.OrdinalIgnoreCase)))
                        {
                            string xml = source.Value;
                            if (string.IsNullOrWhiteSpace(xml)) continue;
                            if (IsWebFormPayload(xml))
                            {
                                result[name] = xml.Trim();
                                break;
                            }
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Reads the package's object metadata before ImportFile is called.
        /// ExploreExport can expose an IExportItem for a damaged XPZ even when
        /// the item has no usable parts; importing that projection creates a
        /// hollow object. This summary is deliberately package-based so the
        /// mutation gate does not trust the SDK projection it is protecting.
        /// </summary>
        internal static ExportPackageSummary ReadExportPackageSummary(string file)
        {
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                throw new FileNotFoundException("XPZ file not found.", file);

            var summary = new ExportPackageSummary();
            int xmlEntries = 0;
            long totalBytes = 0;

            using (var archive = ZipFile.OpenRead(file))
            {
                foreach (var entry in archive.Entries.Where(e =>
                    e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                {
                    if (++xmlEntries > MaxExportXmlEntries)
                        throw new InvalidDataException("The XPZ contains too many XML entries for safe preflight inspection.");
                    if (entry.Length > MaxExportXmlEntryBytes || (totalBytes += entry.Length) > MaxExportXmlBytes)
                        throw new InvalidDataException("The XPZ XML payload exceeds the safe preflight-inspection limit.");

                    XDocument document;
                    using (var stream = entry.Open())
                    using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        MaxCharactersFromEntities = 0
                    }))
                        document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);

                    foreach (var objectElement in document.Descendants().Where(e =>
                        string.Equals(e.Name.LocalName, "Object", StringComparison.OrdinalIgnoreCase)))
                    {
                        string name = ReadAttribute(objectElement, "name", "objectName")
                            ?? ReadChildValue(objectElement, "Name");
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            summary.UnnamedObjectCount++;
                            continue;
                        }

                        string type = ReadAttribute(objectElement, "type", "objectType")
                            ?? ReadChildValue(objectElement, "Type", "ObjectType");
                        var parts = objectElement.Descendants().Where(e =>
                            string.Equals(e.Name.LocalName, "Part", StringComparison.OrdinalIgnoreCase)).ToList();
                        int nonEmptyParts = parts.Count(HasMeaningfulPartPayload);
                        string key = (type ?? string.Empty) + "|" + name;
                        var existing = summary.Objects.FirstOrDefault(o =>
                            string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));
                        if (existing == null)
                        {
                            summary.Objects.Add(new ExportPackageObject
                            {
                                Key = key,
                                Name = name,
                                Type = type,
                                PartCount = parts.Count,
                                NonEmptyPartCount = nonEmptyParts
                            });
                        }
                        else
                        {
                            existing.PartCount += parts.Count;
                            existing.NonEmptyPartCount += nonEmptyParts;
                        }
                    }
                }
            }

            return summary;
        }

        internal static string ValidateImportPackage(ExportPackageSummary summary)
        {
            if (summary == null)
                return "The XPZ preflight produced no package summary.";
            if (summary.UnnamedObjectCount > 0)
                return "The XPZ contains object records without a readable object name.";
            if (summary.Objects.Count == 0)
                return "The XPZ contains no readable object records.";

            var emptyObjects = summary.Objects
                .Where(o => o.NonEmptyPartCount == 0)
                .Select(o => string.IsNullOrWhiteSpace(o.Type) ? o.Name : o.Name + " (" + o.Type + ")")
                .ToList();
            if (emptyObjects.Count > 0)
                return "The XPZ contains object records without a non-empty part payload: "
                    + string.Join(", ", emptyObjects) + ".";

            return null;
        }

        private static string ReadAttribute(XElement element, params string[] names)
        {
            foreach (string name in names)
            {
                string value = element.Attributes().FirstOrDefault(a =>
                    string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return null;
        }

        private static string ReadChildValue(XElement element, params string[] names)
        {
            foreach (string name in names)
            {
                string value = element.Elements().FirstOrDefault(e =>
                    string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return null;
        }

        private static bool HasMeaningfulPartPayload(XElement part)
        {
            return part.Descendants().Any(e =>
                !string.IsNullOrWhiteSpace(e.Value)
                || e.Attributes().Any(a => !string.IsNullOrWhiteSpace(a.Value)));
        }

        private static bool IsWebFormPayload(string xml)
        {
            try
            {
                using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersFromEntities = 0
                }))
                {
                    var root = XDocument.Load(reader, LoadOptions.PreserveWhitespace).Root;
                    string local = root?.Name.LocalName;
                    return string.Equals(local, "GxMultiForm", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(local, "BODY", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(local, "Layout", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
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

        internal sealed class ExportPackageSummary
        {
            internal List<ExportPackageObject> Objects { get; } = new List<ExportPackageObject>();
            internal int UnnamedObjectCount { get; set; }
        }

        internal sealed class ExportPackageObject
        {
            internal string Key { get; set; }
            internal string Name { get; set; }
            internal string Type { get; set; }
            internal int PartCount { get; set; }
            internal int NonEmptyPartCount { get; set; }
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

            string classConflicts = args?["classConflicts"]?.ToString();
            if (!string.IsNullOrWhiteSpace(classConflicts))
            {
                if (!string.Equals(classConflicts, "UseExisting", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(classConflicts, "UseFromExport", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("classConflicts must be UseExisting or UseFromExport.");
                if (!SetEnumValue(o, "ClassConflicts", classConflicts))
                    throw new InvalidOperationException("The installed GeneXus SDK does not expose a writable ClassConflicts option.");
            }

            string themeImportBehavior = args?["themeImportBehavior"]?.ToString();
            if (!string.IsNullOrWhiteSpace(themeImportBehavior))
            {
                if (!string.Equals(themeImportBehavior, "IncrementalIntegration", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(themeImportBehavior, "Overwrite", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("themeImportBehavior must be IncrementalIntegration or Overwrite.");
                if (!SetEnumValue(o, "ThemeImportBehavior", themeImportBehavior))
                    throw new InvalidOperationException("The installed GeneXus SDK does not expose a writable ThemeImportBehavior option.");
            }

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

        private static bool SetEnumValue(object target, string propertyName, string enumValueName)
        {
            try
            {
                if (target == null) return false;
                var prop = target.GetType().GetProperty(propertyName);
                if (prop == null || !prop.PropertyType.IsEnum || !prop.CanWrite) return false;
                var val = Enum.Parse(prop.PropertyType, enumValueName, true);
                prop.SetValue(target, val, null);
                return true;
            }
            catch { return false; }
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
