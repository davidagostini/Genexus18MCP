using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Helpers;
using GeneXusApi = Artech.Genexus.Common.Objects.API;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// `genexus_api` — introspect HTTP Procedures and native API methods. API
    /// methods are read and written through API.ServiceGroupSource, preserving
    /// the complete authored route block around the small prefix transformation.
    /// </summary>
    public class ApiIntrospectService
    {
        private const string RouteStatusDryRun = "DryRun";
        private const string RouteStatusPending = "Pending";
        private const string RouteStatusPersisted = "Persisted";
        private const string RouteStatusNoChange = "NoChange";

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
                    case "routes_inspect":
                        return DoApiRoutesInspect(args);
                    case "routes_clone":
                        return DoApiRoutesChange(args, updateExisting: false);
                    case "routes_update":
                        return DoApiRoutesChange(args, updateExisting: true);
                    case "snapshot":
                        return DoSnapshot(args?["name"]?.ToString());
                    case "diff_baseline":
                        return DoDiffBaseline(args?["baseline"]?.ToString());
                    case "export_openapi":
                        return DoExportOpenApi(args?["title"]?.ToString(), args?["version"]?.ToString(), args?["pathPrefix"]?.ToString());
                    case "import_openapi":
                        return DoImportOpenApi(args?["spec"]?.ToString() ?? args?["content"]?.ToString());
                    default:
                        return Err("InvalidAction", $"Unknown action '{action}'. Use list|describe|routes_inspect|routes_clone|routes_update|diff_baseline|snapshot|export_openapi|import_openapi.");
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

            // API objects have their callable methods in ServiceGroupSource,
            // not in the Procedure index used by the legacy describe path.
            var api = ResolveApi(target);
            if (api != null)
                return BuildApiRoutesResponse(api, api.ServiceGroupSource?.Source, "ApiRoutesInspected", "Inspected", false, null, null);

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

        // ---- native API routes --------------------------------------------

        private string DoApiRoutesInspect(JObject args)
        {
            string apiName = args?["api"]?.ToString();
            if (string.IsNullOrWhiteSpace(apiName))
                return Err("InvalidApi", "api is required for action=routes_inspect.");

            var api = ResolveApi(apiName);
            if (api == null)
                return Err("NotFound", $"No API named '{apiName}' was found in the open KB.");

            return BuildApiRoutesResponse(
                api,
                api.ServiceGroupSource?.Source,
                "ApiRoutesInspected",
                "Inspected",
                false,
                args?["sourcePrefix"]?.ToString(),
                args?["targetPrefix"]?.ToString());
        }

        private string DoApiRoutesChange(JObject args, bool updateExisting)
        {
            string apiName = args?["api"]?.ToString();
            string sourcePrefix = args?["sourcePrefix"]?.ToString();
            string targetPrefix = args?["targetPrefix"]?.ToString();
            bool dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false;
            bool rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? true;
            string operation = args?["operation"]?.ToString();

            if (string.IsNullOrWhiteSpace(apiName)) return Err("InvalidApi", "api is required.");
            if (!IsSafeRoutePrefix(sourcePrefix) || !IsSafeRoutePrefix(targetPrefix))
                return McpResponse.Err(
                    code: "InvalidPrefix",
                    message: "sourcePrefix and targetPrefix must be identifiers (letters, digits, _ or -).",
                    hint: "Use values such as inbound and reverse.");

            var api = ResolveApi(apiName);
            if (api == null)
                return Err("NotFound", $"No API named '{apiName}' was found in the open KB.");
            if (api.ServiceGroupSource == null)
                return Err("MethodsPartUnavailable", "The API does not expose its native ServiceGroupSource part.");

            string currentSource = api.ServiceGroupSource.Source ?? string.Empty;
            var snapshot = CaptureApiSnapshot(api, currentSource);
            var plan = BuildApiRoutePlan(currentSource, sourcePrefix, targetPrefix, updateExisting);
            string expectedVersion = args?["expectedVersion"]?.ToString();
            string requestedOperation = string.IsNullOrWhiteSpace(operation)
                ? targetPrefix.ToUpperInvariant()
                : operation;

            var preview = BuildApiRoutePlanResult(
                api,
                snapshot,
                plan,
                sourcePrefix,
                targetPrefix,
                requestedOperation,
                dryRun,
                rollbackOnFailure);

            if (dryRun)
                return McpResponse.Ok(target: api.Name, code: updateExisting ? "ApiRoutesUpdatePreview" : "ApiRoutesClonePreview", result: preview);

            // A write is only valid after a preview/re-read supplied its opaque
            // token. This prevents a naked false dryRun from becoming an edit.
            if (string.IsNullOrWhiteSpace(expectedVersion))
            {
                preview["dryRunRequired"] = true;
                return McpResponse.Err(
                    code: "DryRunRequired",
                    message: "Run the same routes action with dryRun=true first, then retry with its versionToken as expectedVersion.",
                    hint: "The KB was not changed.",
                    target: api.Name,
                    extra: new JObject { ["result"] = preview });
            }

            if (!string.Equals(expectedVersion, snapshot.VersionToken, StringComparison.Ordinal))
            {
                return McpResponse.Err(
                    code: "VersionConflict",
                    message: "The API changed after the route preview; no route was written.",
                    hint: "Run routes_inspect or dryRun again and retry with the new versionToken.",
                    target: api.Name,
                    extra: new JObject
                    {
                        ["persisted"] = false,
                        ["versionToken"] = snapshot.VersionToken,
                        ["expectedVersion"] = expectedVersion
                    });
            }

            if (plan.Conflicts.Count > 0)
            {
                return McpResponse.Err(
                    code: "RouteConflict",
                    message: "The requested target routes conflict with existing API methods; no route was written.",
                    hint: "Inspect the target prefix and choose routes that do not collide.",
                    target: api.Name,
                    extra: new JObject { ["persisted"] = false, ["diff"] = preview["diff"], ["conflicts"] = plan.Conflicts });
            }

            if (string.Equals(currentSource, plan.CandidateSource, StringComparison.Ordinal))
            {
                preview["status"] = RouteStatusNoChange;
                preview["persisted"] = false;
                return McpResponse.Ok(target: api.Name, code: "ApiRoutesNoChange", result: preview);
            }

            // Re-read immediately before saving. The preview token check above
            // protects the caller's intent; this second check closes the normal
            // read/plan/write window so a concurrent edit is never knowingly
            // overwritten.
            var latest = ResolveApiFresh(api.Name);
            if (latest == null || latest.ServiceGroupSource == null)
            {
                return McpResponse.Err(
                    code: "VersionConflict",
                    message: "The API could not be re-read before saving; no route was written.",
                    hint: "Run routes_inspect or dryRun again and retry with the new versionToken.",
                    target: api.Name,
                    extra: new JObject { ["persisted"] = false, ["versionToken"] = snapshot.VersionToken });
            }

            var latestSnapshot = CaptureApiSnapshot(latest, latest.ServiceGroupSource.Source ?? string.Empty);
            if (!string.Equals(latestSnapshot.VersionToken, snapshot.VersionToken, StringComparison.Ordinal)
                || !string.Equals(latestSnapshot.Methods, snapshot.Methods, StringComparison.Ordinal))
            {
                return McpResponse.Err(
                    code: "VersionConflict",
                    message: "The API changed after the route preview; no route was written.",
                    hint: "Run routes_inspect or dryRun again and retry with the new versionToken.",
                    target: api.Name,
                    extra: new JObject
                    {
                        ["persisted"] = false,
                        ["versionToken"] = latestSnapshot.VersionToken,
                        ["expectedVersion"] = expectedVersion
                    });
            }
            api = latest;

            string validationError = ValidateApiSourceWithSdk(api, plan.CandidateSource, plan.TargetMethodNames);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                return McpResponse.Err(
                    code: "ApiRoutesValidationFailed",
                    message: validationError,
                    hint: "The native API part rejected the candidate; the KB was not changed.",
                    target: api.Name,
                    extra: new JObject { ["persisted"] = false, ["versionToken"] = snapshot.VersionToken });
            }

            try
            {
                // The candidate contains only the new/updated ServiceGroupSource
                // blocks. No Specify, Generate, Build, Rebuild or execution is
                // invoked by this operation.
                api.ServiceGroupSource.Source = plan.CandidateSource;
                api.EnsureSave(false);

                _objectService.MarkReadCacheDirty(api, "Methods");
                var fresh = ResolveApiFresh(api.Name);
                if (fresh == null || fresh.ServiceGroupSource == null)
                    throw new InvalidOperationException("The API could not be re-read after saving.");

                var after = CaptureApiSnapshot(fresh, fresh.ServiceGroupSource.Source ?? string.Empty);
                string changedPart = null;
                bool nonMethodsEqual = SnapshotNonMethodsEqual(snapshot, after, out changedPart);
                if (!string.Equals(after.Methods, plan.CandidateSource, StringComparison.Ordinal)
                    || !nonMethodsEqual)
                {
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(changedPart)
                        ? "The persisted API routes differ from the candidate after re-read."
                        : "The SDK changed an API part outside Methods after route save: " + changedPart);
                }

                var persisted = BuildApiRoutePlanResult(
                    fresh,
                    after,
                    plan,
                    sourcePrefix,
                    targetPrefix,
                    requestedOperation,
                    false,
                    rollbackOnFailure);
                persisted["status"] = RouteStatusPersisted;
                persisted["persisted"] = true;
                persisted["versionToken"] = after.VersionToken;
                persisted["methods"] = BuildApiMethods(fresh, after.Methods);
                return McpResponse.Ok(target: fresh.Name, code: "ApiRoutesPersisted", result: persisted);
            }
            catch (Exception ex)
            {
                Logger.Warn("[ApiIntrospectService] API route write failed: " + ex.Message);
                var rollback = rollbackOnFailure
                    ? TryRollbackApiSnapshot(api.Name, snapshot, plan.CandidateSource)
                    : new JObject { ["attempted"] = false, ["verified"] = false };
                return McpResponse.Err(
                    code: "ApiRoutesWriteFailed",
                    message: ex.Message,
                    hint: rollback["verified"]?.ToObject<bool>() == true
                        ? "The complete API snapshot was restored."
                        : "Review the API before retrying; automatic restoration could not be confirmed.",
                    target: api.Name,
                    extra: new JObject
                    {
                        ["persisted"] = false,
                        ["rollback"] = rollback,
                        ["versionToken"] = snapshot.VersionToken
                    });
            }
        }

        private GeneXusApi ResolveApi(string name)
        {
            try { return _objectService?.FindObject(name, "API") as GeneXusApi; }
            catch { return null; }
        }

        private GeneXusApi ResolveApiFresh(string name)
        {
            try { return _objectService?.FindObjectFresh(name, "API") as GeneXusApi; }
            catch { return null; }
        }

        private string BuildApiRoutesResponse(
            GeneXusApi api,
            string source,
            string code,
            string status,
            bool persisted,
            string sourcePrefix,
            string targetPrefix)
        {
            if (api?.ServiceGroupSource == null)
                return Err("MethodsPartUnavailable", "The API does not expose its native ServiceGroupSource part.");

            var snapshot = CaptureApiSnapshot(api, source ?? string.Empty);
            var result = new JObject
            {
                ["status"] = status,
                ["persisted"] = persisted,
                ["api"] = api.Name,
                ["versionToken"] = snapshot.VersionToken,
                ["methods"] = BuildApiMethods(api, snapshot.Methods)
            };
            if (!string.IsNullOrWhiteSpace(sourcePrefix)) result["sourcePrefix"] = sourcePrefix;
            if (!string.IsNullOrWhiteSpace(targetPrefix)) result["targetPrefix"] = targetPrefix;
            return McpResponse.Ok(target: api.Name, code: code, result: result);
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

        private static readonly Regex ApiRouteRegex = new Regex(
            @"(?ms)(?<block>\[(?<attrs>[^\]]*)\])(?<between>(?:(?:[ \t]*//[^\r\n]*)?\s*)*)(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<params>.*?)\)\s*=>\s*(?<call>[^;]+);",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ApiVerbRegex = new Regex(
            @"RestMethod\s*\(\s*(?<verb>[A-Za-z]+)\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ApiPathRegex = new Regex(
            @"RestPath\s*\(\s*[""'](?<path>.*?)[""']\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ApiCallRegex = new Regex(
            @"^\s*(?<procedure>[A-Za-z_][A-Za-z0-9_.]*)\s*\((?<arguments>.*)\)\s*$",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex ApiBindingRegex = new Regex(
            @"(?<direction>inout|in|out)\s*:\s*&(?<name>[A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RoutePrefixRegex = new Regex(
            @"^[A-Za-z][A-Za-z0-9_-]{0,63}$",
            RegexOptions.Compiled);

        internal sealed class ApiRoute
        {
            public string MethodName;
            public string Verb;
            public string Path;
            public string ParametersText;
            public string CallText;
            public string SourceText;
            public int SourceIndex;
            public int MethodOffset;
            public int PathOffset;
            public int PathLength;
        }

        private sealed class ApiRouteChange
        {
            public ApiRoute Existing;
            public string Replacement;
        }

        internal sealed class ApiRoutePlan
        {
            public string CandidateSource;
            public List<ApiRoute> Added = new List<ApiRoute>();
            public List<ApiRoute> Updated = new List<ApiRoute>();
            public List<ApiRoute> Unchanged = new List<ApiRoute>();
            public JArray Conflicts = new JArray();
            public List<string> TargetMethodNames = new List<string>();
        }

        private sealed class ApiSnapshot
        {
            public string Methods;
            public string VersionToken;
            public Dictionary<string, string> Parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        internal static bool IsSafeRoutePrefix(string prefix)
        {
            return !string.IsNullOrWhiteSpace(prefix) && RoutePrefixRegex.IsMatch(prefix.Trim());
        }

        internal static List<ApiRoute> ParseApiRoutes(string source)
        {
            var routes = new List<ApiRoute>();
            if (string.IsNullOrEmpty(source)) return routes;

            foreach (Match match in ApiRouteRegex.Matches(source))
            {
                var verb = ApiVerbRegex.Match(match.Groups["attrs"].Value);
                var path = ApiPathRegex.Match(match.Groups["attrs"].Value);
                if (!verb.Success || !path.Success) continue;

                routes.Add(new ApiRoute
                {
                    MethodName = match.Groups["name"].Value,
                    Verb = verb.Groups["verb"].Value.ToUpperInvariant(),
                    Path = path.Groups["path"].Value,
                    ParametersText = match.Groups["params"].Value.Trim(),
                    CallText = match.Groups["call"].Value.Trim(),
                    SourceText = match.Value,
                    SourceIndex = match.Index,
                    MethodOffset = match.Groups["name"].Index - match.Index,
                    PathOffset = match.Groups["attrs"].Index - match.Index + path.Groups["path"].Index,
                    PathLength = path.Groups["path"].Length
                });
            }
            return routes;
        }

        private static ApiRoute CloneApiRoute(ApiRoute source, string sourcePrefix, string targetPrefix)
        {
            string methodName = Regex.Replace(
                source.MethodName,
                "^" + Regex.Escape(sourcePrefix) + "(?=_|$)",
                targetPrefix,
                RegexOptions.IgnoreCase);
            string path = Regex.Replace(
                source.Path,
                "/" + Regex.Escape(sourcePrefix) + "(?=/|$)",
                "/" + targetPrefix,
                RegexOptions.IgnoreCase);

            string block = source.SourceText;
            if (source.MethodOffset > source.PathOffset)
            {
                block = ReplaceAt(block, source.MethodOffset, source.MethodName.Length, methodName);
                block = ReplaceAt(block, source.PathOffset, source.PathLength, path);
            }
            else
            {
                block = ReplaceAt(block, source.PathOffset, source.PathLength, path);
                block = ReplaceAt(block, source.MethodOffset, source.MethodName.Length, methodName);
            }

            return new ApiRoute
            {
                MethodName = methodName,
                Verb = source.Verb,
                Path = path,
                ParametersText = source.ParametersText,
                CallText = source.CallText,
                SourceText = block
            };
        }

        private static string ReplaceAt(string text, int index, int length, string replacement)
        {
            return text.Substring(0, index) + replacement + text.Substring(index + length);
        }

        private static bool HasRoutePrefix(ApiRoute route, string prefix)
        {
            return route.MethodName.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(route.Path, "/" + Regex.Escape(prefix) + "(?=/|$)", RegexOptions.IgnoreCase);
        }

        internal static ApiRoutePlan BuildApiRoutePlan(string source, string sourcePrefix, string targetPrefix, bool updateExisting)
        {
            var plan = new ApiRoutePlan { CandidateSource = source ?? string.Empty };
            var current = ParseApiRoutes(plan.CandidateSource);
            var selected = current.Where(r => HasRoutePrefix(r, sourcePrefix)).ToList();
            var replacements = new List<ApiRouteChange>();
            var additions = new List<ApiRoute>();

            foreach (var sourceRoute in selected)
            {
                var candidate = CloneApiRoute(sourceRoute, sourcePrefix, targetPrefix);
                plan.TargetMethodNames.Add(candidate.MethodName);

                var existing = current.FirstOrDefault(r =>
                    string.Equals(r.MethodName, candidate.MethodName, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    existing = current.FirstOrDefault(r =>
                        string.Equals(r.Verb, candidate.Verb, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(r.Path, candidate.Path, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        plan.Conflicts.Add(candidate.MethodName + " (" + candidate.Verb + " " + candidate.Path + ")");
                        continue;
                    }

                    additions.Add(candidate);
                    plan.Added.Add(candidate);
                    continue;
                }

                if (string.Equals(existing.SourceText, candidate.SourceText, StringComparison.Ordinal))
                {
                    plan.Unchanged.Add(candidate);
                }
                else if (updateExisting)
                {
                    plan.Updated.Add(candidate);
                    replacements.Add(new ApiRouteChange { Existing = existing, Replacement = candidate.SourceText });
                }
                else
                {
                    plan.Conflicts.Add(candidate.MethodName + " (método já existe com conteúdo diferente)");
                }
            }

            // Never write a partial clone/update when one target collides.
            if (plan.Conflicts.Count > 0)
            {
                plan.CandidateSource = source ?? string.Empty;
                return plan;
            }

            string candidateSource = source ?? string.Empty;
            foreach (var change in replacements.OrderByDescending(c => c.Existing.SourceIndex))
            {
                candidateSource = candidateSource.Substring(0, change.Existing.SourceIndex)
                    + change.Replacement
                    + candidateSource.Substring(change.Existing.SourceIndex + change.Existing.SourceText.Length);
            }
            if (additions.Count > 0)
                candidateSource = AppendApiRouteBlocks(candidateSource, additions.Select(a => a.SourceText));

            plan.CandidateSource = candidateSource;
            return plan;
        }

        private static string AppendApiRouteBlocks(string source, IEnumerable<string> blocks)
        {
            string addition = string.Join(Environment.NewLine + Environment.NewLine, blocks ?? Enumerable.Empty<string>());
            if (string.IsNullOrEmpty(addition)) return source;
            int close = source.LastIndexOf('}');
            if (close < 0) return source.TrimEnd() + Environment.NewLine + addition + Environment.NewLine;

            string before = source.Substring(0, close).TrimEnd();
            string after = source.Substring(close);
            return before + Environment.NewLine + Environment.NewLine + addition + Environment.NewLine + after;
        }

        private ApiSnapshot CaptureApiSnapshot(GeneXusApi api, string methods)
        {
            var snapshot = new ApiSnapshot
            {
                Methods = methods ?? string.Empty,
                VersionToken = WriteService.ComputeContentVersionToken(api, methods ?? string.Empty)
            };
            snapshot.Parts["Methods"] = snapshot.Methods;
            foreach (string partName in GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(api))
            {
                if (string.Equals(partName, "Methods", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    string json = _objectService.ReadObjectSourceForVerification(api.Name, partName, "API");
                    var payload = JObject.Parse(json);
                    snapshot.Parts[partName] = payload["source"]?.ToString()
                        ?? (payload["error"] == null ? payload.ToString(Formatting.None) : null);
                }
                catch { }
            }
            return snapshot;
        }

        private JArray BuildApiMethods(GeneXusApi api, string source)
        {
            var typedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (api?.ServiceGroupSource != null)
                {
                    foreach (var signature in api.ServiceGroupSource.GetPublicMethods() ?? Enumerable.Empty<Artech.Genexus.Common.Objects.Signature>())
                    {
                        string signatureName = GetSignatureName(signature);
                        if (!string.IsNullOrWhiteSpace(signatureName)) typedNames.Add(signatureName);
                    }
                }
            }
            catch { }

            var variableTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (JObject variable in _objectService.GetVariablesCompact(api, source).OfType<JObject>())
                {
                    string name = variable["name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name)) variableTypes[name.TrimStart('&')] = variable["type"]?.ToString();
                }
            }
            catch { }

            var methods = new JArray();
            foreach (var route in ParseApiRoutes(source))
                methods.Add(ApiRouteToJson(route, variableTypes, typedNames.Contains(route.MethodName)));
            return methods;
        }

        private static string GetSignatureName(Artech.Genexus.Common.Objects.Signature signature)
        {
            try { return signature?.Data?.Name; }
            catch { return null; }
        }

        private static JObject ApiRouteToJson(ApiRoute route, IDictionary<string, string> variableTypes, bool hasTypedSignature)
        {
            var parameters = new JArray();
            foreach (Match binding in ApiBindingRegex.Matches(route.ParametersText ?? string.Empty))
            {
                string name = binding.Groups["name"].Value;
                var parameter = new JObject
                {
                    ["direction"] = binding.Groups["direction"].Value.ToLowerInvariant(),
                    ["name"] = name
                };
                if (variableTypes != null && variableTypes.TryGetValue(name, out string type) && !string.IsNullOrWhiteSpace(type))
                    parameter["type"] = type;
                parameters.Add(parameter);
            }

            var call = ApiCallRegex.Match(route.CallText ?? string.Empty);
            var callArguments = new JArray();
            if (call.Success)
            {
                foreach (string arg in SplitArguments(call.Groups["arguments"].Value))
                    if (!string.IsNullOrWhiteSpace(arg)) callArguments.Add(arg.Trim());
            }

            var bindings = new JObject
            {
                ["parameters"] = parameters,
                ["callArguments"] = callArguments,
                ["procedure"] = call.Success ? call.Groups["procedure"].Value : route.CallText
            };
            return new JObject
            {
                ["method"] = route.MethodName,
                ["name"] = route.MethodName,
                ["verb"] = route.Verb,
                ["route"] = route.Path,
                ["bindings"] = bindings,
                ["sdkSignature"] = hasTypedSignature,
                ["source"] = route.SourceText
            };
        }

        private static IEnumerable<string> SplitArguments(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;
            int start = 0;
            int depth = 0;
            bool quoted = false;
            char quote = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == quote && (i == 0 || text[i - 1] != '\\')) quoted = false;
                    continue;
                }
                if (c == '\'' || c == '"') { quoted = true; quote = c; continue; }
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') depth--;
                else if (c == ',' && depth == 0)
                {
                    yield return text.Substring(start, i - start);
                    start = i + 1;
                }
            }
            yield return text.Substring(start);
        }

        private static JObject BuildApiRoutePlanResult(
            GeneXusApi api,
            ApiSnapshot snapshot,
            ApiRoutePlan plan,
            string sourcePrefix,
            string targetPrefix,
            string operation,
            bool dryRun,
            bool rollbackOnFailure)
        {
            return new JObject
            {
                ["status"] = dryRun ? RouteStatusDryRun : RouteStatusPending,
                ["persisted"] = false,
                ["api"] = api?.Name,
                ["sourcePrefix"] = sourcePrefix,
                ["targetPrefix"] = targetPrefix,
                ["operation"] = operation,
                ["rollbackOnFailure"] = rollbackOnFailure,
                ["versionToken"] = snapshot?.VersionToken,
                ["methods"] = new JArray(plan.Added.Concat(plan.Updated).Select(r => ApiRouteToJson(r, null, false))),
                ["diff"] = new JObject
                {
                    ["addedRoutes"] = new JArray(plan.Added.Select(r => r.Path)),
                    ["updatedRoutes"] = new JArray(plan.Updated.Select(r => r.Path)),
                    ["unchangedRoutes"] = new JArray(plan.Unchanged.Select(r => r.Path)),
                    ["conflicts"] = plan.Conflicts
                }
            };
        }

        private static bool SnapshotNonMethodsEqual(ApiSnapshot before, ApiSnapshot after, out string changedPart)
        {
            changedPart = null;
            if (before == null || after == null) { changedPart = "snapshot"; return false; }
            var names = before.Parts.Keys.Union(after.Parts.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (string name in names)
            {
                if (string.Equals(name, "Methods", StringComparison.OrdinalIgnoreCase)) continue;
                before.Parts.TryGetValue(name, out string oldValue);
                after.Parts.TryGetValue(name, out string newValue);
                if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
                {
                    changedPart = name;
                    return false;
                }
            }
            return true;
        }

        private static string ValidateApiSourceWithSdk(GeneXusApi api, string candidateSource, IEnumerable<string> targetMethodNames)
        {
            if (api?.ServiceGroupSource == null) return "The API does not expose its native ServiceGroupSource part.";
            string original = api.ServiceGroupSource.Source ?? string.Empty;
            try
            {
                api.ServiceGroupSource.Source = candidateSource ?? string.Empty;
                var typedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var signature in api.ServiceGroupSource.GetPublicMethods() ?? Enumerable.Empty<Artech.Genexus.Common.Objects.Signature>())
                {
                    string signatureName = GetSignatureName(signature);
                    if (!string.IsNullOrWhiteSpace(signatureName)) typedNames.Add(signatureName);
                }

                foreach (string targetName in targetMethodNames ?? Enumerable.Empty<string>())
                    if (!typedNames.Contains(targetName))
                        return "The native SDK did not recognize API method '" + targetName + "' in the candidate source.";
                return null;
            }
            catch (Exception ex)
            {
                return "The native API SDK rejected the candidate source: " + ex.Message;
            }
            finally
            {
                try { api.ServiceGroupSource.Source = original; } catch { }
            }
        }

        private JObject TryRollbackApiSnapshot(string apiName, ApiSnapshot snapshot, string candidateSource)
        {
            var result = new JObject { ["attempted"] = true, ["verified"] = false };
            try
            {
                var current = ResolveApiFresh(apiName);
                if (current == null || current.ServiceGroupSource == null)
                {
                    result["error"] = "API could not be re-read for rollback.";
                    return result;
                }

                string currentSource = current.ServiceGroupSource.Source ?? string.Empty;
                if (!string.Equals(currentSource, candidateSource, StringComparison.Ordinal)
                    && !string.Equals(currentSource, snapshot.Methods, StringComparison.Ordinal))
                {
                    result["error"] = "The API changed again after the failed write; rollback was not allowed to overwrite it.";
                    result["versionToken"] = WriteService.ComputeContentVersionToken(current, currentSource);
                    return result;
                }

                current.ServiceGroupSource.Source = snapshot.Methods ?? string.Empty;
                current.EnsureSave(false);
                _objectService.MarkReadCacheDirty(current, "Methods");
                var restored = ResolveApiFresh(apiName);
                var restoredSnapshot = restored == null || restored.ServiceGroupSource == null
                    ? null
                    : CaptureApiSnapshot(restored, restored.ServiceGroupSource.Source ?? string.Empty);
                result["verified"] = restoredSnapshot != null
                    && string.Equals(restoredSnapshot.Methods, snapshot.Methods, StringComparison.Ordinal)
                    && SnapshotNonMethodsEqual(snapshot, restoredSnapshot, out string ignored);
                if (restoredSnapshot != null) result["versionToken"] = restoredSnapshot.VersionToken;
            }
            catch (Exception ex)
            {
                result["error"] = ex.Message;
            }
            return result;
        }

        // ---- core enumeration ----------------------------------------------

        private IEnumerable<HttpEndpoint> EnumerateHttpEndpoints(string pathPrefix)
        {
            var idx = _indexCacheService?.GetIndex();
            if (idx?.Objects != null)
            {
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

            // API methods are not Procedures and therefore do not appear in the
            // legacy index pass. Enumerate the SDK's typed API collection too.
            foreach (var api in EnumerateApis())
            {
                string apiPath = ApiPath(api);
                if (!string.IsNullOrEmpty(pathPrefix)
                    && !apiPath.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                string source = api.ServiceGroupSource?.Source;
                foreach (var route in ParseApiRoutes(source))
                {
                    var endpoint = BuildEndpointFromApiRoute(api, route);
                    yield return endpoint;
                }
            }
        }

        private IEnumerable<GeneXusApi> EnumerateApis()
        {
            var model = _kbService?.GetKB()?.DesignModel;
            if (model == null) yield break;
            IEnumerable<GeneXusApi> apis = null;
            try { apis = GeneXusApi.GetAll(model); } catch { }
            if (apis == null) yield break;
            foreach (var api in apis)
                if (api != null) yield return api;
        }

        private static string ApiPath(GeneXusApi api)
        {
            try
            {
                return api?.Parent?.Name ?? api?.Module?.Name ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private HttpEndpoint BuildEndpointFromApiRoute(GeneXusApi api, ApiRoute route)
        {
            var endpoint = new HttpEndpoint
            {
                Name = api.Name + "." + route.MethodName,
                HttpMethod = route.Verb,
                Url = route.Path,
                Path = ApiPath(api),
                LastUpdate = DateTime.MinValue,
                ApiName = api.Name,
                ApiMethod = route.MethodName
            };
            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (JObject variable in _objectService.GetVariablesCompact(api, api.ServiceGroupSource?.Source).OfType<JObject>())
                {
                    string name = variable["name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name)) variables[name.TrimStart('&')] = variable["type"]?.ToString();
                }
            }
            catch { }
            foreach (Match binding in ApiBindingRegex.Matches(route.ParametersText ?? string.Empty))
            {
                string name = binding.Groups["name"].Value;
                endpoint.Parms.Add(new Parm
                {
                    Name = name,
                    Direction = binding.Groups["direction"].Value.ToLowerInvariant(),
                    Type = variables.TryGetValue(name, out string type) ? type : null
                });
            }
            return endpoint;
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
            public string ApiName;
            public string ApiMethod;
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
            if (!string.IsNullOrWhiteSpace(ep.ApiName))
            {
                j["api"] = ep.ApiName;
                j["method"] = ep.ApiMethod;
                j["sourceType"] = "API";
            }
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
