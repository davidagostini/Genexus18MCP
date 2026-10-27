using System;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public partial class WriteService
    {
        internal JObject InspectEventsIsolation(string target, string typeFilter)
        {
            using (SdkGate.Enter())
            {
                try
                {
                    var seed = _objectService.FindObject(target, typeFilter);
                    var isolation = EventsSaveIsolation.Preflight(seed);
                    var original = EventsSaveIsolation.Fresh(seed.KB, seed.Guid);
                    var snapshot = ObjectMoveSnapshot.Capture(original);
                    if (!snapshot.Compare(EventsSaveIsolation.Fresh(seed.KB, seed.Guid)).Equal)
                        throw new InvalidOperationException("The canonical SDK snapshot changed during preflight.");
                    isolation["canonicalSnapshotVerified"] = true;
                    isolation["preservedParts"] = snapshot.PreservedParts;
                    isolation["canonicalParts"] = snapshot.DescribeParts("Events");
                    return isolation;
                }
                catch (Exception ex)
                {
                    return EventsSaveIsolation.DescribeRefusal(ex);
                }
            }
        }

        internal string WriteIsolatedEvents(string target, string source, string typeFilter, string baseVersion)
        {
            using (SdkGate.Enter())
            {
                var receipt = new JObject();
                KBObject verifiedState;
                Guid guid;
                string response;
                try
                {
                    response = WriteIsolatedEventsCore(target, source, typeFilter, baseVersion, receipt, out verifiedState, out guid);
                }
                finally
                {
                    // Rollback and interrupted Save also leave cached SDK-derived
                    // reads stale. Invalidate every alias without touching the SDK.
                    if (receipt["mutationAttempted"]?.Value<bool>() == true)
                        InvalidateIsolatedEventsReadCaches();
                }

                if (receipt["mutationAttempted"]?.Value<bool>() != true) return response;
                var envelope = JObject.Parse(response);
                var body = envelope["result"] as JObject ?? envelope;
                body["readCachesInvalidated"] = true;
                bool dirty = ShouldMarkIsolatedEventsDirty(receipt);
                if (dirty) NotePerTargetWrite(target);
                body["targetMarkedDirty"] = dirty;
                try
                {
                    var index = _objectService.GetKbService()?.GetIndexCache();
                    // Drop the old entry first. Never refresh from the pre-save seed
                    // or from a speculative in-transaction instance.
                    if (index?.TryGetLoadedIndex() != null) index.RemoveEntryByGuid(guid.ToString());
                    if (verifiedState != null && !SdkEventSuppressionScope.IsPoisoned)
                    {
                        index?.UpdateEntry(verifiedState);
                        body["indexRefresh"] = index == null ? "unavailable" : "fresh-sdk-state";
                    }
                    else body["indexRefresh"] = index?.TryGetLoadedIndex() == null ? "not-loaded" : "entry-invalidated";
                }
                catch (Exception ex)
                {
                    // A cache refresh must not hide a committed/partial save receipt.
                    body["indexRefreshError"] = ex.Message;
                }
                return envelope.ToString();
            }
        }

        internal static void InvalidateIsolatedEventsReadCaches()
        {
            PatchService.InvalidateAllSourceCaches();
            ObjectReader.InvalidateAll();
            ObjectService.InvalidateAllReadCaches();
            ListService.InvalidateCache();
            SummarizeService.InvalidateCache();
        }

        // Mirrors WriteObject's outcome-based dirty tracking: refusal, verified
        // rollback and a verified content no-op stay clean; uncertain persistence
        // remains dirty. This path returns a canonical envelope, not WriteApplied.
        internal static bool ShouldMarkIsolatedEventsDirty(JObject receipt)
        {
            if (receipt?["writeAttempted"]?.Value<bool>() != true
                || receipt["rollbackVerified"]?.Value<bool>() == true) return false;
            bool verifiedNoContentChange = receipt["verificationCompleted"]?.Value<bool>() == true
                && receipt["objectSaved"]?.Value<bool>() == true
                && receipt["partPersisted"]?.Value<bool>() == true
                && receipt["otherPartsIntact"]?.Value<bool>() == true
                && receipt["otherObjectMetadataIntact"]?.Value<bool>() == true
                && receipt["sourceChanged"]?.Value<bool?>() == false
                && receipt["persistenceUncertain"]?.Value<bool>() != true;
            return !verifiedNoContentChange;
        }

        private string WriteIsolatedEventsCore(string target, string source, string typeFilter, string baseVersion,
            JObject receipt, out KBObject verifiedState, out Guid guid)
        {
            verifiedState = null;
            guid = Guid.Empty;
            using (SdkGate.Enter())
            {
                KBObject seed = _objectService.FindObject(target, typeFilter);
                if (seed != null
                    && K2bWebPanelDesignerService.TryBuildEditRejection(
                        seed, "Events", source, out string k2bDesignerRejection))
                    return k2bDesignerRejection;
                JObject isolation;
                try { isolation = EventsSaveIsolation.Preflight(seed); }
                catch (Exception ex)
                {
                    var failure = EventsSaveIsolation.DescribeRefusal(ex);
                    failure["writeAttempted"] = false;
                    failure["partPersisted"] = false;
                    failure["objectSaved"] = false;
                    failure["savePhase"] = IsolatedEventsSavePhase.Preflight;
                    return McpResponse.Err(code: "ObjectSaveIsolationUnverified", target: target,
                        message: failure["reason"]?.ToString() ?? ex.Message, extra: failure);
                }
                var kb = seed.KB;
                guid = seed.Guid;
                receipt.Merge(new JObject
                {
                    ["requireObjectSave"] = true, ["persistencePath"] = "sdk_object_force_save_isolated",
                    ["isolation"] = isolation, ["writeAttempted"] = false,
                    ["objectSaveInvoked"] = false, ["objectSaved"] = false, ["partPersisted"] = false,
                    ["metadataUpdated"] = false, ["metadataStampPersisted"] = false,
                    ["implicitOperations"] = new JArray(), ["retrySafe"] = false
                });
                // The audited U16 callback chain must run for a complete object
                // save; suppressing it prevents the Events source from being
                // materialized by the SDK. Preflight fingerprints every loaded
                // callback assembly, while the pattern scope disables only the
                // known WWP reapplication path.
                using (var steps = new SdkIsolatedEventsSaveSteps(kb, guid, target, source, isolation, receipt))
                {
                    var trace = new IsolatedEventsSaveTrace(receipt, target);
                    var outcome = IsolatedEventsSaveFlow.Run(steps, trace, baseVersion, KbWatcherService.AcquireWriteGate);
                    verifiedState = steps.VerifiedState;
                    switch (outcome.Kind)
                    {
                        case IsolatedEventsSaveOutcomeKind.VersionConflict:
                            return McpResponse.Err(code: "VersionConflict", target: target,
                                message: outcome.Message,
                                extra: new JObject
                                {
                                    ["currentVersion"] = outcome.CurrentVersion,
                                    ["writeAttempted"] = false,
                                    ["savePhase"] = outcome.Phase
                                });
                        case IsolatedEventsSaveOutcomeKind.Verified:
                            return McpResponse.Ok(code: "Applied", target: target, result: receipt);
                        default:
                            return McpResponse.Err(code: "ObjectSaveIncomplete", target: target,
                                message: outcome.Message, extra: receipt);
                    }
                }
            }
        }
    }
}
