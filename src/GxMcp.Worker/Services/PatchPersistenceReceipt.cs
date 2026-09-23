using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// How far the write intent of a mode=patch call had progressed when an
    /// unexpected exception aborted it. Only <see cref="BeforeWrite"/> proves
    /// that nothing was persisted.
    /// </summary>
    internal enum PatchWriteStage
    {
        /// <summary>No write call was started, so no save can have happened.</summary>
        BeforeWrite = 0,
        /// <summary>A write call was entered and never returned; the persisted state is unknown.</summary>
        DuringWrite = 1,
        /// <summary>The write call returned and a later step threw; post-save verification is unknown.</summary>
        AfterWrite = 2
    }

    /// <summary>
    /// Builds the stable persistence evidence returned by mode=patch. It does
    /// not read, save, cache, or roll back GeneXus objects.
    /// </summary>
    internal static class PatchPersistenceReceipt
    {
        private const string UnknownOutcomeRecovery =
            "Do not retry and do not roll back automatically. Read the complete part again and compare it with the requested content before any further edit.";

        /// <summary>
        /// True when an unexpected failure at this stage leaves the target's persisted
        /// state unknown, so the target must be treated as written (dirty) even though
        /// the call failed. Only <see cref="PatchWriteStage.BeforeWrite"/> proves the
        /// target is untouched; assuming that for the other stages would let a later
        /// build take the compile-only fast path over a possibly-changed object.
        /// </summary>
        internal static bool RequiresDirtyTargetMark(PatchWriteStage stage)
            => stage != PatchWriteStage.BeforeWrite;

        /// <summary>
        /// Rewrites the envelope produced for an unexpected exception so it reports
        /// what is actually known about persistence. A failure raised before any
        /// write call keeps the generic error contract and states that no save was
        /// attempted. Once a write call has been entered, neither this process nor a
        /// retry can prove the persisted state, so the outcome is reported as unknown
        /// (<c>persisted: null</c>, <c>persistedStateKnown: false</c>) and the caller
        /// is told to re-read the part. This method never writes, retries, or rolls
        /// anything back.
        /// </summary>
        internal static string AttachUnexpectedFailureOutcome(
            string envelope,
            PatchWriteStage stage,
            bool? sdkSaveCompleted,
            string target,
            string partName,
            string failureType)
        {
            JObject payload;
            try { payload = JObject.Parse(envelope); }
            catch { return envelope; }

            payload["part"] = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;
            payload["writeStage"] = StageName(stage);
            if (!string.IsNullOrWhiteSpace(failureType)) payload["failureType"] = failureType;
            if (payload["target"] == null && !string.IsNullOrWhiteSpace(target)) payload["target"] = target;

            if (stage == PatchWriteStage.BeforeWrite)
            {
                // The exception was raised before any write call, so "nothing was
                // persisted" is an observation here rather than an assumption.
                payload["writeAttempted"] = false;
                payload["saveAttempted"] = false;
                payload["sdkSaveCompleted"] = false;
                payload["saved"] = false;
                payload["persisted"] = false;
                payload["persistedStateKnown"] = true;
                payload["verified"] = false;
                return payload.ToString();
            }

            bool afterWrite = stage == PatchWriteStage.AfterWrite;
            payload["writeAttempted"] = true;
            payload["saveAttempted"] = true;
            // Only a write call that returned can report whether the SDK save ran.
            payload["sdkSaveCompleted"] = afterWrite && sdkSaveCompleted.HasValue
                ? (JToken)sdkSaveCompleted.Value
                : JValue.CreateNull();
            payload["saved"] = payload["sdkSaveCompleted"].DeepClone();
            // `persisted: false` would be a claim this aborted run cannot support.
            payload["persisted"] = JValue.CreateNull();
            payload["persistedStateKnown"] = false;
            // `verified: false` means the post-save re-read did not confirm the
            // content. It is not evidence that the content was not written.
            payload["verified"] = false;
            payload["verificationUnavailable"] = true;
            payload["retrySafe"] = false;
            payload["retriable"] = false;
            payload["retryable"] = false;

            payload["postSaveVerification"] = new JObject
            {
                ["reReadConfirmed"] = false,
                ["matches"] = JValue.CreateNull(),
                ["readCompleted"] = false,
                ["reason"] = afterWrite
                    ? "The patch aborted after the SDK write call returned; no post-save read completed."
                    : "The patch aborted while the SDK write call was running; no post-save read was performed."
            };
            payload["rollback"] = new JObject
            {
                ["attempted"] = false,
                ["saveAttempted"] = false,
                ["rolledBack"] = false,
                ["verificationUnavailable"] = true,
                ["error"] = "Rollback was not attempted because the post-write state is unknown."
            };
            payload["rolledBack"] = false;
            payload["manualRecovery"] = UnknownOutcomeRecovery;

            var error = payload["error"] as JObject;
            if (error == null)
            {
                error = new JObject();
                payload["error"] = error;
            }
            string failureDetail = error["message"]?.ToString();
            if (!string.IsNullOrWhiteSpace(failureDetail))
            {
                error["failureDetail"] = failureDetail;
                if (payload["details"] == null) payload["details"] = failureDetail;
            }
            error["code"] = "PatchWriteOutcomeUnknown";
            error["message"] = afterWrite
                ? "The SDK write call returned, but the patch failed before persistence could be verified; the persisted state is unknown."
                : "The patch failed while the SDK write call was running; whether the change was persisted is unknown.";
            error["hint"] = UnknownOutcomeRecovery;
            error["nextSteps"] = new JArray(Models.McpResponse.NextStep(
                tool: "genexus_read",
                args: new JObject
                {
                    ["name"] = string.IsNullOrWhiteSpace(target) ? "(target)" : target,
                    ["part"] = payload["part"].DeepClone()
                },
                why: "Read the complete current part and decide from its actual content; this response cannot say whether the write landed."));
            return payload.ToString();
        }

        private static string StageName(PatchWriteStage stage)
        {
            switch (stage)
            {
                case PatchWriteStage.DuringWrite: return "sdk-write";
                case PatchWriteStage.AfterWrite: return "post-write";
                default: return "pre-write";
            }
        }

        internal static string ObjectSaveIsolationGuard(string target, bool requireObjectSave, bool dryRun)
        {
            if (!requireObjectSave || dryRun) return null;
            return Models.McpResponse.Err(
                code: "ObjectSaveIsolationUnverified",
                message: "No write was attempted: complete object save can run SDK/pattern event handlers whose isolation has not been verified.",
                target: target,
                extra: new JObject
                {
                    ["requireObjectSave"] = true, ["persisted"] = false, ["saved"] = false,
                    ["partPersisted"] = false, ["objectSaved"] = false,
                    ["metadataUpdated"] = false, ["implicitOperations"] = new JArray(),
                    ["writeAttempted"] = false
                });
        }

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
            payload["source"] = persistedSource;
            payload.Remove("partialPersistenceDetected");
            payload.Remove("verificationWarning");
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

        internal static bool CanAttemptRollback(
            bool persistedMatches,
            bool rollbackOnFailure,
            string observedPersistedVersion)
            => ShouldRollback(persistedMatches, rollbackOnFailure)
                && !string.IsNullOrWhiteSpace(observedPersistedVersion);

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
            payload["saveAttempted"] = saved;
            AttachOutcome(payload, saved, verified: false);
        }

        internal static void MarkVerificationUnavailable(JObject payload, bool saveAttempted, string reason)
        {
            var verification = payload["verification"] as JObject ?? new JObject();
            payload.Remove("error");
            payload.Remove("mutation");
            payload.Remove("source");
            payload.Remove("persistedHash");
            payload.Remove("persistedSnippet");
            payload.Remove("changed");
            payload.Remove("partialPersistenceDetected");
            payload.Remove("verificationWarning");
            payload["_internalStatus"] = "Error";
            payload["code"] = "WriteVerificationUnavailable";
            payload["message"] = saveAttempted
                ? "The SDK save completed, but the complete post-save read could not confirm persistence."
                : "The operation did not report a new save, and the complete post-save read could not confirm persistence.";
            payload["hint"] = "Do not retry blindly. Re-read the complete part or recover from the pre-write snapshot before attempting another edit.";
            payload["verificationUnavailable"] = true;
            verification["readCompleted"] = false;
            verification["reReadConfirmed"] = false;
            verification["reason"] = reason ?? "unknown";
            payload["verification"] = verification;
            payload["postSaveVerification"] = new JObject
            {
                ["reReadConfirmed"] = false,
                ["reason"] = reason ?? "unknown"
            };
            if (!string.IsNullOrWhiteSpace(reason)) payload["persistedVerifyError"] = reason;
            var content = payload["content"] as JObject;
            if (content != null)
            {
                content["saved"] = JValue.CreateNull();
                content["reRead"] = JValue.CreateNull();
            }
            AttachOutcome(payload, saved: false, verified: false);
            payload["saveAttempted"] = saveAttempted;
        }

        internal static void MarkRollbackNotAttempted(
            JObject payload,
            string reason,
            bool verificationUnavailable = true)
        {
            if (payload == null) return;
            payload["rollback"] = new JObject
            {
                ["requested"] = true,
                ["snapshotValid"] = true,
                ["attempted"] = false,
                ["saveAttempted"] = false,
                ["verified"] = false,
                ["rolledBack"] = false,
                ["verificationUnavailable"] = verificationUnavailable,
                ["error"] = reason ?? "Rollback was not attempted."
            };
            payload["rolledBack"] = false;
        }

        internal static void AttachOutcome(JObject payload, bool saved, bool verified)
        {
            payload["persistedVerified"] = verified;
            payload["persisted"] = verified;
            // `saved` is a persistence claim, not an SDK call-return signal. A
            // write whose post-save read did not confirm the requested content
            // must never be exposed as saved=true.
            payload["saved"] = saved && verified;
            payload["verified"] = verified;
        }

        internal static bool AttachObjectSaveEvidence(
            JObject payload,
            bool partPersisted,
            bool objectSaved,
            string revisionBefore,
            string revisionAfter,
            string lastUpdateBefore,
            string lastUpdateAfter,
            bool? otherPartsIntact,
            bool metadataStampPersisted = false,
            JArray unexpectedChangedParts = null)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            bool metadataUpdated = MetadataChanged(
                revisionBefore, revisionAfter, lastUpdateBefore, lastUpdateAfter, metadataStampPersisted);
            payload["partPersisted"] = partPersisted;
            payload["objectSaved"] = objectSaved;
            payload["revisionBefore"] = revisionBefore == null ? JValue.CreateNull() : (JToken)revisionBefore;
            payload["revisionAfter"] = revisionAfter == null ? JValue.CreateNull() : (JToken)revisionAfter;
            payload["lastUpdateBefore"] = lastUpdateBefore == null ? JValue.CreateNull() : (JToken)lastUpdateBefore;
            payload["lastUpdateAfter"] = lastUpdateAfter == null ? JValue.CreateNull() : (JToken)lastUpdateAfter;
            payload["metadataStampPersisted"] = metadataStampPersisted;
            payload["metadataUpdated"] = metadataUpdated;
            if (otherPartsIntact.HasValue) payload["otherPartsIntact"] = otherPartsIntact.Value;
            if (unexpectedChangedParts != null && unexpectedChangedParts.Count > 0)
                payload["unexpectedChangedParts"] = unexpectedChangedParts;
            return metadataUpdated;
        }

        internal static bool MetadataChanged(
            string revisionBefore,
            string revisionAfter,
            string lastUpdateBefore,
            string lastUpdateAfter,
            bool metadataStampPersisted = false)
        {
            if (!metadataStampPersisted)
                return false;

            long beforeRevision;
            long afterRevision;
            if (long.TryParse(revisionBefore, out beforeRevision)
                && long.TryParse(revisionAfter, out afterRevision)
                && afterRevision > beforeRevision)
                return true;

            DateTime before;
            DateTime after;
            return DateTime.TryParse(lastUpdateBefore, null,
                       System.Globalization.DateTimeStyles.RoundtripKind, out before)
                   && DateTime.TryParse(lastUpdateAfter, null,
                       System.Globalization.DateTimeStyles.RoundtripKind, out after)
                   && after.ToUniversalTime() > before.ToUniversalTime();
        }

        internal static void RequireCompleteObjectSave(JObject payload)
        {
            if (payload["requireObjectSave"]?.Value<bool>() != true) return;
            if (payload["objectSaved"]?.Value<bool>() == true
                && payload["partPersisted"]?.Value<bool>() == true
                && payload["metadataUpdated"]?.Value<bool>() == true
                && payload["otherPartsIntact"]?.Value<bool?>() == true) return;

            payload["_internalStatus"] = "Error";
            payload["code"] = "ObjectSaveIncomplete";
            payload["message"] = payload["partPersisted"]?.Value<bool>() == true
                ? "The Events content is persisted, but the complete object-save contract was not confirmed. Do not repeat the edit blindly."
                : "The complete object-save contract was not confirmed and the requested Events content was not found by the fresh re-read.";
            payload["manualRecovery"] = "Compare the fresh Events content with the object open in the GeneXus IDE. If the IDE tab is older, reopen it before saving the object manually so the persisted content is not overwritten.";
            payload["retrySafe"] = false;
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
                ["attempted"] = true,
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
