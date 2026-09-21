using System;
using System.Linq;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Packages.Patterns.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Structure;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed partial class WwpActionService
    {
        private string RunGridColumnOperation(string target, string operation, JObject args)
        {
            string expected = (string)(args["expectedVersion"] ?? args["baseVersion"] ?? args["versionToken"]);
            if (string.IsNullOrWhiteSpace(expected))
                return McpResponse.Err(code: "ExpectedVersionRequired", target: target,
                    message: "A PatternInstance read token is required, including for dryRun.");
            KBObject requested = _objects.FindObject(target, guid: (string)args["guid"], entityKey: (string)args["entityKey"]);
            if (requested == null) return McpResponse.Err(code: "ObjectNotFound", message: "Target not found unambiguously.", target: target,
                nextSteps: new JArray(McpResponse.NextStep("genexus_search", new JObject { ["query"] = target }, "Resolve the exact parent or pattern instance identity.")));
            var host = requested as PatternInstance;
            if (host == null)
            {
                var children = requested.Model.Objects.GetChildren(requested).OfType<PatternInstance>()
                    .Where(p => p.TypeDescriptor?.Name == "WorkWithPlus" && p.KBObject?.Guid == requested.Guid).ToList();
                if (children.Count != 1) return McpResponse.Err(code: "WwpIdentityUnavailable", target: target,
                    message: "Exactly one WorkWithPlus instance bound to the target identity is required.");
                host = children[0];
            }
            if (host.TypeDescriptor?.Name != "WorkWithPlus" || host.KBObject == null)
                return McpResponse.Err(code: "WwpIdentityUnavailable", target: target, message: "WorkWithPlus parent identity is unavailable.");
            Guid hostId = host.Guid, parentId = host.KBObject.Guid;
            var kb = host.KB;
            // Keep coordination with existing name-keyed writers and serialize aliases
            // of these identity-based operations. All take name before identity.
            lock (WriteService.AcquirePerTargetLock(target))
            lock (WriteService.AcquirePerTargetLock(hostId.ToString()))
            {
                host = (PatternInstance)EventsSaveIsolation.Fresh(kb, hostId);
                KBObject parent = EventsSaveIsolation.Fresh(kb, parentId);
                string original = GridPatternText(host);
                string token = WriteService.ComputeContentVersionToken(host, original);
                if (!string.Equals(expected, token, StringComparison.Ordinal))
                    return McpResponse.Err(code: "StaleObject", target: target, message: "PatternInstance version changed; no save attempted.",
                        extra: new JObject { ["currentVersion"] = token, ["persisted"] = false });
                XDocument before = XDocument.Parse(original, LoadOptions.PreserveWhitespace);
                XDocument planned = new XDocument(before);
                JObject diff = ApplyGridColumnXml(planned, operation, args);
                if (diff["error"] != null) return McpResponse.Err(code: (string)diff["code"], message: (string)diff["error"], target: target);
                diff["changed"] = !XNode.DeepEquals(GridComparable(before.Root), GridComparable(planned.Root));
                JArray variables = GridVariables(parent);
                if (operation == "add_grid_variable" && variables.Any(v => string.Equals((string)v["name"], (string)args["variable"], StringComparison.OrdinalIgnoreCase)))
                    return McpResponse.Err(code: "VariableAlreadyExists", message: "The parent already declares this variable; no rebinding is allowed.", target: target);
                var receipt = new JObject
                {
                    ["operation"] = operation, ["instanceGuid"] = hostId.ToString(), ["parentGuid"] = parentId.ToString(),
                    ["versionToken"] = token, ["diff"] = diff, ["persisted"] = false, ["saved"] = false,
                    ["sdkSaveCompleted"] = false, ["persistedStateKnown"] = true,
                    ["implicitLifecycleActions"] = new JArray(), ["rollbackPerformed"] = false
                };
                if (args["dryRun"]?.Value<bool>() == true)
                    return McpResponse.Ok(target: target, code: "WwpGridColumnDryRun", result: receipt);
                if (diff["changed"]?.Value<bool>() == false)
                    return McpResponse.Ok(target: target, code: "WriteNoChange", result: receipt);
                string form = GridPartText(parent, "WebForm"), events = GridPartText(parent, "Events");
                var hostSnapshot = ObjectMoveSnapshot.Capture(host);
                var parentSnapshot = ObjectMoveSnapshot.Capture(parent);
                var snapshots = CaptureSnapshots(host, original, parent, form);
                if (snapshots.Pattern == null || snapshots.WebForm == null)
                    throw new InvalidOperationException("Complete PatternInstance and WebForm snapshots are required.");
                string snapshotRoot = EditSnapshotStore.ResolveRoot(kb.Location);
                var eventsSnapshot = EditSnapshotStore.SaveSnapshot(snapshotRoot, parentId.ToString(), "Events", events);
                var variablesSnapshot = EditSnapshotStore.SaveSnapshot(snapshotRoot, parentId.ToString(), "Variables", variables.ToString());
                if (eventsSnapshot == null || variablesSnapshot == null) throw new InvalidOperationException("Events and Variables snapshots are required.");
                receipt["snapshot"] = snapshots.ToJson();
                receipt["snapshot"]["events"] = eventsSnapshot.Path;
                receipt["snapshot"]["variables"] = variablesSnapshot.Path;
                string applyBefore = ReadObjectProperty(host, "SDPlus_Editor_Apply_On_Save");
                bool committed = false, rolledBack = false, saveReturned = false;
                string failure = null;
                try
                {
                using (var transaction = kb.BeginTransaction())
                {
                    try
                    {
                        host = (PatternInstance)EventsSaveIsolation.Fresh(kb, hostId);
                        parent = EventsSaveIsolation.Fresh(kb, parentId);
                        if (host.KBObject?.Guid != parentId || WriteService.ComputeContentVersionToken(host, GridPatternText(host)) != expected
                            || !hostSnapshot.Compare(host).Equal || !parentSnapshot.Compare(parent).Equal)
                            throw new WwpTabException("StaleObject", "Host or parent changed at the transaction boundary; no save attempted.");
                        _patterns.BuildPatternPartEnvelope(host, "PatternInstance", original, out _, out KBObjectPart part);
                        JObject mutation = ApplyNativeGridColumnMutation(part, operation, args);
                        if (mutation["error"] != null) throw new WwpTabException((string)mutation["code"], (string)mutation["error"]);
                        WriteService.ForcePatternPartDirty(part);
                        receipt["saveAttempted"] = true;
                        // One normal object save, no ForceSave, retry, explicit apply or build callbacks.
                        host.Save(new KBObjectSavePreferences());
                        saveReturned = true;
                        parent = EventsSaveIsolation.Fresh(kb, parentId);
                        JObject check = CheckGridColumnState(before, planned, host, parent, operation, args, form, events, variables,
                            hostSnapshot, parentSnapshot, applyBefore);
                        receipt["preCommitVerification"] = check;
                        if (check["confirmed"]?.Value<bool>() != true)
                            throw new WwpTabException("WwpGridProjectionMismatch", "The normal pattern save did not produce the isolated requested state; transaction aborted.");
                        transaction.Commit();
                        committed = true;
                    }
                    catch (Exception ex)
                    {
                        failure = (ex.InnerException ?? ex).Message;
                        receipt["failureCode"] = (ex as WwpTabException)?.Code ?? "WwpGridSaveFailed";
                        try { transaction.Rollback(); rolledBack = true; }
                        catch (Exception rollbackError) { receipt["rollbackError"] = rollbackError.Message; }
                    }
                }
                }
                catch (Exception finalizationError)
                {
                    failure = finalizationError.Message;
                    receipt["transactionFinalizationError"] = failure;
                }
                receipt["objectSaveReturned"] = saveReturned;
                receipt["sdkSaveCompleted"] = saveReturned && committed;
                receipt["commitCompleted"] = committed;
                receipt["rollbackPerformed"] = rolledBack;
                try
                {
                    host = (PatternInstance)EventsSaveIsolation.Fresh(kb, hostId);
                    parent = EventsSaveIsolation.Fresh(kb, parentId);
                    string persistedXml = GridPatternText(host);
                    receipt["versionToken"] = WriteService.ComputeContentVersionToken(host, persistedXml);
                    bool originalRestored = hostSnapshot.Compare(host).Equal && parentSnapshot.Compare(parent).Equal;
                    if (!committed && originalRestored)
                    {
                        receipt["stateRestoredExactly"] = true;
                        receipt["partialPersistenceDetected"] = false;
                        receipt["rollback"] = new JObject { ["performed"] = rolledBack, ["verified"] = rolledBack };
                        return McpResponse.Err(code: (string)receipt["failureCode"] ?? "WwpGridColumnRolledBack", target: target,
                            message: failure ?? "No change persisted; the original state was independently confirmed.", extra: receipt);
                    }
                    var verification = CheckGridColumnState(before, planned, host, parent, operation, args, form, events, variables,
                        hostSnapshot, parentSnapshot, applyBefore);
                    bool matches = verification["confirmed"]?.Value<bool>() == true;
                    receipt["postSaveVerification"] = verification;
                    receipt["persisted"] = matches;
                    receipt["saved"] = committed ? new JValue(true) : JValue.CreateNull();
                    receipt["stateRestoredExactly"] = originalRestored;
                    receipt["rollback"] = new JObject { ["performed"] = rolledBack, ["verified"] = rolledBack && originalRestored,
                        ["code"] = committed && !matches ? "AtomicRollbackUnavailable" : null };
                    receipt["partialPersistenceDetected"] = !matches && !originalRestored;
                    if (committed && matches)
                    {
                        WriteService.NotePerTargetWrite(target);
                        return McpResponse.Ok(target: target, code: "WwpGridColumnUpdated", result: receipt);
                    }
                }
                catch (Exception readError)
                {
                    receipt["persisted"] = JValue.CreateNull();
                    receipt["persistedStateKnown"] = false;
                    receipt["saved"] = committed ? new JValue(true) : JValue.CreateNull();
                    receipt["verificationError"] = readError.Message;
                }
                return McpResponse.Err(code: "WwpGridColumnNotVerified", target: target,
                    message: failure ?? "Post-commit reread did not confirm the requested state. No compensating write or retry was attempted.", extra: receipt);
            }
        }

        private string GridPatternText(KBObject host)
        {
            string text = _patterns.ReadPatternPartXml(host, "PatternInstance", out KBObject resolved, out _);
            if (resolved?.Guid != host.Guid || string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("PatternInstance identity/read could not be verified.");
            return text;
        }

        private static string GridPartText(KBObject obj, string part)
        {
            if (part == "WebForm") return WebFormXmlHelper.ReadEditableXml(obj)
                ?? throw new InvalidOperationException("A complete WebForm is required.");
            var value = PartAccessor.GetPart(obj, part);
            if (value is ISource source) return source.Source ?? string.Empty;
            throw new InvalidOperationException("A complete " + part + " source is required.");
        }

        private static JArray GridVariables(KBObject obj)
        {
            var part = PartAccessor.GetVariablesPart(obj) ?? throw new InvalidOperationException("Variables are unavailable.");
            return new JArray(part.Variables.Select(v => new JObject
            {
                ["name"] = v.Name, ["id"] = v.Id, ["basicType"] = v.Type.ToString() == "CHARACTER" || v.Type.ToString() == "CHAR" ? "Character"
                    : v.Type.ToString() == "VARCHAR" ? "VarChar" : v.Type.ToString(),
                ["length"] = v.Length, ["decimals"] = v.Decimals, ["definition"] = v.SerializeToXml()
            }));
        }

        private string GridBinding(KBObject parent, XDocument planned, JObject args, string name, bool? variable)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            XElement grid = ResolveGridPath(planned.Root, (string)args["gridPath"], e => e.Name.LocalName, e => e.Elements().ToList());
            XElement column = FindGridColumn(grid.Elements().ToList(), name, variable, e => e.Name.LocalName, Attr);
            if (column != null && Is(column, "gridVariable"))
            {
                var matches = PartAccessor.GetVariablesPart(parent)?.Variables.Where(v => v.Name.Equals(Attr(column, "name"), StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches?.Count == 1) return "var:" + matches[0].Id;
            }
            else if (column != null)
            {
                string reference = Attr(column, "attribute");
                Guid id;
                KBObject attribute = reference != null && reference.Length > 37 && Guid.TryParse(reference.Substring(0, 36), out id)
                    ? _objects.FindObject(null, "Attribute", guid: id.ToString()) : _objects.FindObject(reference, "Attribute");
                if (attribute?.TypeDescriptor?.Name == "Attribute") return "att:" + attribute.Id;
            }
            throw new InvalidOperationException("The column binding cannot be resolved unambiguously: " + name);
        }

        private JObject CheckGridColumnState(XDocument before, XDocument planned, KBObject host, KBObject parent,
            string operation, JObject args, string form, string events, JArray variables,
            ObjectMoveSnapshot hostSnapshot, ObjectMoveSnapshot parentSnapshot, string applyBefore)
        {
            JObject pattern = VerifyGridColumnXml(before, planned, XDocument.Parse(GridPatternText(host), LoadOptions.PreserveWhitespace), operation, args);
            JObject projection = VerifyGridColumnProjection(form, GridPartText(parent, "WebForm"), operation, args,
                GridBinding(parent, planned, args, (string)(args["attribute"] ?? args["variable"]), args["variable"] != null),
                GridBinding(parent, planned, args, (string)args["before"], null));
            JObject vars = VerifyGridColumnVariables(variables, GridVariables(parent), operation, args);
            JObject eventCheck = VerifyGridColumnEvents(events, GridPartText(parent, "Events"));
            var patternPart = ((PatternInstance)host).PatternPart;
            string patternPartName = patternPart.TypeDescriptor?.Name ?? patternPart.GetType().Name;
            bool others = hostSnapshot.Compare(host, new[] { patternPartName }).Equal
                && parentSnapshot.Compare(parent, new[] { "WebForm", "Variables", "Events" }).Equal
                && string.Equals(applyBefore, ReadObjectProperty(host, "SDPlus_Editor_Apply_On_Save"), StringComparison.Ordinal);
            return new JObject
            {
                ["confirmed"] = pattern["matches"]?.Value<bool>() == true && projection["confirmed"]?.Value<bool>() == true
                    && vars["confirmed"]?.Value<bool>() == true && eventCheck["confirmed"]?.Value<bool>() == true && others
                    && (host as PatternInstance)?.KBObject?.Guid == parent.Guid,
                ["pattern"] = pattern, ["webForm"] = projection, ["variables"] = vars, ["events"] = eventCheck,
                ["unrelatedPartsPreserved"] = others, ["identityConfirmed"] = (host as PatternInstance)?.KBObject?.Guid == parent.Guid
            };
        }
    }
}
