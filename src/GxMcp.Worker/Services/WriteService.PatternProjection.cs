using System;
using System.Linq;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public partial class WriteService
    {
        private string WriteProjectedPatternPart(KBObject host, KBObjectPart part, string target, string partName,
            string envelope, string requestedXml, Guid? patternId)
        {
            KBObject parent = WwpProjectionHelper.ResolveHostParent(host, _objectService);
            ObjectMoveSnapshot originalHost, originalParent;
            string originalPattern, originalWebForm;
            try
            {
                if (parent == null) throw new InvalidOperationException("The WorkWithPlus parent could not be resolved.");
                // Generic part serialization does not include all Pattern/WebForm
                // content. Preserve their editable XML explicitly as well.
                originalPattern = ReadProjectedPattern(host, partName, patternId, out _, fresh: false);
                originalWebForm = ReadProjectionWebForm(parent);
                originalHost = CaptureProjectionSnapshot(host);
                originalParent = CaptureProjectionSnapshot(parent);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "PatternSnapshotUnavailable", target: target,
                    message: "Complete host and parent snapshots are required before editing: " + ex.Message,
                    extra: new JObject { ["saveContract"] = PatternProjectionWriteFlow.SaveContract,
                        ["persisted"] = false, ["saveAttempted"] = false });
            }

            var receipt = new JObject
            {
                ["saveContract"] = PatternProjectionWriteFlow.SaveContract,
                ["part"] = partName, ["saveAttempted"] = false, ["commitCompleted"] = false,
                ["transactionRolledBack"] = false, ["rollbackAttempted"] = false,
                ["hostHashBefore"] = originalHost.Hash, ["parentHashBefore"] = originalParent.Hash,
                ["restorationVerification"] = new JObject { ["webFormXmlView"] = "sdk-materialized" },
                ["implicitLifecycleActions"] = new JArray()
            };
            ObjectMoveSnapshot projectedHost = null, projectedParent = null;
            string projectedPattern = null, projectedWebForm = null;
            Exception failure = null;
            var kb = host.KB;
            try
            {
                using (var transaction = kb.BeginTransaction())
                {
                    failure = PatternProjectionWriteFlow.Run(receipt, () =>
                    {
                        // Check both objects again at the transaction boundary. Never
                        // apply an envelope over a parent changed since the snapshot.
                        string boundaryPattern = ReadProjectedPattern(host, partName, patternId, out var boundaryHost);
                        var boundaryParent = FreshProjectionObject(parent);
                        // Use the same SDK materialization stage for each visual
                        // XML read, before the generic snapshot comparison.
                        string boundaryWebForm = ReadProjectionWebForm(boundaryParent);
                        var boundary = new JObject
                        {
                            ["host"] = ProjectionComparison(CompareProjectionSnapshot(originalHost, boundaryHost),
                                CompareProjectionXml(originalPattern, boundaryPattern), partName),
                            ["parent"] = ProjectionComparison(CompareProjectionSnapshot(originalParent, boundaryParent),
                                CompareProjectionXml(originalWebForm, boundaryWebForm, allowAbsent: true), "WebForm")
                        };
                        receipt["restorationVerification"]["boundary"] = boundary;
                        if (boundary["host"]["equal"].Value<bool>() != true
                            || boundary["parent"]["equal"].Value<bool>() != true)
                            throw new InvalidOperationException("The PatternInstance or parent changed before the write.");
                        receipt["saveAttempted"] = true;
                        ApplyPatternEnvelope(part, envelope, requestedXml);
                        PatternSemanticAttributeWriter.ApplyGxObjectAttributes(part, requestedXml);
                        try { part.Save(); }
                        catch (Exception ex)
                        {
                            // The owner save may complete even when a part-level save
                            // is rejected. The independent XML read below is decisive.
                            receipt["sdkSaveError"] = IsolatedEventsSaveDiagnostics.Sanitize(ex.Message);
                        }
                        host.Save(new KBObjectSavePreferences
                        {
                            ForceSave = true, ForceSaveDefaultParts = true, SkipValidation = true
                        });
                        receipt["sdkSaveCompleted"] = true;
                        receipt["applyOnSaveReenabled"] = WwpApplyOnSaveHelper.TryEnable(host);
                        receipt["savePhase"] = "parent-projection";
                        bool projected = WwpProjectionHelper.TryProjectHostOntoParent(parent, host, out var projection);
                        receipt["projection"] = projection.ToJson();
                        if (!projected)
                            throw new InvalidOperationException("The WorkWithPlus parent projection failed: " + projection.Failure);
                    }, () =>
                    {
                        string xml = ReadProjectedPattern(host, partName, patternId, out var freshHost);
                        projectedPattern = xml;
                        projectedWebForm = ReadProjectionWebForm(parent);
                        var freshParent = FreshProjectionObject(parent);
                        var xmlVerification = new JObject
                        {
                            ["pattern"] = CompareProjectionXml(requestedXml, xml),
                            ["webForm"] = CompareProjectionXml(projectedWebForm, ReadProjectionWebForm(freshParent),
                                allowAbsent: true, projectedWebForm: true)
                        };
                        receipt["projection"]["verification"] = xmlVerification;
                        if (xmlVerification["pattern"]["equal"].Value<bool>() != true)
                            throw new InvalidOperationException("The saved PatternInstance differs from the request.");
                        projectedHost = CaptureProjectionSnapshot(freshHost);
                        projectedParent = CaptureProjectionSnapshot(parent);
                        if (!CompareProjectionSnapshot(projectedParent, freshParent).Equal
                            || xmlVerification["webForm"]["equal"].Value<bool>() != true)
                            throw new InvalidOperationException("The independently read parent differs from the projected object.");
                    }, transaction.Commit, transaction.Rollback);
                }
            }
            catch (Exception ex)
            {
                failure = failure ?? ex;
                receipt["transactionFinalizationError"] = IsolatedEventsSaveDiagnostics.Sanitize(ex.Message);
            }

            bool? hostRestored = null, parentRestored = null, hostProjected = null, parentProjected = null, patternMatches = null;
            try
            {
                // The transaction has closed. A rollback return value alone is
                // never evidence that SDK callbacks restored either object.
                string xml = ReadProjectedPattern(host, partName, patternId, out var freshHost);
                var freshParent = FreshProjectionObject(parent);
                string webForm = ReadProjectionWebForm(freshParent);
                var hostState = ProjectionComparison(CompareProjectionSnapshot(originalHost, freshHost),
                    CompareProjectionXml(originalPattern, xml), partName);
                var parentState = ProjectionComparison(CompareProjectionSnapshot(originalParent, freshParent),
                    CompareProjectionXml(originalWebForm, webForm, allowAbsent: true), "WebForm");
                hostRestored = hostState["equal"].Value<bool>();
                parentRestored = parentState["equal"].Value<bool>();
                hostProjected = projectedHost == null ? (bool?)null
                    : ProjectionComparison(CompareProjectionSnapshot(projectedHost, freshHost),
                        CompareProjectionXml(projectedPattern, xml), partName)["equal"].Value<bool>();
                parentProjected = projectedParent == null ? (bool?)null
                    : ProjectionComparison(CompareProjectionSnapshot(projectedParent, freshParent),
                        CompareProjectionXml(projectedWebForm, webForm, allowAbsent: true, projectedWebForm: true), "WebForm")["equal"].Value<bool>();
                patternMatches = XmlEquivalence.AreEquivalent(xml, requestedXml, out _);
                receipt["source"] = xml;
                receipt["versionToken"] = ComputeContentVersionToken(freshHost, xml);
                receipt["restorationVerification"]["host"] = hostState;
                receipt["restorationVerification"]["parent"] = parentState;
            }
            catch (Exception ex)
            {
                receipt["verificationError"] = IsolatedEventsSaveDiagnostics.Sanitize(ex.Message);
            }
            bool verified = PatternProjectionWriteFlow.ConfirmReadback(receipt, hostRestored, parentRestored,
                hostProjected, parentProjected, patternMatches);
            if (receipt["saveAttempted"]?.Value<bool>() == true)
            {
                try { InvalidatePatternMutationCaches(_objectService?.GetKbService()?.GetIndexCache(), target, host.Name, parent.Name); }
                catch (Exception ex) { receipt["readCacheInvalidationError"] = IsolatedEventsSaveDiagnostics.Sanitize(ex.Message); }
                if (receipt["rollbackVerified"]?.Value<bool>() != true)
                {
                    NotePerTargetWrite(target);
                    NotePerTargetWrite(parent.Name);
                }
                try { ScheduleFlush(force: true); }
                catch (Exception ex) { receipt["flushError"] = IsolatedEventsSaveDiagnostics.Sanitize(ex.Message); }
            }
            if (verified && failure == null)
                return McpResponse.Ok(code: "WriteApplied", target: target, result: receipt);
            return McpResponse.Err(code: "PatternProjectionFailed", target: target,
                message: failure == null
                    ? "The committed projection could not be confirmed by a fresh read; inspect the returned state before another write."
                    : IsolatedEventsSaveDiagnostics.Sanitize(failure.Message),
                hint: "Inspect the PatternInstance and parent state before retrying. No compensating write was attempted.",
                extra: receipt);
        }

        private KBObject FreshProjectionObject(KBObject expected) => _objectService.FindObjectFreshByIdentity(expected)
            ?? throw new InvalidOperationException("A fresh read of the same native object identity was unavailable.");

        private string ReadProjectedPattern(KBObject host, string partName, Guid? patternId, out KBObject freshHost, bool fresh = true)
        {
            JObject diagnostic;
            string xml = fresh
                ? _patternAnalysisService.ReadPatternPartXmlFresh(host, partName, patternId, out freshHost, out _, out diagnostic)
                : _patternAnalysisService.ReadPatternPartXml(host, partName, patternId, out freshHost, out _, out diagnostic);
            if (diagnostic != null || freshHost == null || freshHost.Guid != host.Guid || string.IsNullOrWhiteSpace(xml))
                throw new InvalidOperationException("Fresh PatternInstance verification was unavailable.");
            return xml;
        }

        internal static void PrepareProjectionSnapshotSources(System.Collections.Generic.IEnumerable<ISource> sources)
        {
            foreach (var source in sources) { _ = source.Source; }
        }

        private static ObjectMoveSnapshot CaptureProjectionSnapshot(KBObject obj)
        {
            PrepareProjectionSnapshotSources(obj.Parts.Cast<KBObjectPart>().OfType<ISource>());
            return ObjectMoveSnapshot.Capture(obj);
        }

        private static ObjectMoveSnapshot.Comparison CompareProjectionSnapshot(ObjectMoveSnapshot snapshot, KBObject obj)
        {
            PrepareProjectionSnapshotSources(obj.Parts.Cast<KBObjectPart>().OfType<ISource>());
            return snapshot.Compare(obj);
        }

        private static string ReadProjectionWebForm(KBObject parent)
        {
            // Procedure/Services hosts can have no visual part. Absence is a
            // state to compare; an unreadable existing part remains an error.
            if (WebFormXmlHelper.GetWebFormPart(parent) == null) return null;
            return ReadMaterializedProjectionXml(() => CaptureProjectionSnapshot(parent),
                () => WebFormXmlHelper.ReadEditableXml(parent));
        }

        internal static string ReadMaterializedProjectionXml(Action materialize, Func<string> readXml)
        {
            // Save/SerializeData materializes SDK defaults such as PanelType.
            // Warm fresh objects the same way as already-saved projected objects.
            materialize();
            string xml = readXml();
            if (string.IsNullOrWhiteSpace(xml))
                throw new InvalidOperationException("The complete parent WebForm XML was unavailable.");
            return xml;
        }

        // A state comparison must be symmetric: the shared XML comparer also
        // admits requested subsets for Report writes, which is insufficient here.
        internal static JObject CompareProjectionXml(string expected, string actual, bool allowAbsent = false, bool projectedWebForm = false)
        {
            string difference = null;
            bool equal = allowAbsent && expected == null && actual == null;
            if (!equal && !string.IsNullOrWhiteSpace(expected) && !string.IsNullOrWhiteSpace(actual))
            {
                string expectedComparison = expected, actualComparison = actual;
                if (projectedWebForm)
                {
                    // Compare SDK-generated structural ids only in projected/fresh
                    // native WebForms. Pattern, boundary and rollback stay strict.
                    try
                    {
                        var left = XDocument.Parse(expected, LoadOptions.PreserveWhitespace);
                        var right = XDocument.Parse(actual, LoadOptions.PreserveWhitespace);
                        LayoutStructuralIds.NormalizeWebForms(left, right);
                        expectedComparison = left.ToString(SaveOptions.DisableFormatting);
                        actualComparison = right.ToString(SaveOptions.DisableFormatting);
                    }
                    catch (System.Xml.XmlException) { } // The comparer below reports malformed XML.
                }
                equal = XmlEquivalence.AreEquivalent(expectedComparison, actualComparison, out difference)
                    && XmlEquivalence.AreEquivalent(actualComparison, expectedComparison, out difference);
            }
            if (!equal && difference == null) difference = "One side is absent or its XML is empty.";
            return new JObject
            {
                ["equal"] = equal, ["difference"] = IsolatedEventsSaveDiagnostics.Sanitize(difference),
                ["comparisonMode"] = projectedWebForm ? "projected-webform-structural-ids" : "strict",
                ["expectedPresent"] = expected != null, ["persistedPresent"] = actual != null,
                ["expectedHash"] = ComputeSha256(expected), ["persistedHash"] = ComputeSha256(actual),
                ["expectedLength"] = expected?.Length ?? 0, ["persistedLength"] = actual?.Length ?? 0
            };
        }

        internal static JObject ProjectionComparison(ObjectMoveSnapshot.Comparison comparison, JObject xmlComparison, string partName)
        {
            bool xmlEqual = xmlComparison["equal"].Value<bool>();
            var changedParts = (JArray)comparison.ChangedParts.DeepClone();
            if (!xmlEqual && !changedParts.Values<string>().Contains(partName, StringComparer.OrdinalIgnoreCase))
                changedParts.Add(partName);
            return new JObject
            {
                ["equal"] = comparison.Equal && xmlEqual, ["objectSnapshotEqual"] = comparison.Equal,
                ["changedParts"] = changedParts, ["persistedHash"] = comparison.PersistedHash,
                [partName] = xmlComparison
            };
        }
    }
}
