using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Utils;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// `genexus_profile` — ingests a GeneXus runtime profile XML and surfaces
    /// hot-spot summaries (totalMs by object, top-N hotspots, drill-into one
    /// target). Defensive parser: the GX profile XML format isn't documented
    /// publicly and varies across versions, so the parser falls back to a
    /// generic "any element with name+timing attributes" probe and reports
    /// parserWarnings when the schema doesn't match a known shape.
    /// </summary>
    public class ProfileService
    {
        // Attribute names that look like the "object/method being profiled".
        private static readonly string[] NameAttrs = { "name", "object", "method", "class", "objectName", "fullName" };

        // Attribute names that look like cumulative timing.
        private static readonly string[] TotalTimeAttrs = { "totalTime", "totalMs", "cumulative", "cumulativeTime", "elapsed", "time" };

        // Attribute names that look like exclusive / self timing.
        private static readonly string[] SelfTimeAttrs = { "selfTime", "selfMs", "exclusive", "exclusiveTime" };

        // Attribute names that look like call/sample counts.
        private static readonly string[] CountAttrs = { "callCount", "calls", "sampleCount", "samples", "hits", "count" };

        private readonly IUserFilePathPolicy _filePathPolicy;

        public ProfileService()
            : this(new UserFilePathPolicy(() => Environment.GetEnvironmentVariable("GX_KB_PATH")))
        {
        }

        internal ProfileService(IUserFilePathPolicy filePathPolicy)
        {
            _filePathPolicy = filePathPolicy ?? throw new ArgumentNullException(nameof(filePathPolicy));
        }

        public string Run(JObject args)
        {
            try
            {
                string action = args?["action"]?.ToString()?.ToLowerInvariant();
                if (string.IsNullOrEmpty(action))
                    return Err("InvalidAction", "action is required (analyze|hotspots|correlate).");
                string requestedPath = args?["path"]?.ToString();
                if (string.IsNullOrWhiteSpace(requestedPath))
                    return Err("MissingPath", "path is required (filesystem path to a GeneXus profile XML).");
                string pathError;
                string path;
                if (!_filePathPolicy.TryResolveReadPath(requestedPath, out path, out pathError))
                    return Err("PathOutsideAllowedRoots", pathError + " Use GXMCP_EXTERNAL_IO_ROOT for a deliberate profile exchange directory.");
                if (!File.Exists(path))
                    return Err("FileNotFound", "path does not exist: " + requestedPath);

                XDocument doc;
                try { doc = XDocument.Load(path); }
                catch (Exception ex) { return Err("InvalidXml", "could not parse XML: " + ex.Message); }

                var warnings = new List<string>();
                var aggregated = AggregateTiming(doc, warnings);

                switch (action)
                {
                    case "analyze":
                        return BuildAnalyzeEnvelope(aggregated, warnings);
                    case "hotspots":
                        int top = args?["top"]?.ToObject<int?>() ?? 10;
                        if (top < 1) top = 1;
                        if (top > 50) top = 50;
                        return BuildHotspotsEnvelope(aggregated, top, warnings);
                    case "correlate":
                        string target = args?["target"]?.ToString();
                        if (string.IsNullOrEmpty(target))
                            return Err("MissingTarget", "correlate requires target.");
                        return BuildCorrelateEnvelope(aggregated, target, warnings);
                    default:
                        return Err("InvalidAction", "Unknown action '" + action + "'. Expected one of: analyze, hotspots, correlate.");
                }
            }
            catch (Exception ex)
            {
                return Err("Unexpected", ex.Message);
            }
        }

        // ---- aggregation (pure, testable) -----------------------------------

        internal class TimingRow
        {
            public string Name;
            public string Type;
            public int CallCount;
            public double TotalMs;
            public double SelfMs;
        }

        internal static IList<TimingRow> AggregateTiming(XDocument doc, IList<string> warnings)
        {
            var byName = new Dictionary<string, TimingRow>(StringComparer.OrdinalIgnoreCase);
            int elementsMatched = 0;
            int elementsScanned = 0;

            foreach (var el in doc.Descendants())
            {
                elementsScanned++;
                string name = ReadFirstAttr(el, NameAttrs);
                if (string.IsNullOrEmpty(name)) continue;

                double? total = ReadFirstNumAttr(el, TotalTimeAttrs);
                if (!total.HasValue) continue;

                elementsMatched++;
                if (!byName.TryGetValue(name, out var row))
                {
                    row = new TimingRow
                    {
                        Name = name,
                        Type = ReadFirstAttr(el, new[] { "type", "kind", "objectType" }),
                    };
                    byName[name] = row;
                }
                row.TotalMs += total.Value;
                double? self = ReadFirstNumAttr(el, SelfTimeAttrs);
                if (self.HasValue) row.SelfMs += self.Value;
                int? cc = ReadFirstIntAttr(el, CountAttrs);
                row.CallCount += cc ?? 1;
            }

            if (elementsScanned == 0)
                warnings.Add("XML had no descendant elements.");
            else if (elementsMatched == 0)
                warnings.Add("no elements with name+timing attributes found");
            else if (elementsMatched < 3)
                warnings.Add("only " + elementsMatched + " matching element(s) — schema may not be fully recognized");

            return byName.Values.OrderByDescending(r => r.TotalMs).ToList();
        }

        private static string ReadFirstAttr(XElement el, IEnumerable<string> attrNames)
        {
            foreach (var n in attrNames)
            {
                var a = el.Attribute(n) ?? el.Attributes().FirstOrDefault(x => string.Equals(x.Name.LocalName, n, StringComparison.OrdinalIgnoreCase));
                if (a != null && !string.IsNullOrEmpty(a.Value)) return a.Value;
            }
            return null;
        }

        private static double? ReadFirstNumAttr(XElement el, IEnumerable<string> attrNames)
        {
            string raw = ReadFirstAttr(el, attrNames);
            if (raw == null) return null;
            if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                return d;
            return null;
        }

        private static int? ReadFirstIntAttr(XElement el, IEnumerable<string> attrNames)
        {
            string raw = ReadFirstAttr(el, attrNames);
            if (raw == null) return null;
            if (int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i))
                return i;
            return null;
        }

        // ---- envelope builders ----------------------------------------------

        private static string BuildAnalyzeEnvelope(IList<TimingRow> rows, IList<string> warnings)
        {
            double total = rows.Sum(r => r.TotalMs);
            int sampleCount = rows.Sum(r => r.CallCount);
            var byObject = new JArray();
            foreach (var r in rows)
            {
                byObject.Add(RowToJson(r, total));
            }
            var payload = new JObject
            {
                ["totalSampleMs"] = total,
                ["sampleCount"] = sampleCount,
                ["byObject"] = byObject
            };
            if (warnings.Count > 0)
            {
                payload["note"] = "Schema may not be fully recognized — see parserWarnings.";
                payload["parserWarnings"] = new JArray(warnings);
            }
            return McpResponse.Ok(code: "ProfileAnalyzed", result: payload);
        }

        private static string BuildHotspotsEnvelope(IList<TimingRow> rows, int top, IList<string> warnings)
        {
            double total = rows.Sum(r => r.TotalMs);
            var hotspots = new JArray();
            foreach (var r in rows.Take(top))
            {
                hotspots.Add(RowToJson(r, total));
            }
            var payload = new JObject
            {
                ["top"] = top,
                ["totalSampleMs"] = total,
                ["hotspots"] = hotspots
            };
            if (warnings.Count > 0) payload["parserWarnings"] = new JArray(warnings);
            return McpResponse.Ok(code: "ProfileHotspots", result: payload);
        }

        private static string BuildCorrelateEnvelope(IList<TimingRow> rows, string target, IList<string> warnings)
        {
            double total = rows.Sum(r => r.TotalMs);
            var matches = rows.Where(r => r.Name.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (matches.Count == 0)
            {
                return McpResponse.Ok(
                    target: target,
                    code: "ProfileCorrelated",
                    result: new JObject
                    {
                        ["matches"] = new JArray(),
                        ["note"] = "target '" + target + "' not found in profile.",
                        ["parserWarnings"] = new JArray(warnings)
                    });
            }
            var arr = new JArray();
            foreach (var r in matches) arr.Add(RowToJson(r, total));
            return McpResponse.Ok(
                target: target,
                code: "ProfileCorrelated",
                result: new JObject
                {
                    ["matches"] = arr,
                    ["totalSampleMs"] = total
                });
        }

        private static JObject RowToJson(TimingRow r, double total)
        {
            var j = new JObject
            {
                ["name"] = r.Name,
                ["callCount"] = r.CallCount,
                ["totalMs"] = r.TotalMs,
                ["percent"] = total > 0 ? Math.Round(r.TotalMs / total * 100.0, 2) : 0.0
            };
            if (!string.IsNullOrEmpty(r.Type)) j["type"] = r.Type;
            if (r.SelfMs > 0) j["selfMs"] = r.SelfMs;
            return j;
        }

        // ---- error envelope --------------------------------------------------

        private static string Err(string code, string message)
            => McpResponse.Err(
                code: code,
                message: message,
                nextSteps: new JArray {
                    McpResponse.NextStep("genexus_profile", new JObject { ["action"] = "analyze", ["path"] = "<path to profile XML>" }, "Retry with a valid profile XML path.")
                });
    }
}
