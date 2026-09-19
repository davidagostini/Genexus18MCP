using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Issue #59 — post-save persistence verification helpers.
    ///
    /// After any mutating write the caller should re-read the affected value and
    /// compare what was REQUESTED against what PERSISTED. This class provides the
    /// pure comparison logic (exact normalized text by default; SDK boolean aliases
    /// — "Yes" vs "True" vs "1" — only when the caller explicitly identifies a
    /// boolean-ish property) plus the structured "NotPersisted" error envelope.
    ///
    /// Pure (no GeneXus SDK dependency) so it's unit-testable without a live KB.
    /// </summary>
    public static class PersistenceVerifier
    {
        /// <summary>
        /// Normalize a value for equality comparison. The SDK persists several
        /// boolean-ish properties in canonical spellings that differ from what an
        /// agent writes ("Yes" vs "True" vs "1" for Nullable, "N" vs "No"), so a
        /// raw string compare produces false negatives on a legitimately-applied
        /// write. This maps those families onto one token each.
        /// </summary>
        public static string NormalizeForCompare(string value)
        {
            if (value == null) return string.Empty;
            string v = value.Trim();
            if (v.Length == 0) return v;

            if (string.Equals(v, "Yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "True", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "Y", StringComparison.OrdinalIgnoreCase)
                || v == "1")
                return "yes";
            if (string.Equals(v, "No", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "False", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "N", StringComparison.OrdinalIgnoreCase)
                || v == "0")
                return "no";
            return v.ToLowerInvariant();
        }

        /// <summary>
        /// Normalization-aware equality of a requested vs a persisted value.
        /// </summary>
        public static bool ValuesMatch(string requested, string persisted)
        {
            return ValuesMatch(requested, persisted, allowBooleanAliases: false);
        }

        /// <summary>
        /// Compare values with the SDK's boolean-display aliases only when the
        /// property is known to be boolean-ish. Enum/string values such as "Yes"
        /// and "True" must not be collapsed globally or a real enum change could
        /// be reported as persisted when it was not.
        /// </summary>
        public static bool ValuesMatch(string requested, string persisted, bool allowBooleanAliases)
        {
            string requestedNormalized = allowBooleanAliases
                ? NormalizeForCompare(requested)
                : NormalizeTextForCompare(requested);
            string persistedNormalized = allowBooleanAliases
                ? NormalizeForCompare(persisted)
                : NormalizeTextForCompare(persisted);
            return string.Equals(
                requestedNormalized,
                persistedNormalized,
                StringComparison.Ordinal);
        }

        private static string NormalizeTextForCompare(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Build the canonical "not persisted" error envelope described in issue #59.
        /// Wire shape:
        /// <code>
        /// {
        ///   "status": "error",
        ///   "target": "&lt;obj&gt;",
        ///   "error": {
        ///     "code": "PropertyNotPersisted", "message": "...", "hint": "...",
        ///     "property": "Nullable", "requestedValue": "Yes",
        ///     "previousValue": "No", "persistedValue": "No", "saved": false
        ///   }
        /// }
        /// </code>
        /// The diff fields ride inside the `error` sub-object via `errorExtra` (the
        /// envelope top level is what `extra:` targets in McpResponse.Err). Never uses a
        /// generic "Applied"/"Updated" code when the value was not confirmed — the caller
        /// picks the specific code (PropertyNotPersisted, DomainUpdateNotPersisted,
        /// StructureUpdateNotPersisted, ...).
        /// </summary>
        public static string BuildNotPersistedError(
            string code,
            string target,
            string property,
            string requestedValue,
            string previousValue,
            string persistedValue,
            string message = null,
            string hint = null,
            JArray nextSteps = null)
        {
            string msg = message
                ?? $"'{property}' was reported applied but the re-read did not confirm it: requested '{requestedValue}', persisted '{persistedValue}' (before: '{previousValue}').";

            string h = hint
                ?? "The SDK save returned without throwing but the requested value did not land. Re-read the object, apply the change in the GeneXus IDE if it recurs, then re-verify.";

            var err = Models.McpResponse.Err(
                code: code,
                message: msg,
                hint: h,
                nextSteps: nextSteps,
                target: target,
                errorExtra: new JObject
                {
                    ["property"] = property ?? string.Empty,
                    ["requestedValue"] = requestedValue ?? string.Empty,
                    ["previousValue"] = previousValue ?? string.Empty,
                    ["persistedValue"] = persistedValue ?? string.Empty,
                    ["saved"] = false
                });
            return err;
        }

        /// <summary>
        /// Attach a before/requested/persisted block to a success envelope's `result`
        /// so even confirmed writes expose the effective diff (acceptance: the return
        /// contains before, requested and persisted). Best-effort: returns the input
        /// JSON unchanged when it isn't parseable.
        /// </summary>
        public static string AttachPersistedDiff(string responseJson, string before, string requested, string persisted)
        {
            try
            {
                var jo = JObject.Parse(responseJson);
                var result = jo["result"] as JObject;
                if (result == null)
                {
                    result = new JObject();
                    jo["result"] = result;
                }
                result["before"] = before ?? string.Empty;
                result["requested"] = requested ?? string.Empty;
                result["persisted"] = persisted ?? string.Empty;
                result["persistedVerified"] = true;
                return jo.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch
            {
                return responseJson;
            }
        }
    }
}
