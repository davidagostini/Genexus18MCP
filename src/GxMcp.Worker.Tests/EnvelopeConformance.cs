using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Tests
{
    // Validator for the canonical MCP envelope (v2.8.0+).
    // See docs/envelope.md for the contract.
    public static class EnvelopeConformance
    {
        private static readonly HashSet<string> AllowedStatuses =
            new HashSet<string>(System.StringComparer.Ordinal) { "ok", "error", "partial", "accepted" };

        public sealed class Result
        {
            public bool Ok { get; set; }
            public List<string> Violations { get; } = new List<string>();
            public override string ToString() => Ok ? "OK" : string.Join("; ", Violations);
        }

        public static Result Validate(string json)
        {
            var r = new Result { Ok = true };
            JObject obj;
            try { obj = JObject.Parse(json); }
            catch (System.Exception ex)
            {
                r.Ok = false;
                r.Violations.Add("not valid JSON: " + ex.Message);
                return r;
            }

            string status = obj["status"]?.ToString();
            if (string.IsNullOrEmpty(status))
            {
                r.Ok = false;
                r.Violations.Add("missing 'status'");
                return r;
            }
            if (!AllowedStatuses.Contains(status))
            {
                r.Ok = false;
                r.Violations.Add($"status='{status}' is not one of ok|error|partial|accepted");
            }

            // Forbidden legacy fields at top level.
            string[] legacy = { "action", "noChange", "noChangeReason", "details" };
            foreach (var f in legacy)
            {
                if (obj[f] != null)
                    r.Violations.Add($"legacy top-level field '{f}' present — move into result or error.");
            }

            switch (status)
            {
                case "ok":
                case "partial":
                    if (obj["error"] != null) r.Violations.Add("ok/partial must not carry 'error'");
                    break;
                case "error":
                    var err = obj["error"] as JObject;
                    if (err == null) { r.Violations.Add("status=error requires 'error' object"); break; }
                    if (string.IsNullOrEmpty(err["code"]?.ToString())) r.Violations.Add("error.code missing");
                    if (string.IsNullOrEmpty(err["message"]?.ToString())) r.Violations.Add("error.message missing");
                    if (obj["result"] != null) r.Violations.Add("error must not carry 'result'");
                    ValidateOptionalBoolean(err, "retryable", r);
                    ValidateOptionalBoolean(err, "reconciliationRequired", r);
                    if (err["nextSteps"] != null)
                    {
                        if (!(err["nextSteps"] is JArray steps))
                        {
                            r.Violations.Add("error.nextSteps must be an array");
                        }
                        else
                        {
                            for (int i = 0; i < steps.Count; i++)
                            {
                                if (!(steps[i] is JObject step) || string.IsNullOrWhiteSpace(step["tool"]?.ToString()))
                                    r.Violations.Add($"error.nextSteps[{i}] requires a tool");
                            }
                        }
                    }
                    break;
                case "accepted":
                    if (string.IsNullOrEmpty(obj["operationId"]?.ToString()))
                        r.Violations.Add("status=accepted requires 'operationId'");
                    break;
            }

            r.Ok = r.Violations.Count == 0;
            return r;
        }

        private static void ValidateOptionalBoolean(JObject obj, string name, Result result)
        {
            if (obj[name] != null && obj[name].Type != JTokenType.Boolean)
                result.Violations.Add($"error.{name} must be boolean");
        }
    }
}
