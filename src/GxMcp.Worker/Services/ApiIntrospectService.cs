using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// `genexus_api` — introspect the REST endpoints exposed by Procedures whose
    /// CALL_PROTOCOL property is set to HTTP. Supports list / describe / snapshot /
    /// diff_baseline. Tests cover the pure-data layer (BuildEndpointFromRules,
    /// DiffEndpoints, SnapshotWrite) without needing a live KB; the Run() entry
    /// point glues those layers to the live IndexCacheService / ObjectService.
    /// </summary>
    public class ApiIntrospectService
    {
        private readonly KbService _kbService;
        private readonly ObjectService _objectService;
        private readonly IndexCacheService _indexCacheService;

        // CALL_PROTOCOL property regex applied to the Rules part as a fallback when
        // the typed property isn't reachable. Both `Call Protocol: HTTP;` (Rules
        // declaration syntax) and the descriptor name match.
        private static readonly Regex CallProtocolHttpRegex = new Regex(
            @"Call\s+Protocol\s*:\s*HTTP\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Per-parm regex on the parm rule string. GeneXus parm rule shape:
        //   parm(in:&InVar, out:&OutVar, inout:&Both);
        // The descriptor's `direction:` token disambiguates input vs output.
        private static readonly Regex ParmTokenRegex = new Regex(
            @"(?<dir>in|out|inout)\s*:\s*&(?<name>[A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public ApiIntrospectService(KbService kbService, ObjectService objectService, IndexCacheService indexCacheService)
        {
            _kbService = kbService;
            _objectService = objectService;
            _indexCacheService = indexCacheService;
        }

        public string Run(JObject args)
        {
            try
            {
                string action = args?["action"]?.ToString()?.ToLowerInvariant();
                // Content-first: a bare call (no action) enumerates the KB's APIs —
                // live data, not an error. `list` is read-only, so it's the safe default.
                if (string.IsNullOrEmpty(action))
                    action = "list";

                switch (action)
                {
                    case "list":
                        return DoList(args?["pathPrefix"]?.ToString());
                    case "describe":
                        return DoDescribe(args?["target"]?.ToString());
                    case "snapshot":
                        return DoSnapshot(args?["name"]?.ToString());
                    case "diff_baseline":
                        return DoDiffBaseline(args?["baseline"]?.ToString());
                    case "export_openapi":
                        return DoExportOpenApi(args?["title"]?.ToString(), args?["version"]?.ToString(), args?["pathPrefix"]?.ToString());
                    case "import_openapi":
                        return DoImportOpenApi(args?["spec"]?.ToString() ?? args?["content"]?.ToString());
                    default:
                        return Err("InvalidAction", $"Unknown action '{action}'. Use list|describe|diff_baseline|snapshot|export_openapi|import_openapi.");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[ApiIntrospectService] " + ex.Message);
                return Err("InternalError", ex.Message);
            }
        }

        // ---- list -----------------------------------------------------------

        private string DoList(string pathPrefix)
        {
            var endpoints = EnumerateHttpEndpoints(pathPrefix);
            var arr = new JArray();
            foreach (var ep in endpoints)
                arr.Add(EndpointToJson(ep, includeSchema: false));

            return McpResponse.Ok(
                code: "ApiIntrospectCompleted",
                result: new JObject { ["endpoints"] = arr, ["count"] = arr.Count });
        }

        // ---- describe -------------------------------------------------------

        private string DoDescribe(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return Err("InvalidTarget", "target (procedure name) is required for action=describe.");

            // Find candidate procedure via index.
            var idx = _indexCacheService?.GetIndex();
            SearchIndex.IndexEntry entry = null;
            if (idx?.Objects != null)
            {
                entry = idx.Objects.Values.FirstOrDefault(e =>
                    string.Equals(e.Type, "Procedure", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Name, target, StringComparison.OrdinalIgnoreCase));
            }
            if (entry == null)
                return Err("NotFound", $"No Procedure named '{target}' in the index.");

            // Pull Rules part to confirm HTTP + extract URL.
            string rulesSrc = TryReadPart(target, "Rules");
            if (!IsHttpProcedure(rulesSrc))
                return Err("NotHttpProcedure", $"Procedure '{target}' does not declare Call Protocol: HTTP.");

            // Build endpoint with schemas inlined.
            var ep = BuildEndpointFromRules(
                name: entry.Name,
                parmRule: entry.ParmRule,
                rulesSource: rulesSrc,
                path: entry.ParentFolderPath ?? entry.ParentPath ?? entry.Path,
                lastUpdate: entry.LastUpdate);

            var sdtRefs = ExtractSdtReferencesFromVariables(target);

            var j = EndpointToJson(ep, includeSchema: true);
            j["sdtsReferenced"] = new JArray(sdtRefs);
            j["roles"] = ExtractRoles(rulesSrc);
            j["gamRequired"] = ContainsGamMarker(rulesSrc);
            return McpResponse.Ok(
                target: target,
                code: "ApiIntrospectCompleted",
                result: j);
        }

        // ---- snapshot -------------------------------------------------------

        private string DoSnapshot(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Err("InvalidName", "name is required for action=snapshot.");
            if (!IsSafeBaselineName(name))
                return Err("InvalidName", "name must match [A-Za-z0-9._-]{1,64}.");

            string kbPath = _kbService?.GetKbPath();
            if (string.IsNullOrEmpty(kbPath))
                return Err("NoKbOpen", "No KB is currently open.");

            var endpoints = EnumerateHttpEndpoints(null);
            var arr = new JArray();
            foreach (var ep in endpoints)
                arr.Add(EndpointToJson(ep, includeSchema: false));

            var payload = new JObject
            {
                ["version"] = 1,
                ["createdAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["kbPath"] = kbPath,
                ["endpoints"] = arr
            };

            string dir = Path.Combine(kbPath, ".gx", "api-baselines");
            Directory.CreateDirectory(dir);
            string outPath = Path.Combine(dir, name + ".json");
            File.WriteAllText(outPath, payload.ToString(Formatting.Indented));

            return McpResponse.Ok(
                code: "ApiSnapshotWritten",
                result: new JObject
                {
                    ["written"] = true,
                    ["path"] = outPath,
                    ["endpointCount"] = arr.Count
                });
        }

        // ---- diff_baseline --------------------------------------------------

        private string DoDiffBaseline(string baselineArg)
        {
            if (string.IsNullOrWhiteSpace(baselineArg))
                return Err("InvalidBaseline", "baseline is required (path or baseline name).");

            string baselinePath = ResolveBaselinePath(baselineArg);
            if (baselinePath == null || !File.Exists(baselinePath))
                return Err("BaselineNotFound", $"Baseline file not found: '{baselineArg}'. Looked at: {baselinePath ?? "<unresolved>"}");

            JObject baselineDoc;
            try { baselineDoc = JObject.Parse(File.ReadAllText(baselinePath)); }
            catch (Exception ex) { return Err("BaselineParseError", "Failed to parse baseline JSON: " + ex.Message); }

            var baselineEndpoints = (baselineDoc["endpoints"] as JArray) ?? new JArray();
            var currentEndpoints = new JArray();
            foreach (var ep in EnumerateHttpEndpoints(null))
                currentEndpoints.Add(EndpointToJson(ep, includeSchema: false));

            var diff = DiffEndpoints(baselineEndpoints, currentEndpoints);
            diff["baselinePath"] = baselinePath;
            return McpResponse.Ok(
                code: "ApiDiffCompleted",
                result: diff);
        }

        private string DoExportOpenApi(string title, string version, string pathPrefix)
        {
            var endpoints = EnumerateHttpEndpoints(pathPrefix).ToList();
            var spec = ApiOpenApiService.ExportOpenApi(endpoints, title, version);
            return McpResponse.Ok(
                code: "ApiOpenApiExported",
                result: new JObject
                {
                    ["openapi"] = spec,
                    ["endpointCount"] = endpoints.Count
                });
        }

        private string DoImportOpenApi(string specContent)
        {
            if (string.IsNullOrWhiteSpace(specContent))
                return Err("InvalidSpec", "spec content (OpenAPI 3 JSON) is required for action=import_openapi.");

            var blueprint = ApiOpenApiService.ImportOpenApi(specContent);
            if (!blueprint.Success)
                return Err("OpenApiParseError", blueprint.ErrorMessage ?? "Failed to parse OpenAPI specification.");

            return McpResponse.Ok(
                code: "ApiOpenApiImported",
                result: JObject.FromObject(blueprint));
        }

        private string ResolveBaselinePath(string baselineArg)
        {
            // Absolute path wins.
            try
            {
                if (Path.IsPathRooted(baselineArg) && File.Exists(baselineArg))
                    return baselineArg;
            }
            catch { /* invalid chars → fall through */ }

            string kbPath = _kbService?.GetKbPath();
            if (string.IsNullOrEmpty(kbPath)) return null;

            // <kb>/.gx/api-baselines/<name>.json
            if (IsSafeBaselineName(baselineArg))
            {
                string candidate = Path.Combine(kbPath, ".gx", "api-baselines", baselineArg + ".json");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        // ---- core enumeration ----------------------------------------------

        private IEnumerable<HttpEndpoint> EnumerateHttpEndpoints(string pathPrefix)
        {
            var idx = _indexCacheService?.GetIndex();
            if (idx?.Objects == null) yield break;

            foreach (var entry in idx.Objects.Values)
            {
                if (!string.Equals(entry.Type, "Procedure", StringComparison.OrdinalIgnoreCase)) continue;

                string folder = entry.ParentFolderPath ?? entry.ParentPath ?? "";
                if (!string.IsNullOrEmpty(pathPrefix) && !folder.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Cheapest gate first: source snippet usually carries Call Protocol rule.
                string rulesSrc = null;
                bool isHttp = false;
                if (!string.IsNullOrEmpty(entry.SourceSnippet) && CallProtocolHttpRegex.IsMatch(entry.SourceSnippet))
                    isHttp = true;
                if (!isHttp)
                {
                    rulesSrc = TryReadPart(entry.Name, "Rules");
                    isHttp = IsHttpProcedure(rulesSrc);
                }
                if (!isHttp) continue;

                yield return BuildEndpointFromRules(
                    name: entry.Name,
                    parmRule: entry.ParmRule,
                    rulesSource: rulesSrc, // may be null when we trusted SourceSnippet
                    path: folder,
                    lastUpdate: entry.LastUpdate);
            }
        }

        private string TryReadPart(string name, string part)
        {
            try
            {
                if (_objectService == null) return null;
                string json = _objectService.ReadObjectSourceParts(name, new[] { part }, "Procedure");
                var jo = JObject.Parse(json);
                return jo["parts"]?[part]?.ToString();
            }
            catch { return null; }
        }

        // ---- pure-data helpers (testable) ----------------------------------

        internal static bool IsHttpProcedure(string rulesSource)
        {
            if (string.IsNullOrEmpty(rulesSource)) return false;
            return CallProtocolHttpRegex.IsMatch(rulesSource);
        }

        internal static bool IsSafeBaselineName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64) return false;
            foreach (var c in name)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-')) return false;
            }
            return name != "." && name != "..";
        }

        public class HttpEndpoint
        {
            public string Name;
            public string HttpMethod = "POST"; // GeneXus REST default; overridden by HttpMethod rule.
            public string Url;
            public string Path;
            public string Protocol = "HTTP";
            public string CallMode = "REST";
            public DateTime LastUpdate;
            public List<Parm> Parms = new List<Parm>();
        }

        public class Parm
        {
            public string Name;
            public string Direction; // in | out | inout
            public string Type;      // raw type literal (e.g. "Numeric(8.2)")
            public bool IsCollection;
        }

        internal static HttpEndpoint BuildEndpointFromRules(
            string name, string parmRule, string rulesSource, string path, DateTime lastUpdate)
        {
            var ep = new HttpEndpoint
            {
                Name = name,
                Path = path,
                LastUpdate = lastUpdate,
                Url = "/rest/" + name
            };

            // HttpMethod rule (e.g. `HttpMethod: GET;`).
            if (!string.IsNullOrEmpty(rulesSource))
            {
                var m = Regex.Match(rulesSource,
                    @"HttpMethod\s*:\s*(?<m>GET|POST|PUT|DELETE|PATCH)\b",
                    RegexOptions.IgnoreCase);
                if (m.Success) ep.HttpMethod = m.Groups["m"].Value.ToUpperInvariant();
            }

            // parm directions from descriptor (entry.ParmRule). Fallback: scan rules
            // for `parm(...)` declaration.
            string parmText = !string.IsNullOrEmpty(parmRule)
                ? parmRule
                : (!string.IsNullOrEmpty(rulesSource)
                    ? ExtractParmDeclaration(rulesSource)
                    : null);

            if (!string.IsNullOrEmpty(parmText))
            {
                foreach (Match m in ParmTokenRegex.Matches(parmText))
                {
                    var p = new Parm
                    {
                        Name = m.Groups["name"].Value,
                        Direction = m.Groups["dir"].Value.ToLowerInvariant()
                    };
                    ep.Parms.Add(p);
                }

                // Variables block in Rules may carry types — best-effort association.
                if (!string.IsNullOrEmpty(rulesSource))
                    EnrichParmTypesFromRules(ep.Parms, rulesSource);
            }

            return ep;
        }

        internal static string ExtractParmDeclaration(string rulesSource)
        {
            var m = Regex.Match(rulesSource, @"parm\s*\(([^)]*)\)\s*;", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static void EnrichParmTypesFromRules(List<Parm> parms, string rulesSource)
        {
            // Best-effort: look for `&Name : Type` style annotations. Not all KBs
            // carry these in Rules; describe() supplements with Variables part.
            foreach (var p in parms)
            {
                var pattern = @"&" + Regex.Escape(p.Name) + @"\s*:\s*(?<t>[A-Za-z][A-Za-z0-9.()\s,]*)";
                var m = Regex.Match(rulesSource, pattern);
                if (m.Success) p.Type = m.Groups["t"].Value.Trim().TrimEnd(';');
            }
        }

        private List<string> ExtractSdtReferencesFromVariables(string procName)
        {
            var sdtNames = new List<string>();
            string varsSrc = TryReadPart(procName, "Variables");
            if (string.IsNullOrEmpty(varsSrc)) return sdtNames;

            // SDT typenames appear in variables as basedOn=SDT:Name or Type=Name (when
            // the type resolves to an SDT). Pull whatever looks like an identifier
            // following 'SDT:' tokens.
            foreach (Match m in Regex.Matches(varsSrc, @"SDT[:\s=]+(?<n>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase))
            {
                string n = m.Groups["n"].Value;
                if (!sdtNames.Contains(n, StringComparer.OrdinalIgnoreCase))
                    sdtNames.Add(n);
            }
            return sdtNames;
        }

        private static JArray ExtractRoles(string rulesSource)
        {
            var arr = new JArray();
            if (string.IsNullOrEmpty(rulesSource)) return arr;
            foreach (Match m in Regex.Matches(rulesSource,
                @"AllowedRoles?\s*:\s*['""]?(?<r>[A-Za-z0-9_,\s]+)['""]?",
                RegexOptions.IgnoreCase))
            {
                foreach (var role in m.Groups["r"].Value.Split(','))
                {
                    var r = role.Trim();
                    if (!string.IsNullOrEmpty(r)) arr.Add(r);
                }
            }
            return arr;
        }

        private static bool ContainsGamMarker(string rulesSource)
        {
            if (string.IsNullOrEmpty(rulesSource)) return false;
            return Regex.IsMatch(rulesSource, @"\bGAM\b|\bIntegratedSecurityLevel\b", RegexOptions.IgnoreCase);
        }

        // ---- json projection ------------------------------------------------

        internal static JObject EndpointToJson(HttpEndpoint ep, bool includeSchema)
        {
            var parms = new JArray();
            foreach (var p in ep.Parms)
            {
                parms.Add(new JObject
                {
                    ["name"] = p.Name,
                    ["direction"] = p.Direction,
                    ["type"] = p.Type,
                    ["isCollection"] = p.IsCollection
                });
            }
            var j = new JObject
            {
                ["name"] = ep.Name,
                ["httpMethod"] = ep.HttpMethod,
                ["url"] = ep.Url,
                ["parms"] = parms,
                ["protocol"] = ep.Protocol,
                ["callMode"] = ep.CallMode,
                ["path"] = ep.Path,
                ["lastUpdate"] = ep.LastUpdate == DateTime.MinValue ? null : ep.LastUpdate.ToUniversalTime().ToString("o")
            };
            if (includeSchema)
            {
                j["requestSchema"] = BuildRequestSchema(ep);
                j["responseSchema"] = BuildResponseSchema(ep);
            }
            return j;
        }

        internal static JObject BuildRequestSchema(HttpEndpoint ep)
        {
            var props = new JObject();
            var required = new JArray();
            foreach (var p in ep.Parms)
            {
                if (p.Direction == "in" || p.Direction == "inout")
                {
                    props[p.Name] = new JObject
                    {
                        ["type"] = MapToJsonType(p.Type),
                        ["genexusType"] = p.Type,
                        ["isCollection"] = p.IsCollection
                    };
                    required.Add(p.Name);
                }
            }
            return new JObject { ["type"] = "object", ["properties"] = props, ["required"] = required };
        }

        internal static JObject BuildResponseSchema(HttpEndpoint ep)
        {
            var props = new JObject();
            foreach (var p in ep.Parms)
            {
                if (p.Direction == "out" || p.Direction == "inout")
                {
                    props[p.Name] = new JObject
                    {
                        ["type"] = MapToJsonType(p.Type),
                        ["genexusType"] = p.Type,
                        ["isCollection"] = p.IsCollection
                    };
                }
            }
            return new JObject { ["type"] = "object", ["properties"] = props };
        }

        internal static string MapToJsonType(string gxType)
        {
            if (string.IsNullOrEmpty(gxType)) return "string";
            var t = gxType.Trim();
            if (t.StartsWith("Numeric", StringComparison.OrdinalIgnoreCase)) return "number";
            if (t.StartsWith("Boolean", StringComparison.OrdinalIgnoreCase)) return "boolean";
            if (t.StartsWith("Date", StringComparison.OrdinalIgnoreCase)) return "string";
            if (t.StartsWith("Character", StringComparison.OrdinalIgnoreCase) || t.StartsWith("VarChar", StringComparison.OrdinalIgnoreCase)) return "string";
            return "string";
        }

        // ---- diff core (pure, testable) ------------------------------------

        /// <summary>
        /// Compute added/removed/changed sets between two endpoint arrays.
        /// Breaking detection:
        ///   - param removed
        ///   - input required→optional flipped on input dir (we treat any in-parm as required)
        ///   - output param removed
        ///   - httpMethod changed
        ///   - type narrowed: Numeric(M.D) → Numeric(M'.D') with M' &lt; M
        /// Compat:
        ///   - param added (any direction)
        ///   - type widened: Numeric(M.D) → Numeric(M'.D') with M' >= M (and !=)
        /// </summary>
        internal static JObject DiffEndpoints(JArray baseline, JArray current)
        {
            var baselineByName = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var currentByName = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in baseline.OfType<JObject>())
            {
                string n = t["name"]?.ToString();
                if (!string.IsNullOrEmpty(n)) baselineByName[n] = t;
            }
            foreach (var t in current.OfType<JObject>())
            {
                string n = t["name"]?.ToString();
                if (!string.IsNullOrEmpty(n)) currentByName[n] = t;
            }

            var added = new JArray();
            var removed = new JArray();
            var changed = new JArray();

            foreach (var kv in currentByName)
            {
                if (!baselineByName.ContainsKey(kv.Key))
                    added.Add(new JObject { ["name"] = kv.Key, ["httpMethod"] = kv.Value["httpMethod"] });
            }
            foreach (var kv in baselineByName)
            {
                if (!currentByName.ContainsKey(kv.Key))
                    removed.Add(new JObject { ["name"] = kv.Key, ["httpMethod"] = kv.Value["httpMethod"] });
            }
            foreach (var kv in currentByName)
            {
                if (!baselineByName.TryGetValue(kv.Key, out var b)) continue;
                var c = kv.Value;
                var breaks = new JArray();
                var compat = new JArray();

                // httpMethod
                string oldM = b["httpMethod"]?.ToString();
                string newM = c["httpMethod"]?.ToString();
                if (!string.Equals(oldM, newM, StringComparison.OrdinalIgnoreCase))
                    breaks.Add($"httpMethod changed: {oldM} → {newM}");

                // parms
                var baseParms = (b["parms"] as JArray)?.OfType<JObject>().ToDictionary(p => p["name"]?.ToString() ?? "", p => p, StringComparer.OrdinalIgnoreCase)
                                ?? new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
                var curParms = (c["parms"] as JArray)?.OfType<JObject>().ToDictionary(p => p["name"]?.ToString() ?? "", p => p, StringComparer.OrdinalIgnoreCase)
                               ?? new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

                foreach (var bp in baseParms)
                {
                    if (string.IsNullOrEmpty(bp.Key)) continue;
                    if (!curParms.ContainsKey(bp.Key))
                    {
                        breaks.Add($"param removed: {bp.Key}");
                    }
                }
                foreach (var cp in curParms)
                {
                    if (string.IsNullOrEmpty(cp.Key)) continue;
                    if (!baseParms.ContainsKey(cp.Key))
                    {
                        compat.Add($"param added: {cp.Key} ({cp.Value["direction"]})");
                        continue;
                    }
                    var bp = baseParms[cp.Key];
                    string oldT = bp["type"]?.ToString();
                    string newT = cp.Value["type"]?.ToString();
                    if (!string.Equals(oldT, newT, StringComparison.Ordinal))
                    {
                        var cmp = CompareNumericType(oldT, newT);
                        if (cmp < 0) breaks.Add($"param {cp.Key} narrowed: {oldT} → {newT}");
                        else if (cmp > 0) compat.Add($"param {cp.Key} widened: {oldT} → {newT}");
                        else breaks.Add($"param {cp.Key} type changed: {oldT} → {newT}");
                    }
                    string oldDir = bp["direction"]?.ToString();
                    string newDir = cp.Value["direction"]?.ToString();
                    if (!string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase))
                        breaks.Add($"param {cp.Key} direction changed: {oldDir} → {newDir}");
                }

                if (breaks.Count > 0 || compat.Count > 0)
                {
                    changed.Add(new JObject
                    {
                        ["name"] = kv.Key,
                        ["breaking"] = breaks,
                        ["compat"] = compat
                    });
                }
            }

            return new JObject
            {
                ["added"] = added,
                ["removed"] = removed,
                ["changed"] = changed,
                ["summary"] = new JObject
                {
                    ["addedCount"] = added.Count,
                    ["removedCount"] = removed.Count,
                    ["changedCount"] = changed.Count,
                    ["hasBreakingChanges"] = removed.Count > 0 ||
                        changed.OfType<JObject>().Any(x => (x["breaking"] as JArray)?.Count > 0)
                }
            };
        }

        // Returns: <0 if newer is narrower (breaking), >0 if newer is wider (compat),
        // 0 when types are non-numeric or otherwise incomparable (caller treats 0 as
        // a generic "type changed" — still breaking unless both sides equal).
        internal static int CompareNumericType(string oldT, string newT)
        {
            if (string.IsNullOrEmpty(oldT) || string.IsNullOrEmpty(newT)) return 0;
            var rx = new Regex(@"^Numeric\s*\(\s*(?<m>\d+)\s*(?:[,.](?<d>\d+))?\s*\)$",
                RegexOptions.IgnoreCase);
            var mo = rx.Match(oldT);
            var mn = rx.Match(newT);
            if (!mo.Success || !mn.Success) return 0;
            int oM = int.Parse(mo.Groups["m"].Value);
            int nM = int.Parse(mn.Groups["m"].Value);
            int oD = mo.Groups["d"].Success ? int.Parse(mo.Groups["d"].Value) : 0;
            int nD = mn.Groups["d"].Success ? int.Parse(mn.Groups["d"].Value) : 0;
            if (nM < oM || nD < oD) return -1;
            if (nM > oM || nD > oD) return 1;
            return 0;
        }

        // ---- error envelope ------------------------------------------------

        private static string Err(string code, string message)
        {
            return McpResponse.Err(code: code, message: message);
        }
    }
}
