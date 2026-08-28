using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using System.Xml;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Deep domain engine for GeneXus object navigation analysis.
    /// Encapsulates .nvg.xml discovery, in-memory domain modeling (<see cref="NavigationReport"/>),
    /// on-disk snapshot caching, and direct SQL generation.
    /// </summary>
    public class NavigationService
    {
        private readonly KbService _kbService;

        public NavigationService(KbService kbService)
        {
            _kbService = kbService;
        }

        /// <summary>
        /// Loads and parses the navigation report for the specified target object into a strongly-typed domain model.
        /// </summary>
        public NavigationReport GetReport(string targetName)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                Logger.Info($"GetNavigation START: {targetName}");

                string nvgPath = FindNavigationFile(targetName);
                if (nvgPath == null)
                {
                    return NavigationReport.Error(targetName, $"Navigation report not found for '{targetName}'. Make sure the object is specified.");
                }

                Logger.Info($"GetNavigation file resolved for {targetName}: {nvgPath}");

                var xmlSettings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    IgnoreComments = true,
                    IgnoreWhitespace = true,
                    CloseInput = true
                };

                XDocument doc = LoadNavigationDocument(nvgPath, xmlSettings, targetName);

                var report = new NavigationReport
                {
                    TargetName = targetName,
                    Status = "OK"
                };

                foreach (var level in doc.Descendants("Level"))
                {
                    var levelObj = new NavigationLevel
                    {
                        Number = (int?)level.Element("LevelNumber"),
                        Type = level.Element("LevelType")?.Value,
                        Line = (int?)level.Element("LevelBeginRow"),
                        Index = level.Element("IndexName")?.Value
                    };

                    var baseTable = level.Element("BaseTable")?.Element("Table");
                    if (baseTable != null)
                    {
                        levelObj.BaseTable = baseTable.Element("TableName")?.Value;
                        levelObj.BaseTableDescription = baseTable.Element("Description")?.Value;
                    }

                    var orderEl = level.Element("Order");
                    if (orderEl != null)
                    {
                        foreach (var att in orderEl.Elements("Attribute"))
                        {
                            string attrName = att.Element("AttriName")?.Value;
                            if (!string.IsNullOrEmpty(attrName)) levelObj.Order.Add(attrName);
                        }
                    }

                    var optWhere = level.Element("OptimizedWhere");
                    bool hasOptimization = optWhere != null && optWhere.Elements().Any();
                    levelObj.IsOptimized = hasOptimization;

                    if (optWhere != null)
                    {
                        foreach (var f in optWhere.Elements())
                        {
                            var fObj = new NavigationFilter
                            {
                                Element = f.Name.LocalName,
                                Expression = f.Value?.Trim(),
                                Attribute = f.Element("Attribute")?.Value?.Trim(),
                                Op = f.Element("Operator")?.Value?.Trim(),
                                Value = f.Element("Value")?.Value?.Trim()
                            };
                            levelObj.Filters.Add(fObj);
                        }
                    }

                    report.Levels.Add(levelObj);
                }

                foreach (var w in doc.Descendants("Warning"))
                {
                    string msg = w.Element("Message")?.Value;
                    if (!string.IsNullOrEmpty(msg)) report.Warnings.Add(msg);
                }

                if (report.Levels.Count == 0)
                {
                    report.Status = "NoNavigationBlocks";
                    report.Hint = "Object has no For Each / data-bound navigation blocks. Use mode=summary or mode=data_context for variable/call analysis.";
                }

                Logger.Info($"GetNavigation SUCCESS: {targetName} in {sw.ElapsedMilliseconds}ms levels={report.Levels.Count}");
                return report;
            }
            catch (Exception ex)
            {
                Logger.Error($"GetNavigation ERROR for {targetName}: {ex.Message}");
                return NavigationReport.Error(targetName, CommandDispatcher.EscapeJsonString(ex.Message));
            }
        }

        /// <summary>
        /// JSON-string representation of navigation analysis for MCP protocol compatibility.
        /// </summary>
        public string GetNavigation(string targetName)
        {
            var report = GetReport(targetName);
            return report.ToJson().ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Wave-3: View Navigation / View Last Navigation parity with disk caching.
        /// </summary>
        public string View(string name, bool latest)
        {
            if (string.IsNullOrWhiteSpace(name))
                return new JObject { ["error"] = "Missing 'name'." }.ToString(Newtonsoft.Json.Formatting.None);

            string kbPath = null;
            try { kbPath = _kbService?.GetKbPath(); } catch { }
            string cacheDir = ResolveCacheDir(kbPath, name);

            if (latest && cacheDir != null && Directory.Exists(cacheDir))
            {
                try
                {
                    var files = Directory.GetFiles(cacheDir, "*.txt")
                        .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (files.Count > 0)
                    {
                        string latestPath = files[0];
                        string cachedContent = File.ReadAllText(latestPath);
                        return new JObject
                        {
                            ["name"] = name,
                            ["fromCache"] = true,
                            ["cachePath"] = latestPath,
                            ["navigation"] = TryParseEmbed(cachedContent)
                        }.ToString(Newtonsoft.Json.Formatting.None);
                    }
                }
                catch { }
            }

            var report = GetReport(name);
            if (report.IsError)
            {
                return new JObject { ["error"] = report.Message ?? "Navigation returned no payload.", ["code"] = "NoNavigation" }
                    .ToString(Newtonsoft.Json.Formatting.None);
            }

            string raw = report.ToJson().ToString(Newtonsoft.Json.Formatting.None);

            string savedPath = null;
            try
            {
                if (cacheDir != null)
                {
                    Directory.CreateDirectory(cacheDir);
                    string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
                    savedPath = Path.Combine(cacheDir, stamp + ".txt");
                    File.WriteAllText(savedPath, raw);
                }
            }
            catch { savedPath = null; }

            return new JObject
            {
                ["name"] = name,
                ["fromCache"] = false,
                ["cachePath"] = savedPath ?? string.Empty,
                ["navigation"] = report.ToJson()
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string ResolveCacheDir(string kbPath, string objectName)
        {
            if (string.IsNullOrWhiteSpace(kbPath) || string.IsNullOrWhiteSpace(objectName)) return null;
            try
            {
                string root = Directory.Exists(kbPath) ? kbPath : Path.GetDirectoryName(kbPath);
                if (string.IsNullOrEmpty(root)) return null;
                string sanitized = SanitizeName(objectName);
                return Path.Combine(root, ".gx", "navigation-cache", sanitized);
            }
            catch { return null; }
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unknown";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(invalid.Contains(c) ? '_' : c);
            return sb.ToString();
        }

        private static JToken TryParseEmbed(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new JObject();
            try { return JToken.Parse(raw); }
            catch { return JToken.FromObject(raw); }
        }

        private string FindNavigationFile(string targetName)
        {
            if (string.IsNullOrWhiteSpace(targetName)) return null;
            var kb = _kbService?.GetKB();
            if (kb == null) return null;

            string kbPath = kb.Location;
            if (File.Exists(kbPath))
            {
                kbPath = Path.GetDirectoryName(kbPath);
            }

            if (string.IsNullOrWhiteSpace(kbPath) || !Directory.Exists(kbPath))
            {
                return null;
            }

            var specFolders = Directory.EnumerateDirectories(kbPath, "GXSPC*", SearchOption.TopDirectoryOnly)
                                       .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase);

            foreach (var specFolder in specFolders)
            {
                foreach (var genFolder in Directory.EnumerateDirectories(specFolder, "GEN*", SearchOption.TopDirectoryOnly))
                {
                    string genPath = Path.Combine(genFolder, "NVG");
                    if (!Directory.Exists(genPath))
                    {
                        continue;
                    }

                    string fullPath = Path.Combine(genPath, targetName + ".xml");
                    if (File.Exists(fullPath)) return fullPath;
                }
            }

            return null;
        }

        private static XDocument LoadNavigationDocument(string nvgPath, XmlReaderSettings xmlSettings, string targetName)
        {
            try
            {
                using (var stream = new FileStream(nvgPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = XmlReader.Create(stream, xmlSettings))
                {
                    return XDocument.Load(reader, LoadOptions.None);
                }
            }
            catch (XmlException ex) when (LooksLikeLegacySingleByteEncoding(ex))
            {
                Logger.Info($"GetNavigation fallback decoding for {targetName}: retrying as Windows-1252.");
                string xmlText = Encoding.GetEncoding(1252).GetString(File.ReadAllBytes(nvgPath));
                using (var stringReader = new StringReader(xmlText))
                using (var reader = XmlReader.Create(stringReader, xmlSettings))
                {
                    return XDocument.Load(reader, LoadOptions.None);
                }
            }
        }

        private static bool LooksLikeLegacySingleByteEncoding(XmlException ex)
        {
            if (ex == null || string.IsNullOrWhiteSpace(ex.Message))
            {
                return false;
            }

            string message = ex.Message;
            return message.IndexOf("codifica", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("encoding", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
