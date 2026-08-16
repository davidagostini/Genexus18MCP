using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Builds the stable persistence evidence returned by mode=patch. It does
    /// not read, save, cache, or roll back GeneXus objects.
    /// </summary>
    internal static class PatchPersistenceReceipt
    {
        internal static bool AttachVerification(
            JObject payload,
            TextPersistenceVerifier.Result verification,
            string requestedReplacement,
            string originalContext,
            string savedSource,
            string persistedSource,
            string verifyMode,
            string partName,
            int matchCount,
            bool commentOnly = false)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (verification == null) throw new ArgumentNullException(nameof(verification));

            payload["requestedHash"] = verification.RequestedHash;
            payload["persistedHash"] = verification.PersistedHash;
            payload["normalizedRequestedHash"] = verification.NormalizedRequestedHash;
            payload["normalizedPersistedHash"] = verification.NormalizedPersistedHash;

            string canonicalReplacement = TextPersistenceVerifier.Canonicalize(requestedReplacement, verifyMode, partName);
            string canonicalPersisted = TextPersistenceVerifier.Canonicalize(persistedSource, verifyMode, partName);
            string canonicalOldContext = TextPersistenceVerifier.Canonicalize(originalContext, verifyMode, partName);
            int replacementMatchCount = canonicalReplacement.Length == 0
                ? 0
                : PatchTextEditor.CountOccurrences(canonicalPersisted, canonicalReplacement);
            int persistedMatchCount = canonicalOldContext.Length == 0
                ? 0
                : commentOnly
                    ? CommentOnlyPatch.CountActiveOccurrences(canonicalPersisted, canonicalOldContext)
                    : PatchTextEditor.CountOccurrences(canonicalPersisted, canonicalOldContext);
            bool replacementPresent = canonicalReplacement.Length == 0 || replacementMatchCount > 0;
            bool verified = verification.Matches && replacementPresent;

            JObject verificationJson = verification.ToJson(reReadConfirmed: verified);
            verificationJson["readCompleted"] = true;
            verificationJson["matchCount"] = matchCount;
            verificationJson["replacementMatchCount"] = replacementMatchCount;
            verificationJson["replacementPresent"] = replacementPresent;
            verificationJson["persistedMatchCount"] = persistedMatchCount;
            verificationJson["oldContentPresent"] = persistedMatchCount > 0;
            payload["persistedMatchCount"] = persistedMatchCount;
            payload["oldContentPresent"] = persistedMatchCount > 0;
            payload["replacementPresent"] = replacementPresent;
            payload["reReadConfirmed"] = verified;
            verificationJson["source"] = "fresh-sdk-read";
            payload["verification"] = verificationJson;
            AttachContentEvidence(payload, savedSource, savedSource, persistedSource);
            return verified;
        }

        internal static void AttachContentEvidence(
            JObject payload,
            string requestedSource,
            string savedSource,
            string reReadSource)
        {
            payload["content"] = new JObject
            {
                ["requested"] = Describe(requestedSource),
                ["saved"] = Describe(savedSource),
                ["reRead"] = Describe(reReadSource)
            };
        }

        internal static bool ShouldRollback(bool persistedMatches, bool rollbackOnFailure)
            => !persistedMatches && rollbackOnFailure;

        internal static void MarkVerified(JObject payload, bool saved)
        {
            payload.Remove("error");
            payload.Remove("mutation");
            payload.Remove("verificationWarning");
            payload["_internalStatus"] = "Success";
            payload["code"] = "Applied";
            payload["message"] = "Patch persisted and was confirmed by post-save re-read.";
            AttachOutcome(payload, saved, verified: true);
        }

        internal static void MarkNotPersisted(JObject payload, bool saved, string verifyError, bool commentOnly = false)
        {
            payload["_internalStatus"] = "Error";
            payload["code"] = commentOnly ? "CommentOnlyWriteNotPersisted" : "WriteNotPersisted";
            payload["message"] = commentOnly
                ? "The SDK save completed, but the forced Source re-read did not contain the requested comment-only change."
                : "The post-save re-read does not contain the requested patched content.";
            if (!string.IsNullOrWhiteSpace(verifyError)) payload["persistedVerifyError"] = verifyError;
            AttachOutcome(payload, saved, verified: false);
        }

        internal static void AttachOutcome(JObject payload, bool saved, bool verified)
        {
            payload["persistedVerified"] = verified;
            payload["persisted"] = verified;
            payload["saved"] = saved;
            payload["verified"] = verified;
        }

        internal static JObject BuildRollback(
            bool saved,
            TextPersistenceVerifier.Result verification,
            string error)
        {
            bool verified = verification != null && verification.Matches;
            return new JObject
            {
                ["requested"] = true,
                ["snapshotValid"] = true,
                ["saved"] = saved,
                ["verified"] = verified,
                ["requestedHash"] = verification?.RequestedHash,
                ["persistedHash"] = verification?.PersistedHash,
                ["error"] = verified ? JValue.CreateNull() : (JToken)(error ?? "Rollback could not be verified.")
            };
        }

        private static JToken Describe(string value)
        {
            if (value == null) return JValue.CreateNull();
            const int cap = 240;
            return new JObject
            {
                ["hash"] = TextPersistenceVerifier.Sha256(value),
                ["length"] = value.Length,
                ["snippet"] = value.Length <= cap ? value : value.Substring(0, cap) + "…[truncated]"
            };
        }
    }
}
