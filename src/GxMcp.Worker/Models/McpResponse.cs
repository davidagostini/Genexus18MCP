using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Models
{
    /// <summary>
    /// Canonical MCP response envelope (v2.8.0+).
    ///
    /// Wire shape:
    /// <code>
    /// {
    ///   "status":      "ok" | "error" | "partial" | "accepted",
    ///   "code":        "MachineReadableId",     // optional on ok
    ///   "target":      "&lt;object name&gt;",   // optional
    ///   "result":      { ...payload... },       // status in (ok | partial)
    ///   "error": {                              // status == error
    ///     "code":      "StableErrorCode",
    ///     "message":   "Short human sentence.",
    ///     "hint":      "One-line plain-English fix.",
    ///     "nextSteps": [{"tool": "...", "args": {...}, "why": "..."}]
    ///   },
    ///   "operationId": "...",                   // status == accepted
    ///   "pollTarget":  "..."                    // status == accepted
    /// }
    /// </code>
    ///
    /// Use <see cref="Ok"/>, <see cref="Err"/>, <see cref="Partial"/>, <see cref="Accepted"/>.
    /// The legacy <see cref="Success"/> and <see cref="Error(string, string)"/>
    /// methods remain for not-yet-migrated services; they will be removed in
    /// v2.8.0 once the whole worker has switched over.
    /// </summary>
    public class McpResponse
    {
        // ── Canonical helpers (v2.8.0) ──────────────────────────────────

        public static string Ok(
            string target = null,
            string code = null,
            JObject result = null)
        {
            var resp = new JObject { ["status"] = "ok" };
            if (!string.IsNullOrWhiteSpace(code)) resp["code"] = code;
            if (!string.IsNullOrWhiteSpace(target)) resp["target"] = target;
            if (result != null) resp["result"] = result;
            return resp.ToString();
        }

        public static string Partial(
            string target,
            string code,
            JObject result,
            JArray warnings = null)
        {
            var resp = new JObject { ["status"] = "partial" };
            if (!string.IsNullOrWhiteSpace(code)) resp["code"] = code;
            if (!string.IsNullOrWhiteSpace(target)) resp["target"] = target;
            if (result != null) resp["result"] = result;
            if (warnings != null && warnings.Count > 0) resp["warnings"] = warnings;
            return resp.ToString();
        }

        public static string Err(
            string code,
            string message,
            string hint = null,
            JArray nextSteps = null,
            string target = null,
            JObject extra = null,
            int? retryAfterMs = null,
            JObject errorExtra = null,
            bool? retryable = null,
            bool? reconciliationRequired = null)
        {
            string enMsg = GxMcp.Worker.Helpers.ErrorMessages.Translate(message);
            string enHint = GxMcp.Worker.Helpers.ErrorMessages.Translate(hint);
            var err = new JObject
            {
                ["code"] = code ?? "Unknown",
                ["message"] = enMsg
            };
            if (!string.IsNullOrWhiteSpace(enHint)) err["hint"] = enHint;
            if (nextSteps != null && nextSteps.Count > 0) err["nextSteps"] = nextSteps;
            // v2.8.0 — transient error codes carry a retry hint so a weakly-
            // capable LLM stops hammering in a tight loop and waits the
            // recommended interval. Caller passes ms; never negative.
            if (retryAfterMs.HasValue && retryAfterMs.Value > 0) err["retryAfterMs"] = retryAfterMs.Value;
            if (retryable.HasValue) err["retryable"] = retryable.Value;
            if (reconciliationRequired.HasValue) err["reconciliationRequired"] = reconciliationRequired.Value;
            // v2.8.0 — additional error-specific structured fields. Merged
            // into the `error` sub-object so error-related context lives
            // alongside code/message/hint/nextSteps rather than at the
            // envelope top level (which is what `extra:` does).
            if (errorExtra != null)
            {
                foreach (var prop in errorExtra.Properties())
                {
                    if (err[prop.Name] == null) err[prop.Name] = prop.Value;
                }
            }

            var resp = new JObject { ["status"] = "error" };
            if (!string.IsNullOrWhiteSpace(target)) resp["target"] = target;
            resp["error"] = err;

            if (extra != null)
            {
                foreach (var prop in extra.Properties())
                {
                    if (resp[prop.Name] == null) resp[prop.Name] = prop.Value;
                }
            }

            // Preserve untranslated source for support tooling.
            bool msgChanged = !string.Equals(enMsg, message, StringComparison.Ordinal);
            bool hintChanged = !string.IsNullOrEmpty(hint)
                && !string.Equals(enHint, hint, StringComparison.Ordinal);
            if (msgChanged || hintChanged)
            {
                var meta = new JObject();
                if (msgChanged) meta["sourceMessage"] = message;
                if (hintChanged) meta["sourceHint"] = hint;
                resp["_meta"] = meta;
            }
            return resp.ToString();
        }

        public static string Accepted(
            string target,
            string operationId,
            string pollTarget = null,
            JObject extra = null,
            JObject cancelTool = null,
            JObject pollTool = null)
        {
            var resp = new JObject { ["status"] = "accepted" };
            if (!string.IsNullOrWhiteSpace(target)) resp["target"] = target;
            if (!string.IsNullOrWhiteSpace(operationId)) resp["operationId"] = operationId;
            if (!string.IsNullOrWhiteSpace(pollTarget)) resp["pollTarget"] = pollTarget;
            // v2.8.0 — inline ready-to-call shortcuts so a weakly-capable LLM
            // doesn't have to know genexus_lifecycle internals to follow up
            // on an async accept. Default polling shortcut is auto-derived
            // when caller doesn't supply one but operationId is set.
            if (cancelTool == null && !string.IsNullOrWhiteSpace(operationId))
            {
                cancelTool = new JObject
                {
                    ["tool"] = "genexus_lifecycle",
                    ["args"] = new JObject
                    {
                        ["action"] = "cancel",
                        ["target"] = pollTarget ?? operationId
                    }
                };
            }
            if (pollTool == null && !string.IsNullOrWhiteSpace(operationId))
            {
                pollTool = new JObject
                {
                    ["tool"] = "genexus_lifecycle",
                    ["args"] = new JObject
                    {
                        ["action"] = "status",
                        ["target"] = pollTarget ?? operationId
                    }
                };
            }
            if (cancelTool != null) resp["cancelTool"] = cancelTool;
            if (pollTool != null) resp["pollTool"] = pollTool;
            if (extra != null)
            {
                foreach (var prop in extra.Properties())
                {
                    if (resp[prop.Name] == null) resp[prop.Name] = prop.Value;
                }
            }
            return resp.ToString();
        }

        /// <summary>
        /// Convenience: build a nextSteps[] entry for the canonical error envelope.
        /// </summary>
        public static JObject NextStep(string tool, JObject args = null, string why = null)
        {
            var step = new JObject { ["tool"] = tool };
            if (args != null) step["args"] = args;
            if (!string.IsNullOrWhiteSpace(why)) step["why"] = why;
            return step;
        }

        // Legacy McpResponse.Success / McpResponse.Error helpers removed in v2.8.0.
        // All emissions go through Ok / Err / Partial / Accepted above. See docs/envelope.md.
    }
}
