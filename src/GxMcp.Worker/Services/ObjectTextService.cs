using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Batch Object Text exchange, matching the four high-value operations exposed by
    /// GeneXus for Agents: export_kb_to_text, import_text_to_kb,
    /// validate_kb_text_files and delete_kb_objects.
    ///
    /// The manifest is intentionally small and portable. Files are relative to the
    /// selected directory and are resolved beneath that directory on import, so a
    /// hand-edited manifest cannot escape its input root.
    /// </summary>
    public sealed class ObjectTextService
    {
        public const string ManifestFileName = "_gxmcp-object-text-manifest.json";
        private const string ManifestKind = "GeneXusObjectText";

        private readonly ObjectService _objectService;
        private readonly IndexCacheService _indexCacheService;
        private readonly IUserFilePathPolicy _filePathPolicy;

        public ObjectTextService(ObjectService objectService, IndexCacheService indexCacheService)
            : this(objectService, indexCacheService,
                new UserFilePathPolicy(() => objectService?.GetKbService()?.GetKbPath()))
        {
        }

        internal ObjectTextService(ObjectService objectService, IndexCacheService indexCacheService,
            IUserFilePathPolicy filePathPolicy)
        {
            _objectService = objectService;
            _indexCacheService = indexCacheService;
            _filePathPolicy = filePathPolicy ?? throw new ArgumentNullException(nameof(filePathPolicy));
        }

        public string Execute(string action, string target, JObject args, CancellationToken cancellationToken)
        {
            action = action ?? string.Empty;
            args = args ?? new JObject();
            switch (action.ToLowerInvariant())
            {
                case "exporttextbatch":
                case "export_kb_to_text":
                    return Export(target, args, cancellationToken);
                case "importtextbatch":
                case "import_text_to_kb":
                    return Import(target, args, cancellationToken);
                case "validatetextbatch":
                case "validate_kb_text_files":
                    return Validate(target, args, cancellationToken);
                case "deletetextbatch":
                case "delete_kb_objects":
                    return Delete(target, args, cancellationToken);
                default:
                    return McpResponse.Err(
                        code: "UnknownObjectTextAction",
                        message: "Unknown Object Text action: " + action,
                        hint: "Use export_kb_to_text, import_text_to_kb, validate_kb_text_files or delete_kb_objects.");
            }
        }

        private string Export(string target, JObject args, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return BuildCancelled("export", null, new JArray(), 0, 0, 0);
            string outputPath = args["outputPath"]?.ToString() ?? args["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(outputPath))
                return McpResponse.Err(code: "OutputPathRequired", message: "outputPath is required for a KB Object Text export.", hint: "Provide a directory where the .gxtext files and manifest will be written.");

            string root;
            string pathError;
            if (!_filePathPolicy.TryResolveWritePath(outputPath, out root, out pathError))
                return McpResponse.Err(code: "PathOutsideAllowedRoots", message: pathError, hint: "Use a path under the active KB, the configured GeneXus installation, or set GXMCP_EXTERNAL_IO_ROOT for an explicit exchange directory.", target: outputPath);

            bool overwrite = args["overwrite"]?.ToObject<bool?>() ?? false;
            List<SearchIndex.IndexEntry> entries;
            string selectionError;
            if (!TrySelectEntries(target, args, allowAll: true, out entries, out selectionError))
                return McpResponse.Err(code: "ObjectSelectionFailed", message: selectionError, target: target);
            if (entries.Count == 0)
                return McpResponse.Err(code: "NoObjectsMatched", message: "No KB objects matched the export selector.", hint: "Use targets[], type, module or pathPrefix that exists in the active index.");

            string manifestPath = Path.Combine(root, ManifestFileName);
            var planned = new List<Tuple<SearchIndex.IndexEntry, string>>();
            for (int i = 0; i < entries.Count; i++)
            {
                string fileName = BuildObjectTextFileName(i, entries[i].Type, entries[i].Name);
                string filePath = Path.Combine(root, fileName);
                planned.Add(Tuple.Create(entries[i], filePath));
                if (!overwrite && (File.Exists(filePath) || File.Exists(manifestPath)))
                {
                    return McpResponse.Err(
                        code: "FileAlreadyExists",
                        message: "The Object Text export would overwrite an existing file.",
                        hint: "Pass overwrite=true or choose an empty output directory.",
                        target: filePath);
                }
            }

            try
            {
                if (!Directory.Exists(root)) Directory.CreateDirectory(root);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "ExportDirectoryCreateFailed", message: ex.Message, target: root);
            }

            var results = new JArray();
            var manifestItems = new JArray();
            int succeeded = 0;
            int failed = 0;
            for (int i = 0; i < planned.Count; i++)
            {
                if (ct.IsCancellationRequested)
                    return BuildCancelled("export", root, results, succeeded, failed, planned.Count);

                var item = planned[i].Item1;
                string filePath = planned[i].Item2;
                string objectTarget = item.Type + ":" + item.Name;
                string raw;
                try
                {
                    raw = _objectService.ExportObjectToText(objectTarget, filePath,
                        args["part"]?.ToString(), item.Type, overwrite: true);
                }
                catch (Exception ex)
                {
                    raw = new JObject
                    {
                        ["status"] = "error",
                        ["code"] = "ObjectTextExportFailed",
                        ["message"] = ex.Message
                    }.ToString(Formatting.None);
                }
                JObject parsed = ParseResult(raw);
                results.Add(BuildItemResult(item, filePath, parsed));
                if (IsSuccess(parsed))
                {
                    succeeded++;
                    manifestItems.Add(new JObject
                    {
                        ["name"] = item.Name,
                        ["type"] = item.Type,
                        ["part"] = args["part"]?.ToString() ?? "Source",
                        ["file"] = Path.GetFileName(filePath)
                    });
                }
                else failed++;
            }

            string manifestWriteError = null;
            try
            {
                var manifest = new JObject
                {
                    ["kind"] = ManifestKind,
                    ["schemaVersion"] = 1,
                    ["generatedAtUtc"] = DateTime.UtcNow.ToString("o"),
                    ["part"] = args["part"]?.ToString() ?? "Source",
                    ["objects"] = manifestItems
                };
                File.WriteAllText(manifestPath, manifest.ToString(Formatting.Indented), new UTF8Encoding(false));
            }
            catch (Exception ex) { manifestWriteError = ex.Message; failed++; }

            var result = BuildAggregateResult("export", root, results, planned.Count, succeeded, failed);
            result["manifestPath"] = manifestPath;
            if (manifestWriteError != null) result["manifestError"] = manifestWriteError;
            return failed > 0
                ? McpResponse.Partial(null, "ObjectTextExportPartial", result)
                : McpResponse.Ok(code: "ObjectTextExportCompleted", result: result);
        }

        private string Import(string target, JObject args, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return BuildCancelled("import", null, new JArray(), 0, 0, 0);
            string inputPath = args["inputPath"]?.ToString() ?? args["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(inputPath))
                return McpResponse.Err(code: "InputPathRequired", message: "inputPath is required for a KB Object Text import.", hint: "Provide a manifest, a .gxtext file, or a directory containing the manifest.");

            List<TextFileEntry> files;
            string root;
            string loadError;
            if (!TryLoadTextFiles(inputPath, target, args, out root, out files, out loadError))
                return McpResponse.Err(code: "ObjectTextManifestInvalid", message: loadError, target: inputPath);
            if (!ApplyManifestSelector(files, args, target))
                return McpResponse.Err(code: "NoObjectsMatched", message: "No Object Text manifest entries matched the selector.", hint: "Check targets[], name, type, module or pathPrefix.");

            bool dryRun = args["dryRun"]?.ToObject<bool?>() ?? false;
            var results = new JArray();
            int succeeded = 0;
            int failed = 0;
            for (int i = 0; i < files.Count; i++)
            {
                if (ct.IsCancellationRequested)
                    return BuildCancelled("import", root, results, succeeded, failed, files.Count);

                var item = files[i];
                string fullPath;
                if (!TryResolveUnderRoot(root, item.File, out fullPath) || !File.Exists(fullPath))
                {
                    var missing = new JObject
                    {
                        ["name"] = item.Name,
                        ["type"] = item.Type,
                        ["file"] = item.File,
                        ["status"] = "error",
                        ["code"] = "InputFileNotFound"
                    };
                    results.Add(missing);
                    failed++;
                    continue;
                }

                string raw;
                try
                {
                    raw = _objectService.ImportObjectFromText(
                        item.Name,
                        fullPath,
                        item.Part ?? args["part"]?.ToString(),
                        item.Type ?? args["type"]?.ToString(),
                        dryRun);
                }
                catch (Exception ex)
                {
                    raw = new JObject
                    {
                        ["status"] = "error",
                        ["code"] = "ObjectTextImportFailed",
                        ["message"] = ex.Message
                    }.ToString(Formatting.None);
                }
                JObject parsed = ParseResult(raw);
                results.Add(BuildItemResult(item, fullPath, parsed));
                if (IsSuccess(parsed)) succeeded++; else failed++;
            }

            var result = BuildAggregateResult("import", root, results, files.Count, succeeded, failed);
            result["dryRun"] = dryRun;
            return failed > 0
                ? McpResponse.Partial(null, "ObjectTextImportPartial", result)
                : McpResponse.Ok(code: dryRun ? "ObjectTextImportDryRun" : "ObjectTextImportCompleted", result: result);
        }

        private string Validate(string target, JObject args, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return BuildCancelled("validate", null, new JArray(), 0, 0, 0);
            string inputPath = args["inputPath"]?.ToString() ?? args["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(inputPath))
                return McpResponse.Err(code: "InputPathRequired", message: "inputPath is required for Object Text validation.", hint: "Provide the export directory or manifest path.");

            List<TextFileEntry> files;
            string root;
            string loadError;
            if (!TryLoadTextFiles(inputPath, target, args, out root, out files, out loadError))
                return McpResponse.Err(code: "ObjectTextManifestInvalid", message: loadError, target: inputPath);
            if (!ApplyManifestSelector(files, args, target))
                return McpResponse.Err(code: "NoObjectsMatched", message: "No Object Text manifest entries matched the selector.");

            var results = new JArray();
            int succeeded = 0;
            int failed = 0;
            for (int i = 0; i < files.Count; i++)
            {
                if (ct.IsCancellationRequested)
                    return BuildCancelled("validate", root, results, succeeded, failed, files.Count);

                var item = files[i];
                string fullPath;
                string error;
                if (!TryResolveUnderRoot(root, item.File, out fullPath) || !File.Exists(fullPath))
                {
                    error = "File does not exist under the manifest root.";
                }
                else
                {
                    error = ValidateTextFile(fullPath, item.Part ?? args["part"]?.ToString());
                    if (error == null && _objectService != null)
                    {
                        // When the object exists, exercise the same SDK import preflight
                        // used by a real import. It is dry-run only and therefore cannot
                        // create, save or alter the KB.
                        try
                        {
                            JObject probe = ParseResult(_objectService.ImportObjectFromText(
                                item.Name, fullPath, item.Part ?? args["part"]?.ToString(),
                                item.Type ?? args["type"]?.ToString(), dryRun: true));
                            if (!IsSuccess(probe)) error = probe["error"]?.ToString() ?? probe["message"]?.ToString() ?? "SDK validation failed.";
                        }
                        catch (Exception ex) { error = "SDK validation failed: " + ex.Message; }
                    }
                }

                var parsed = new JObject
                {
                    ["name"] = item.Name,
                    ["type"] = item.Type,
                    ["part"] = item.Part ?? args["part"]?.ToString() ?? "Source",
                    ["file"] = item.File,
                    ["valid"] = error == null
                };
                if (error != null)
                {
                    parsed["status"] = "error";
                    parsed["message"] = error;
                    failed++;
                }
                else
                {
                    parsed["status"] = "ok";
                    succeeded++;
                }
                results.Add(parsed);
            }

            var result = BuildAggregateResult("validate", root, results, files.Count, succeeded, failed);
            result["valid"] = failed == 0;
            return failed > 0
                ? McpResponse.Partial(null, "ObjectTextValidationPartial", result)
                : McpResponse.Ok(code: "ObjectTextValidationCompleted", result: result);
        }

        private string Delete(string target, JObject args, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return BuildCancelled("delete", null, new JArray(), 0, 0, 0);
            bool dryRun = args["dryRun"]?.ToObject<bool?>() ?? false;
            bool confirm = args["confirm"]?.ToObject<bool?>() ?? false;
            if (!dryRun && !confirm)
                return McpResponse.Err(code: "ConfirmRequired", message: "delete_kb_objects requires confirm=true.", hint: "Run dryRun=true first, then repeat with confirm=true to delete.");

            List<SearchIndex.IndexEntry> entries;
            string selectionError;
            if (!TrySelectEntries(target, args, allowAll: false, out entries, out selectionError))
                return McpResponse.Err(code: "ObjectSelectionFailed", message: selectionError, target: target);
            if (entries.Count == 0)
                return McpResponse.Err(code: "NoObjectsMatched", message: "No KB objects matched the delete selector.", hint: "Use targets[] or name with an exact object identity.");

            var results = new JArray();
            int succeeded = 0;
            int failed = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                if (ct.IsCancellationRequested)
                    return BuildCancelled("delete", null, results, succeeded, failed, entries.Count);

                var item = entries[i];
                JObject parsed;
                try
                {
                    parsed = ParseResult(_objectService.DeleteObject(
                        item.Type + ":" + item.Name, item.Type, confirm, dryRun));
                }
                catch (Exception ex)
                {
                    parsed = new JObject
                    {
                        ["status"] = "error",
                        ["code"] = "ObjectTextDeleteFailed",
                        ["message"] = ex.Message
                    };
                }
                results.Add(BuildItemResult(item, null, parsed));
                if (IsSuccess(parsed)) succeeded++; else failed++;
            }

            var result = BuildAggregateResult("delete", null, results, entries.Count, succeeded, failed);
            result["dryRun"] = dryRun;
            return failed > 0
                ? McpResponse.Partial(null, "ObjectTextDeletePartial", result)
                : McpResponse.Ok(code: dryRun ? "ObjectTextDeleteDryRun" : "ObjectTextDeleteCompleted", result: result);
        }

        private bool TrySelectEntries(string target, JObject args, bool allowAll,
            out List<SearchIndex.IndexEntry> selected, out string error)
        {
            selected = new List<SearchIndex.IndexEntry>();
            error = null;
            SearchIndex index;
            try { index = _indexCacheService?.TryGetLoadedIndex() ?? _indexCacheService?.GetIndex(); }
            catch (Exception ex) { error = "The object index could not be read: " + ex.Message; return false; }
            if (index?.Objects == null || index.Objects.Count == 0)
            {
                error = "The active object index is empty. Run genexus_lifecycle action=index first.";
                return false;
            }

            var selectors = ReadSelectors(target, args);
            bool explicitSelection = HasExplicitSelector(args, target);
            if (selectors.Count == 0 && (!allowAll || explicitSelection))
            {
                error = allowAll
                    ? "The Object Text selector was empty or invalid. Omit targets/name to export the full indexed KB."
                    : "A target is required for this Object Text action. Supply name or targets[].";
                return false;
            }

            string typeFilter = args["type"]?.ToString();
            string moduleFilter = args["module"]?.ToString();
            string pathPrefix = NormalizePath(args["pathPrefix"]?.ToString());
            int limit = args["limit"]?.ToObject<int?>() ?? 0;

            IEnumerable<SearchIndex.IndexEntry> baseEntries = !string.IsNullOrWhiteSpace(typeFilter)
                ? (IEnumerable<SearchIndex.IndexEntry>)index.FindByType(typeFilter)
                : index.Objects.Values;

            IEnumerable<SearchIndex.IndexEntry> query = baseEntries
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Name))
                .Where(e => !string.Equals(e.Type, "Folder", StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(e.Type, "Module", StringComparison.OrdinalIgnoreCase));
            if (selectors.Count > 0) query = query.Where(e => selectors.Any(s => SelectorMatches(s, e)));
            if (!string.IsNullOrWhiteSpace(typeFilter)) query = query.Where(e => string.Equals(e.Type, typeFilter, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(moduleFilter)) query = query.Where(e => string.Equals(e.Module, moduleFilter, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(pathPrefix)) query = query.Where(e => NormalizePath(e.Path).StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase));

            selected = query
                .OrderBy(e => e.Type ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (limit > 0) selected = selected.Take(limit).ToList();
            return true;
        }

        private static List<Selector> ReadSelectors(string target, JObject args)
        {
            var selectors = new List<Selector>();
            JArray array = args["targets"] as JArray ?? args["names"] as JArray ?? args["objects"] as JArray;
            if (array != null)
            {
                foreach (JToken token in array)
                {
                    Selector selector = ParseSelector(token);
                    if (selector != null) selectors.Add(selector);
                }
            }
            else
            {
                string name = args["name"]?.ToString() ?? target;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    foreach (string value in name.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        Selector selector = ParseSelector(value.Trim());
                        if (selector != null) selectors.Add(selector);
                    }
                }
            }
            return selectors;
        }

        private static Selector ParseSelector(JToken token)
        {
            if (token == null) return null;
            if (token.Type == JTokenType.Object)
            {
                string name = token["name"]?.ToString() ?? token["target"]?.ToString();
                string type = token["type"]?.ToString();
                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(type)) return null;
                return new Selector { Name = name, Type = type };
            }

            string raw = token.ToString().Trim();
            if (raw.Length == 0) return null;
            int colon = raw.IndexOf(':');
            if (colon > 0 && colon < raw.Length - 1)
                return new Selector { Type = raw.Substring(0, colon), Name = raw.Substring(colon + 1) };
            return new Selector { Name = raw };
        }

        private static bool SelectorMatches(Selector selector, SearchIndex.IndexEntry entry)
        {
            if (selector == null || entry == null) return false;
            if (!string.IsNullOrWhiteSpace(selector.Type)
                && !string.Equals(selector.Type, entry.Type, StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.IsNullOrWhiteSpace(selector.Name) || selector.Name == "*") return true;
            if (string.Equals(selector.Name, entry.Name, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(selector.Name, entry.Type + ":" + entry.Name, StringComparison.OrdinalIgnoreCase)) return true;

            int dot = selector.Name.LastIndexOf('.');
            return dot >= 0 && string.Equals(selector.Name.Substring(dot + 1), entry.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildObjectTextFileName(int ordinal, string type, string name)
        {
            return ordinal.ToString("D5") + "_" + SanitizeFilePart(type) + "__" + SanitizeFilePart(name) + ".gxtext";
        }

        internal static string SanitizeFilePart(string value)
        {
            string source = string.IsNullOrWhiteSpace(value) ? "unnamed" : value.Trim();
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            var sb = new StringBuilder(source.Length);
            foreach (char c in source)
                sb.Append(invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c);
            string result = sb.ToString().Trim().TrimEnd('.');
            if (result.Length == 0) result = "unnamed";
            return result.Length > 120 ? result.Substring(0, 120) : result;
        }

        private static bool TryResolveUnderRoot(string root, string relative, out string fullPath)
        {
            fullPath = null;
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return false;
            try
            {
                string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));
                if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return false;
                fullPath = candidate;
                return true;
            }
            catch { return false; }
        }

        private bool TryLoadTextFiles(string inputPath, string target, JObject args,
            out string root, out List<TextFileEntry> files, out string error)
        {
            root = null;
            files = new List<TextFileEntry>();
            error = null;
            string full;
            string pathError;
            if (!_filePathPolicy.TryResolveReadPath(inputPath, out full, out pathError))
            {
                error = pathError;
                return false;
            }

            string manifestPath = null;
            if (Directory.Exists(full))
            {
                root = full;
                manifestPath = Path.Combine(root, ManifestFileName);
                if (!File.Exists(manifestPath))
                {
                    error = "Manifest not found in input directory: " + ManifestFileName;
                    return false;
                }
            }
            else if (File.Exists(full))
            {
                root = Path.GetDirectoryName(full);
                if (string.Equals(Path.GetExtension(full), ".gxtext", StringComparison.OrdinalIgnoreCase))
                {
                    TextFileEntry standalone;
                    if (!TryBuildStandaloneEntry(full, target, args, out standalone, out error)) return false;
                    files.Add(standalone);
                    return true;
                }
                manifestPath = full;
            }
            else
            {
                error = "Input path does not exist.";
                return false;
            }

            try
            {
                JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
                string kind = manifest["kind"]?.ToString();
                if (!string.IsNullOrWhiteSpace(kind) && !string.Equals(kind, ManifestKind, StringComparison.OrdinalIgnoreCase))
                {
                    error = "Unsupported Object Text manifest kind: " + kind;
                    return false;
                }

                JArray items = manifest["objects"] as JArray ?? manifest["files"] as JArray;
                if (items == null)
                {
                    error = "Manifest must contain an objects[] array.";
                    return false;
                }
                foreach (JToken token in items)
                {
                    string name = token["name"]?.ToString() ?? token["target"]?.ToString();
                    string file = token["file"]?.ToString() ?? token["path"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(file))
                    {
                        error = "Every manifest entry requires name and file.";
                        return false;
                    }
                    files.Add(new TextFileEntry
                    {
                        Name = name,
                        Type = token["type"]?.ToString(),
                        Part = token["part"]?.ToString() ?? manifest["part"]?.ToString() ?? "Source",
                        File = file,
                        Module = token["module"]?.ToString(),
                        Path = token["objectPath"]?.ToString() ?? token["pathPrefix"]?.ToString()
                    });
                }
            }
            catch (Exception ex)
            {
                error = "Could not read Object Text manifest: " + ex.Message;
                return false;
            }
            return true;
        }

        private static bool TryBuildStandaloneEntry(string fullPath, string target, JObject args,
            out TextFileEntry entry, out string error)
        {
            entry = null;
            error = null;
            string requested = args["name"]?.ToString() ?? target;
            Selector selector = string.IsNullOrWhiteSpace(requested) ? null : ParseSelector(requested);
            string type = selector?.Type ?? args["type"]?.ToString();
            string name = selector?.Name;

            if (string.IsNullOrWhiteSpace(name) || name == "*")
            {
                string stem = Path.GetFileNameWithoutExtension(fullPath) ?? string.Empty;
                int marker = stem.IndexOf("__", StringComparison.Ordinal);
                if (marker > 0 && marker + 2 < stem.Length)
                {
                    string left = stem.Substring(0, marker);
                    int separator = left.IndexOf('_');
                    if (separator >= 0 && separator + 1 < left.Length)
                    {
                        if (string.IsNullOrWhiteSpace(type)) type = left.Substring(separator + 1);
                    }
                    else if (string.IsNullOrWhiteSpace(type))
                    {
                        type = left;
                    }
                    name = stem.Substring(marker + 2);
                }
            }

            if (string.IsNullOrWhiteSpace(name) || name == "*")
            {
                error = "A standalone .gxtext file needs name (or target), or a deterministic export filename such as 00000_Procedure__MyProc.gxtext.";
                return false;
            }

            entry = new TextFileEntry
            {
                Name = name,
                Type = type,
                Part = args["part"]?.ToString() ?? "Source",
                File = Path.GetFileName(fullPath),
                Module = args["module"]?.ToString(),
                Path = args["objectPath"]?.ToString() ?? args["pathPrefix"]?.ToString()
            };
            return true;
        }

        private static bool ApplyManifestSelector(List<TextFileEntry> files, JObject args, string target)
        {
            var selectors = ReadSelectors(target, args);
            string type = args["type"]?.ToString();
            string module = args["module"]?.ToString();
            string prefix = NormalizePath(args["pathPrefix"]?.ToString());
            bool explicitSelection = HasExplicitSelector(args, target);
            files.RemoveAll(item => !MatchesManifestSelector(item, selectors, explicitSelection, type, module, prefix));
            int limit = args["limit"]?.ToObject<int?>() ?? 0;
            if (limit > 0 && files.Count > limit)
                files.RemoveRange(limit, files.Count - limit);
            return files.Count > 0;
        }

        private static bool MatchesManifestSelector(TextFileEntry item, List<Selector> selectors,
            bool explicitSelection, string type, string module, string prefix)
        {
            if (item == null) return false;
            if ((explicitSelection && selectors.Count == 0)
                || (selectors.Count > 0 && !selectors.Any(s => SelectorMatches(s, item))))
                return false;
            if (!string.IsNullOrWhiteSpace(type)
                && !string.Equals(type, item.Type, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(module)
                && !string.Equals(module, item.Module, StringComparison.OrdinalIgnoreCase))
                return false;
            return string.IsNullOrWhiteSpace(prefix)
                || NormalizePath(item.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasExplicitSelector(JObject args, string target)
        {
            if (!string.IsNullOrWhiteSpace(target)) return true;
            if (args == null) return false;
            if (args["targets"] != null || args["names"] != null || args["objects"] != null) return true;
            return !string.IsNullOrWhiteSpace(args["name"]?.ToString());
        }

        private static bool SelectorMatches(Selector selector, TextFileEntry item)
        {
            if (selector == null || item == null) return false;
            if (!string.IsNullOrWhiteSpace(selector.Type)
                && !string.Equals(selector.Type, item.Type, StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.IsNullOrWhiteSpace(selector.Name) || selector.Name == "*") return true;
            if (string.Equals(selector.Name, item.Name, StringComparison.OrdinalIgnoreCase)) return true;

            int dot = selector.Name.LastIndexOf('.');
            return dot >= 0
                && string.Equals(selector.Name.Substring(dot + 1), item.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static string ValidateTextFile(string path, string part)
        {
            try
            {
                string text = File.ReadAllText(path);
                if (text == null) return "File could not be read.";
                if (string.Equals(part, "WebForm", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(part, "Layout", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(part, "PatternInstance", StringComparison.OrdinalIgnoreCase))
                {
                    try { XDocument.Parse(text, LoadOptions.PreserveWhitespace); }
                    catch (Exception ex) { return "Invalid XML: " + ex.Message; }
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        private static JObject BuildItemResult(SearchIndex.IndexEntry item, string path, JObject response)
        {
            var result = new JObject
            {
                ["name"] = item?.Name,
                ["type"] = item?.Type,
                ["path"] = path,
                ["response"] = response ?? new JObject { ["status"] = "error" }
            };
            return result;
        }

        private static JObject BuildItemResult(TextFileEntry item, string path, JObject response)
        {
            return new JObject
            {
                ["name"] = item?.Name,
                ["type"] = item?.Type,
                ["part"] = item?.Part,
                ["file"] = item?.File,
                ["path"] = path,
                ["response"] = response ?? new JObject { ["status"] = "error" }
            };
        }

        private static JObject BuildAggregateResult(string operation, string root, JArray results,
            int attempted, int succeeded, int failed)
        {
            var result = new JObject
            {
                ["operation"] = operation,
                ["attempted"] = attempted,
                ["succeeded"] = succeeded,
                ["failed"] = failed,
                ["results"] = results
            };
            if (!string.IsNullOrWhiteSpace(root)) result["root"] = root;
            return result;
        }

        private static string BuildCancelled(string operation, string root, JArray results,
            int succeeded, int failed, int attempted)
        {
            var result = BuildAggregateResult(operation, root, results, attempted, succeeded, failed);
            result["cancelled"] = true;
            result["remaining"] = Math.Max(0, attempted - results.Count);
            return McpResponse.Ok(code: "Cancelled", result: result);
        }

        private static JObject ParseResult(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new JObject
                {
                    ["status"] = "error",
                    ["message"] = "Empty worker response."
                };
            }

            try
            {
                return JObject.Parse(raw);
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["status"] = "error",
                    ["message"] = ex.Message,
                    ["raw"] = raw
                };
            }
        }

        private static bool IsSuccess(JObject response)
        {
            string status = response?["status"]?.ToString();
            return string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "partial", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string value)
        {
            return (value ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        }

        private sealed class Selector
        {
            public string Name;
            public string Type;
        }

        private sealed class TextFileEntry
        {
            public string Name;
            public string Type;
            public string Part;
            public string File;
            public string Module;
            public string Path;
        }
    }
}
