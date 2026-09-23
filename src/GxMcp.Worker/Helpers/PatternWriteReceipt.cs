using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    internal static class PatternWriteReceipt
    {
        // A mismatch after commit is a saved mutation, not evidence of no write.
        internal static void Apply(JObject response, bool committed, string before, string requested,
            string observed, string snapshot, bool rollbackAttempted = false, string rollbackError = null, string versionToken = null)
        {
            bool known = !string.IsNullOrWhiteSpace(observed);
            bool matches = known && XmlEquivalence.AreEquivalent(observed, requested, out _);
            bool unchanged = known && XmlEquivalence.AreEquivalent(observed, before, out _);
            response["sdkSaveCompleted"] = committed ? (JToken)true : JValue.CreateNull();
            response["saved"] = response["sdkSaveCompleted"].DeepClone();
            response["persistedStateKnown"] = known;
            response["verified"] = matches;
            response["persisted"] = known ? (JToken)!unchanged : JValue.CreateNull();
            response["persistenceState"] = !known ? "unknown" : matches ? "requested" : unchanged ? "unchanged" : "partial";
            response["partialPersistenceDetected"] = known && !unchanged && !matches;
            response["beforeHash"] = Hash(before);
            response["requestedHash"] = Hash(requested);
            response["persistedHash"] = known ? Hash(observed) : null;
            response["snapshot"] = snapshot;
            response["saveAttempted"] = true;
            response["versionToken"] = known ? versionToken : null;
            response["source"] = known ? observed : null;
            response["persistedSnippet"] = known ? observed.Substring(0, System.Math.Min(800, observed.Length)) : null;
            response["changed"] = known ? (JToken)!unchanged : JValue.CreateNull();
            response["implicitLifecycleActions"] = new JArray();
            response["postSaveVerification"] = new JObject
            {
                ["reReadConfirmed"] = known, ["matches"] = known ? (JToken)matches : JValue.CreateNull(),
                ["versionToken"] = known ? versionToken : null,
                ["representation"] = "PatternInstance SDK XML"
            };
            response["rollback"] = new JObject
            {
                ["attempted"] = rollbackAttempted,
                ["verified"] = rollbackAttempted && unchanged,
                ["reason"] = committed ? "AtomicRollbackUnavailable" : rollbackError,
                ["error"] = rollbackError
            };
        }

        internal static string Complete(string target, JObject receipt)
        {
            if (receipt["verified"]?.Value<bool>() == true)
                return Models.McpResponse.Ok(target: target, code: "WriteApplied", result: receipt);
            return Models.McpResponse.Partial(target: target, code: "PatternVerificationMismatch", result: receipt,
                warnings: new JArray(new JObject { ["code"] = "PatternVerificationMismatch",
                    ["message"] = "The saved pattern differs from the request; inspect the returned source before retrying." }));
        }

        internal static void Promote(JObject response, string target, string part)
        {
            var receipt = response["persistenceState"] != null ? response : response["result"] as JObject;
            if (receipt == null) return;
            if (!object.ReferenceEquals(receipt, response))
                foreach (string key in new[] { "sdkSaveCompleted", "saved", "persistedStateKnown", "verified", "persisted",
                    "persistenceState", "partialPersistenceDetected", "beforeHash", "requestedHash", "persistedHash", "snapshot",
                    "rollback", "saveAttempted", "versionToken", "source", "persistedSnippet", "changed", "implicitLifecycleActions", "postSaveVerification" })
                    if (receipt[key] != null) response[key] = receipt[key].DeepClone();
            response["part"] = part;
            if (response["target"] == null) response["target"] = target;
        }

        private static string Hash(string value)
        {
            if (value == null) return null;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return "sha256:" + System.BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value)))
                    .Replace("-", "").ToLowerInvariant();
        }
    }
}
