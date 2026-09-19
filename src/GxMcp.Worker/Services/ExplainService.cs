using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 68 — genexus_explain.
    ///
    /// Deterministic, structured natural-language summary of an object.
    /// Combines object Description, parm signature, variables, top called
    /// procedures/transactions, and a derived one-sentence purpose line.
    /// Suitable for a PM/stakeholder; NOT raw source.
    /// </summary>
    public class ExplainService
    {
        // v2.6.9 perf: repeat-call cache. Same pattern as AnalyzeService's
        // inspect cache — Explain does N sequential SDK reads (parm rule,
        // variables, called procs, called transactions, description, etc.)
        // on the STA thread; repeat calls within 30s with no observed write
        // return the previously-built summary unchanged.
        private sealed class ExplainCacheEntry
        {
            public string Json;
            public System.DateTime FilledAtUtc;
        }
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ExplainCacheEntry> _explainCache
            = new System.Collections.Concurrent.ConcurrentDictionary<string, ExplainCacheEntry>(System.StringComparer.OrdinalIgnoreCase);
        private static readonly System.TimeSpan ExplainCacheTtl = System.TimeSpan.FromSeconds(30);

        private readonly KbService _kbService;
        private readonly ObjectService _objectService;

        public ExplainService(KbService kbService, ObjectService objectService)
        {
            _kbService = kbService;
            _objectService = objectService;
        }

        public string Explain(string target, string typeFilter, string depth)
        {
            if (string.IsNullOrEmpty(target))
                return Models.McpResponse.Err(
                    code: "MissingTarget",
                    message: "target object name is required.",
                    hint: "Pass the name of the object to explain.");

            // v2.6.9 perf: check cache before SDK reads.
            string explainKey = (target ?? "") + "|" + (typeFilter ?? "") + "|" + (depth ?? "shallow").ToLowerInvariant();
            if (_explainCache.TryGetValue(explainKey, out var cachedExplain) && cachedExplain != null)
            {
                bool ttlOk = (System.DateTime.UtcNow - cachedExplain.FilledAtUtc) < ExplainCacheTtl;
                bool noWrite = !WriteService.WasTargetWrittenSince(target, cachedExplain.FilledAtUtc);
                if (ttlOk && noWrite)
                {
                    return cachedExplain.Json;
                }
                _explainCache.TryRemove(explainKey, out _);
            }

            try
            {
                var obj = _objectService?.FindObject(target, typeFilter);
                if (obj == null)
                    return Models.McpResponse.Err(
                        code: "ObjectNotFound",
                        message: "The requested object is not available in the active Knowledge Base.",
                        hint: "Check the object name or use genexus_list_objects to find the correct name.",
                        nextSteps: new Newtonsoft.Json.Linq.JArray(Models.McpResponse.NextStep(
                            tool: "genexus_list_objects",
                            args: new Newtonsoft.Json.Linq.JObject(),
                            why: "Lists all objects in the KB so you can find the correct name.")),
                        target: target);

                bool deep = string.Equals(depth, "deep", StringComparison.OrdinalIgnoreCase);

                var (parmRule, parms) = _objectService.GetParametersInternal(obj);
                var inputs = new JArray();
                var outputs = new JArray();
                foreach (var p in parms ?? new List<ObjectService.ParameterInfo>())
                {
                    var item = new JObject
                    {
                        ["name"] = p.Name,
                        ["type"] = p.Type,
                        ["accessor"] = p.Accessor
                    };
                    if (string.Equals(p.Accessor, "out", StringComparison.OrdinalIgnoreCase))
                        outputs.Add(item);
                    else if (string.Equals(p.Accessor, "inout", StringComparison.OrdinalIgnoreCase))
                    {
                        inputs.Add(item);
                        outputs.Add(item);
                    }
                    else
                        inputs.Add(item);
                }

                var variables = BuildVariables(obj);
                var (procCalls, trnCalls) = BuildCalls(obj, deep);

                string objectType = SafeGet(() => obj.TypeDescriptor?.Name) ?? "Object";
                string objectName = obj.Name ?? target;
                string description = SafeGet(() => obj.Description) ?? string.Empty;
                string purpose = BuildPurpose(objectType, objectName, description, parms);

                DateTime lastModified = DateTime.MinValue;
                try { lastModified = SdkTimestampNormalizer.NormalizeUtc(obj.LastUpdate); } catch { }

                var payload = new JObject
                {
                    ["name"] = objectName,
                    ["type"] = objectType,
                    ["purpose"] = purpose,
                    ["inputs"] = inputs,
                    ["outputs"] = outputs,
                    ["variables"] = variables,
                    ["calls"] = new JObject
                    {
                        ["procedures"] = procCalls,
                        ["transactions"] = trnCalls
                    },
                    ["description"] = description,
                    ["lastModified"] = lastModified == DateTime.MinValue
                        ? (JToken)JValue.CreateNull()
                        : lastModified.ToUniversalTime().ToString("o")
                };
                string json = Models.McpResponse.Ok(target: objectName, code: "ExplainOk", result: payload);
                _explainCache[explainKey] = new ExplainCacheEntry
                {
                    Json = json,
                    FilledAtUtc = System.DateTime.UtcNow
                };
                return json;
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "ExplainFailed",
                    message: ex.Message,
                    hint: "Check that the KB is open and the object exists.",
                    target: target);
            }
        }

        private JArray BuildVariables(KBObject obj)
        {
            var arr = new JArray();
            try
            {
                string text = VariableInjector.GetVariablesAsText(obj);
                if (string.IsNullOrEmpty(text)) return arr;
                foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    // Shape: "&Name : Type[ Collection]"
                    var trimmed = line.Trim();
                    int sep = trimmed.IndexOf(':');
                    if (sep < 0) continue;
                    string name = trimmed.Substring(0, sep).Trim().TrimStart('&');
                    string type = trimmed.Substring(sep + 1).Trim();
                    arr.Add(new JObject
                    {
                        ["name"] = name,
                        ["type"] = type
                    });
                }
            }
            catch { }
            return arr;
        }

        private (JArray procs, JArray trns) BuildCalls(KBObject obj, bool deep)
        {
            var procs = new JArray();
            var trns = new JArray();
            try
            {
                var kb = _kbService?.GetKB();
                if (kb == null) return (procs, trns);

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var reference in obj.GetReferences())
                {
                    string refKey = null;
                    try { refKey = reference.To?.ToString(); } catch { }
                    if (string.IsNullOrEmpty(refKey) || !seen.Add(refKey)) continue;

                    KBObject targetObj = null;
                    try { targetObj = kb.DesignModel.Objects.Get(reference.To); } catch { }
                    if (targetObj == null) continue;

                    string targetType = SafeGet(() => targetObj.TypeDescriptor?.Name) ?? "";
                    string targetName = targetObj.Name ?? "";
                    if (string.IsNullOrEmpty(targetName)) continue;

                    var entry = new JObject
                    {
                        ["name"] = targetName,
                        ["description"] = SafeGet(() => targetObj.Description) ?? string.Empty
                    };

                    if (deep)
                    {
                        // one-level recurse: just include the called object's calls list (names only).
                        try
                        {
                            var nested = new JArray();
                            int cap = 0;
                            foreach (var sub in targetObj.GetReferences())
                            {
                                if (cap++ >= 5) break;
                                KBObject subObj = null;
                                try { subObj = kb.DesignModel.Objects.Get(sub.To); } catch { }
                                if (subObj?.Name != null) nested.Add(subObj.Name);
                            }
                            if (nested.Count > 0) entry["calls"] = nested;
                        }
                        catch { }
                    }

                    if (string.Equals(targetType, "Procedure", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(targetType, "DataProvider", StringComparison.OrdinalIgnoreCase))
                    {
                        if (procs.Count < 5) procs.Add(entry);
                    }
                    else if (string.Equals(targetType, "Transaction", StringComparison.OrdinalIgnoreCase))
                    {
                        if (trns.Count < 5) trns.Add(entry);
                    }

                    if (procs.Count >= 5 && trns.Count >= 5) break;
                }
            }
            catch { }
            return (procs, trns);
        }

        internal static string BuildPurpose(string objectType, string name, string description, List<ObjectService.ParameterInfo> parms)
        {
            if (!string.IsNullOrWhiteSpace(description))
            {
                // Use first sentence of description as the purpose.
                var first = description.Split(new[] { '.', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(first)) return first.Trim() + ".";
            }

            int inCount = parms?.Count(p => !string.Equals(p.Accessor, "out", StringComparison.OrdinalIgnoreCase)) ?? 0;
            int outCount = parms?.Count(p => string.Equals(p.Accessor, "out", StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(p.Accessor, "inout", StringComparison.OrdinalIgnoreCase)) ?? 0;

            string verb;
            switch ((objectType ?? "").ToLowerInvariant())
            {
                case "transaction":
                    verb = "Manages records of"; break;
                case "procedure":
                    verb = "Procedure that"; break;
                case "dataprovider":
                    verb = "Provides data for"; break;
                case "webpanel":
                case "sdpanel":
                    verb = "Web/SD panel for"; break;
                case "sdt":
                    verb = "Structured data type representing"; break;
                case "domain":
                    verb = "Domain defining"; break;
                default:
                    verb = objectType + " for"; break;
            }

            string body = SplitHumanReadable(name);
            string ioHint = (inCount + outCount > 0)
                ? string.Format(" Takes {0} input(s) and produces {1} output(s).", inCount, outCount)
                : string.Empty;
            return string.Format("{0} {1}.{2}", verb, body, ioHint);
        }

        private static string SplitHumanReadable(string name)
        {
            if (string.IsNullOrEmpty(name)) return "(unnamed)";
            var sb = new System.Text.StringBuilder(name.Length + 8);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1])) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString().ToLowerInvariant();
        }

        private static string SafeGet(Func<string> f)
        {
            try { return f(); } catch { return null; }
        }
    }
}
