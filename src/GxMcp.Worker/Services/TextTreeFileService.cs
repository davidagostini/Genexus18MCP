using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Xml.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XmlReader = System.Xml.XmlReader;
using XmlReaderSettings = System.Xml.XmlReaderSettings;
using DtdProcessing = System.Xml.DtdProcessing;
using XmlException = System.Xml.XmlException;

namespace GxMcp.Worker.Services
{
    internal static class TextTreeFileService
    {
        private const string ManifestKind = "GeneXusObjectText";
        internal static string ValidateInMemory(JObject args, CancellationToken ct)
        {
            string inputPath = args["inputPath"]?.ToString() ?? args["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(inputPath))
                return McpResponse.Err(
                    code: "InputPathRequired",
                    message: "inputPath is required for in-memory Object Text validation.",
                    hint: "Provide the root directory containing src and/or ref.");

            string root;
            try { root = Path.GetFullPath(inputPath); }
            catch (Exception ex) { return McpResponse.Err(code: "InvalidInputPath", message: ex.Message, target: inputPath); }

            if (!Directory.Exists(root))
                return McpResponse.Err(
                    code: "InputDirectoryNotFound",
                    message: "The Object Text input directory does not exist.",
                    hint: "Point inputPath at the exported text root.",
                    target: root);

            var ignored = ReadIgnorePatterns(args["ignore"]);
            if (!TextBatchOptions.TryParse(args, false, false, false, true, false, false, true, out TextBatchOptions options, out string optionError))
                return McpResponse.Err(code: "InvalidTextOperationOption", message: optionError);
            bool stopOnError = options.StopOnError;
            bool includeChildren = options.IncludeChildren;
            int skip = options.Skip;
            var files = EnumerateTextFiles(root, ignored, includeChildren);
            var results = new JArray();
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int valid = 0;
            int invalid = 0;
            int offset = Math.Min(skip, files.Count);
            bool stoppedOnError = false;

            for (int index = offset; index < files.Count; index++)
            {
                if (ct.IsCancellationRequested)
                {
                    var cancelled = new JObject
                    {
                        ["root"] = root,
                        ["filesChecked"] = results.Count,
                        ["validFiles"] = valid,
                        ["invalidFiles"] = invalid,
                        ["cancelled"] = true,
                        ["remaining"] = Math.Max(0, files.Count - results.Count),
                        ["results"] = results
                    };
                    return McpResponse.Ok(code: "Cancelled", result: cancelled);
                }

                string file = files[index];
                string relative = PathSafety.MakeRelative(root, file);
                string error = ValidateNativeTextFile(file, relative, identities);
                var item = new JObject
                {
                    ["file"] = relative,
                    ["kind"] = GetNativeTextKind(file),
                    ["valid"] = error == null
                };
                if (error == null) valid++;
                else
                {
                    invalid++;
                    item["message"] = error;
                }
                results.Add(item);
                if (error != null && stopOnError)
                {
                    stoppedOnError = true;
                    break;
                }
            }

            var result = new JObject
            {
                ["root"] = root,
                ["filesChecked"] = results.Count,
                ["filesAvailable"] = files.Count,
                ["skipped"] = offset,
                ["remaining"] = Math.Max(0, files.Count - offset - results.Count),
                ["validFiles"] = valid,
                ["invalidFiles"] = invalid,
                ["valid"] = invalid == 0,
                ["stoppedOnError"] = stoppedOnError,
                ["results"] = results
            };
            return invalid > 0
                ? McpResponse.Partial(null, "ObjectTextMemoryValidationPartial", result)
                : McpResponse.Ok(code: "ObjectTextMemoryValidationCompleted", result: result);
        }

        internal static string ListInMemory(JObject args, CancellationToken ct)
        {
            string inputPath = args["inputPath"]?.ToString() ?? args["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(inputPath))
                return McpResponse.Err(code: "InputPathRequired", message: "inputPath is required for in-memory Object Text listing.", hint: "Provide the root directory containing src and/or ref.");

            string root;
            try { root = Path.GetFullPath(inputPath); }
            catch (Exception ex) { return McpResponse.Err(code: "InvalidInputPath", message: ex.Message, target: inputPath); }
            if (!Directory.Exists(root))
                return McpResponse.Err(code: "InputDirectoryNotFound", message: "The Object Text input directory does not exist.", hint: "Point inputPath at the exported text root.", target: root);

            var ignored = ReadIgnorePatterns(args["ignore"]);
            if (!TextBatchOptions.TryParse(args, false, false, false, true, false, false, true, out TextBatchOptions options, out string optionError))
                return McpResponse.Err(code: "InvalidTextOperationOption", message: optionError);
            bool includeChildren = options.IncludeChildren;
            int skip = options.Skip;
            int limit = options.Limit;
            var files = EnumerateTextFiles(root, ignored, includeChildren);
            var results = new JArray();
            int objectFiles = 0;
            int visualFiles = 0;
            int metadataFiles = 0;
            int packageFiles = 0;
            long bytes = 0;
            int offset = Math.Min(skip, files.Count);
            int end = limit > 0 ? Math.Min(files.Count, offset + limit) : files.Count;

            for (int index = offset; index < end; index++)
            {
                if (ct.IsCancellationRequested)
                    return McpResponse.Ok(code: "Cancelled", result: new JObject
                    {
                        ["root"] = root,
                        ["filesChecked"] = files.Count,
                        ["filesAvailable"] = files.Count,
                        ["filesReturned"] = results.Count,
                        ["skipped"] = offset,
                        ["cancelled"] = true,
                        ["remaining"] = Math.Max(0, files.Count - end),
                        ["results"] = results
                    });

                string file = files[index];
                long length = new FileInfo(file).Length;
                bytes += length;
                string kind = GetNativeTextKind(file);
                if (kind == "Object") objectFiles++;
                else if (kind == "WebForm" || kind == "Report" || kind == "Xml") visualFiles++;
                else if (kind == "Package") packageFiles++;
                else metadataFiles++;

                var item = new JObject
                {
                    ["file"] = PathSafety.MakeRelative(root, file),
                    ["kind"] = kind,
                    ["bytes"] = length
                };
                if (kind == "Object")
                {
                    string header = TryReadNativeHeader(file);
                    if (!string.IsNullOrWhiteSpace(header))
                    {
                        string[] parts = header.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 2)
                        {
                            item["type"] = parts[0];
                            item["name"] = parts[1].Trim();
                        }
                    }
                }
                results.Add(item);
            }

            return McpResponse.Ok(code: "ObjectTextMemoryListCompleted", result: new JObject
            {
                ["root"] = root,
                ["filesChecked"] = files.Count,
                ["filesAvailable"] = files.Count,
                ["filesReturned"] = results.Count,
                ["skipped"] = offset,
                ["remaining"] = Math.Max(0, files.Count - end),
                ["objectFiles"] = objectFiles,
                ["visualFiles"] = visualFiles,
                ["metadataFiles"] = metadataFiles,
                ["packageFiles"] = packageFiles,
                ["bytes"] = bytes,
                ["results"] = results
            });
        }

        private static List<string> EnumerateTextFiles(string root, HashSet<string> ignored, bool includeChildren = true)
        {
            var files = new List<string>();
            SearchOption searchOption = includeChildren ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (string file in Directory.EnumerateFiles(root, "*", searchOption))
            {
                string relative = TextTreePath.NormalizePath(PathSafety.MakeRelative(root, file));
                if (ignored != null && ignored.Any(pattern => MatchesIgnore(relative, pattern))) continue;
                string extension = Path.GetExtension(file);
                if (string.Equals(extension, ".gx", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".gxtext", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".toml", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".opc", StringComparison.OrdinalIgnoreCase)
                    || IsManifestFile(file))
                    files.Add(file);
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
        }

        private static HashSet<string> ReadIgnorePatterns(JToken token)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (token is JArray array)
            {
                foreach (JToken value in array)
                {
                    string pattern = TextTreePath.NormalizePath(value?.ToString());
                    if (!string.IsNullOrWhiteSpace(pattern)) result.Add(pattern);
                }
            }
            else
            {
                string pattern = TextTreePath.NormalizePath(token?.ToString());
                if (!string.IsNullOrWhiteSpace(pattern)) result.Add(pattern);
            }
            return result;
        }

        private static bool MatchesIgnore(string relative, string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return false;
            if (string.Equals(relative, pattern, StringComparison.OrdinalIgnoreCase)) return true;
            return relative.StartsWith(pattern.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetNativeTextKind(string path)
        {
            string file = Path.GetFileName(path) ?? string.Empty;
            if (IsManifestFile(path)) return "Manifest";
            if (file.EndsWith(".gxtext", StringComparison.OrdinalIgnoreCase)) return "LegacyObjectText";
            if (file.EndsWith(".web.xml", StringComparison.OrdinalIgnoreCase)) return "WebForm";
            if (file.EndsWith(".report.xml", StringComparison.OrdinalIgnoreCase)) return "Report";
            if (string.Equals(Path.GetExtension(path), ".gx", StringComparison.OrdinalIgnoreCase)) return "Object";
            if (string.Equals(Path.GetExtension(path), ".opc", StringComparison.OrdinalIgnoreCase)) return "Package";
            if (file.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)) return "ModuleMetadata";
            if (file.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return "Readme";
            return "Xml";
        }

        private static string ValidateNativeTextFile(string path, string relative, HashSet<string> identities)
        {
            try
            {
                string text = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text)) return "File is empty.";

                if (IsManifestFile(path))
                    return ValidateManifestFile(path);

                string extension = Path.GetExtension(path);
                if (string.Equals(extension, ".gxtext", StringComparison.OrdinalIgnoreCase))
                    return null;
                if (string.Equals(extension, ".opc", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".toml", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase))
                    return null;

                if (string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase))
                {
                    using (var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        MaxCharactersFromEntities = 0
                    }))
                    {
                        XDocument.Load(reader, LoadOptions.PreserveWhitespace);
                    }
                    return null;
                }

                string header = TryReadNativeHeader(path, text);
                if (string.IsNullOrWhiteSpace(header)) return "Object file has no header.";
                string[] parts = header.Trim().Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                    return "Object header must contain type and name.";

                string identity = parts[0] + ":" + parts[1].Trim();
                if (TextTreePath.NormalizePath(relative).IndexOf("#tables/", StringComparison.OrdinalIgnoreCase) >= 0)
                    identity = "table-projection:" + TextTreePath.NormalizePath(relative);
                if (!identities.Add(identity)) return "Duplicate object identity: " + identity;
                return null;
            }
            catch (XmlException ex) { return "Invalid XML: " + ex.Message; }
            catch (Exception ex) { return ex.Message; }
        }

        private static bool IsManifestFile(string path)
        {
            string name = Path.GetFileName(path) ?? string.Empty;
            return string.Equals(name, ObjectTextService.ManifestFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, SdkTextTreeService.ManifestFileName, StringComparison.OrdinalIgnoreCase);
        }

        private static string ValidateManifestFile(string path)
        {
            JObject manifest;
            try { manifest = JObject.Parse(File.ReadAllText(path)); }
            catch (JsonException ex) { return "Invalid manifest JSON: " + ex.Message; }
            catch (Exception ex) { return "Manifest could not be read: " + ex.Message; }

            bool native = string.Equals(Path.GetFileName(path), SdkTextTreeService.ManifestFileName, StringComparison.OrdinalIgnoreCase);
            string expectedKind = native ? "GeneXusSdkTextTree" : ManifestKind;
            string kind = manifest["kind"]?.ToString();
            if (!string.IsNullOrWhiteSpace(kind) && !string.Equals(kind, expectedKind, StringComparison.OrdinalIgnoreCase))
                return "Unsupported manifest kind: " + kind;

            JArray objects = manifest["objects"] as JArray ?? manifest["files"] as JArray;
            if (objects == null) return "Manifest must contain an objects[] array.";

            string root = Path.GetDirectoryName(path);
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JToken token in objects)
            {
                JObject item = token as JObject;
                if (item == null) return "Every manifest entry must be an object.";
                string name = item["name"]?.ToString() ?? item["target"]?.ToString();
                string type = item["type"]?.ToString();
                string relative = item["file"]?.ToString() ?? item["path"]?.ToString();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(relative))
                    return "Every manifest entry requires name and file.";
                if (native && string.IsNullOrWhiteSpace(type))
                    return "Native manifest entries require type: " + name;

                string identity = (type ?? string.Empty) + ":" + name;
                if (item["tableProjection"]?.ToObject<bool?>() == true
                    || TextTreePath.NormalizePath(relative).IndexOf("#tables/", StringComparison.OrdinalIgnoreCase) >= 0)
                    identity = "table-projection:" + TextTreePath.NormalizePath(relative);
                if (!identities.Add(identity)) return "Duplicate manifest identity: " + identity;

                string referenceError = ValidateManifestFileReference(
                    root,
                    relative,
                    item["bytes"],
                    item["sha256"]);
                if (referenceError != null) return "Manifest entry '" + name + "': " + referenceError;

                if (item["companions"] is JArray companions)
                {
                    foreach (JObject companion in companions.OfType<JObject>())
                    {
                        string companionFile = companion["file"]?.ToString();
                        if (string.IsNullOrWhiteSpace(companionFile)) return "Companion entry has no file: " + name;
                        referenceError = ValidateManifestFileReference(root, companionFile, companion["bytes"], companion["sha256"]);
                        if (referenceError != null) return "Manifest companion for '" + name + "': " + referenceError;
                    }
                }
            }
            if (native && manifest["metadata"] is JArray metadata)
            {
                foreach (JObject item in metadata.OfType<JObject>())
                {
                    string module = item["module"]?.ToString() ?? "<unnamed>";
                    string relative = item["file"]?.ToString();
                    if (string.IsNullOrWhiteSpace(relative)) return "Metadata entry has no file: " + module;
                    string referenceError = ValidateManifestFileReference(root, relative, item["bytes"], item["sha256"]);
                    if (referenceError != null) return "Metadata entry '" + module + "': " + referenceError;
                }
            }
            if (native && manifest["packages"] is JArray packages)
            {
                foreach (JObject item in packages.OfType<JObject>())
                {
                    string module = item["module"]?.ToString() ?? "<unnamed>";
                    string relative = item["file"]?.ToString();
                    if (string.IsNullOrWhiteSpace(relative)) return "Package entry has no file: " + module;
                    string referenceError = ValidateManifestFileReference(root, relative, item["bytes"], item["sha256"]);
                    if (referenceError != null) return "Package entry '" + module + "': " + referenceError;
                }
            }
            return null;
        }

        private static string ValidateManifestFileReference(string root, string relative, JToken bytesToken, JToken hashToken)
        {
            if (!TextTreePath.TryResolveUnderRoot(root, relative, out string fullPath))
                return "file must be a relative path beneath the manifest root.";
            if (!File.Exists(fullPath)) return "file does not exist: " + TextTreePath.NormalizePath(relative);

            if (bytesToken != null && bytesToken.Type != JTokenType.Null)
            {
                long expectedBytes;
                try { expectedBytes = bytesToken.ToObject<long>(); }
                catch { return "bytes must be an integer: " + TextTreePath.NormalizePath(relative); }
                if (new FileInfo(fullPath).Length != expectedBytes)
                    return "byte count does not match: " + TextTreePath.NormalizePath(relative);
            }
            if (hashToken != null && hashToken.Type != JTokenType.Null)
            {
                string expectedHash = hashToken.ToString();
                if (!string.Equals(expectedHash, HashFile(fullPath), StringComparison.OrdinalIgnoreCase))
                    return "sha256 does not match: " + TextTreePath.NormalizePath(relative);
            }
            return null;
        }

        private static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string TryReadNativeHeader(string path)
        {
            try { return TryReadNativeHeader(path, File.ReadAllText(path)); }
            catch { return null; }
        }

        private static string TryReadNativeHeader(string path, string text)
        {
            if (string.IsNullOrWhiteSpace(text)
                || !string.Equals(Path.GetExtension(path), ".gx", StringComparison.OrdinalIgnoreCase)) return null;
            return text
                .Split(new[] { "\n" }, StringSplitOptions.None)
                .Select(line => (line ?? string.Empty).TrimEnd((char)13))
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        }

    }
}
