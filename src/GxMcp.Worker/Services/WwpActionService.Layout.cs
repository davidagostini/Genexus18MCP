using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common;
using Artech.Packages.Patterns.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Structure;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed partial class WwpActionService
    {
        private string RunLayoutOperation(string target, KBObject instance, JObject args)
        {
            string expected = (string)(args["expectedVersion"] ?? args["baseVersion"] ?? args["versionToken"]);
            if (string.IsNullOrWhiteSpace(expected))
                return McpResponse.Err(code: "ExpectedVersionRequired", target: target,
                    message: "add_layout requires the PatternInstance read token, including for dryRun.");
            var host = instance as PatternInstance;
            if (!IsLayoutParentType(host?.KBObject?.GetType()))
                return McpResponse.Err(code: "WwpLayoutParentUnsupported", target: target,
                    message: "add_layout requires a native WorkWithPlus instance bound to a WebPanel or WebComponent.");
            Guid hostId = host.Guid, parentId = host.KBObject.Guid;
            var kb = host.KB;
            lock (WriteService.AcquirePerTargetLock(target))
            lock (WriteService.AcquirePerTargetLock(hostId.ToString()))
            {
                host = (PatternInstance)EventsSaveIsolation.Fresh(kb, hostId);
                KBObject parent = EventsSaveIsolation.Fresh(kb, parentId);
                string original = LayoutPatternText(host);
                string token = WriteService.ComputeContentVersionToken(host, original);
                if (token != expected)
                    return McpResponse.Err(code: "StaleObject", message: "PatternInstance changed; no save attempted.", target: target,
                        extra: new JObject { ["currentVersion"] = token, ["persisted"] = false });
                var before = XDocument.Parse(original);
                var planned = new XDocument(before);
                JObject plan = PlanLayout(planned, args);
                if (plan["error"] != null)
                    return McpResponse.Err(code: (string)plan["code"], message: (string)plan["error"], target: target);
                // Preflight every declared variable before creating any native child.
                var variables = LayoutVariables(parent, (JArray)args["children"]);
                string formBefore = WebFormXmlHelper.ReadEditableXml(parent)
                    ?? throw new InvalidOperationException("The complete parent WebForm is required.");
                string eventsBefore = LayoutEvents(parent);
                var receipt = new JObject
                {
                    ["operation"] = "add_layout", ["instanceGuid"] = hostId.ToString(), ["parentGuid"] = parentId.ToString(),
                    ["versionToken"] = token, ["typedDiff"] = plan, ["persisted"] = false,
                    ["saveAttempted"] = false, ["commitCompleted"] = false, ["rollbackPerformed"] = false,
                    ["persistedStateKnown"] = true, ["specified"] = false, ["generated"] = false, ["built"] = false
                };
                if (args["dryRun"]?.Value<bool>() == true)
                    return McpResponse.Ok(code: "WwpLayoutDryRun", target: target, result: receipt);
                var hostSnapshot = CaptureLayoutSnapshot(host);
                var parentSnapshot = CaptureLayoutSnapshot(parent);
                var snapshots = CaptureSnapshots(host, original, parent, formBefore);
                if (snapshots.Pattern == null || snapshots.WebForm == null)
                    throw new InvalidOperationException("PatternInstance and WebForm snapshots are required.");
                string snapshotRoot = EditSnapshotStore.ResolveRoot(kb.Location);
                var eventsSnapshot = EditSnapshotStore.SaveSnapshot(snapshotRoot, parentId.ToString(), "Events", eventsBefore);
                var variablesPart = PartAccessor.GetVariablesPart(parent)
                    ?? throw new InvalidOperationException("The Variables snapshot is unavailable.");
                var variablesSnapshot = EditSnapshotStore.SaveSnapshot(snapshotRoot, parentId.ToString(), "Variables", variablesPart.SerializeToXml());
                if (eventsSnapshot == null || variablesSnapshot == null)
                    throw new InvalidOperationException("Events and Variables snapshots are required.");
                receipt["snapshot"] = snapshots.ToJson();
                receipt["snapshot"]["events"] = eventsSnapshot.Path;
                receipt["snapshot"]["variables"] = variablesSnapshot.Path;
                receipt["snapshot"]["hostHash"] = hostSnapshot.Hash;
                receipt["snapshot"]["parentHash"] = parentSnapshot.Hash;
                string parentName = parent.Name, hostName = host.Name;
                IndexCacheService readIndex = _objects.GetKbService()?.GetIndexCache();
                bool committed = false, rolledBack = false, mutationAttempted = false;
                string failure = null;
                try
                {
                    using (var transaction = kb.BeginTransaction())
                    {
                        try
                        {
                            host = (PatternInstance)EventsSaveIsolation.Fresh(kb, hostId);
                            parent = EventsSaveIsolation.Fresh(kb, parentId);
                            bool identityMatches = host.KBObject?.Guid == parentId;
                            bool patternMatches = LayoutPatternText(host) == original;
                            var boundaryHost = CompareLayoutSnapshot(hostSnapshot, host);
                            var boundaryParent = CompareLayoutSnapshot(parentSnapshot, parent);
                            receipt["boundaryVerification"] = new JObject
                            {
                                ["identityConfirmed"] = identityMatches, ["patternXmlConfirmed"] = patternMatches,
                                ["host"] = LayoutComparisonReceipt(boundaryHost), ["parent"] = LayoutComparisonReceipt(boundaryParent)
                            };
                            if (!identityMatches || !patternMatches || !boundaryHost.Equal || !boundaryParent.Equal)
                                throw new WwpTabException("StaleObject", "Host or parent changed at the transaction boundary.");
                            var part = host.PatternPart;
                            if (!TryFindNativeTable(GetProperty(part, "RootElement"), (string)args["tablePath"], out object table, out string error))
                                throw new WwpTabException("WwpTableNotFound", error);
                            mutationAttempted = true;
                            foreach (JObject child in (JArray)args["children"])
                                AttachLayoutChild(table, child, variables, receipt);
                            WriteService.ForcePatternPartDirty(part);
                            receipt["nativeOperation"] = new JObject { ["stage"] = "save" };
                            receipt["saveAttempted"] = true;
                            // Save the native instance, then project it through the supported
                            // WWP callback inside this transaction. Save alone does not project.
                            host.Save(new KBObjectSavePreferences());
                            receipt["objectSaveReturned"] = true;
                            host = (PatternInstance)EventsSaveIsolation.Fresh(kb, hostId);
                            parent = EventsSaveIsolation.Fresh(kb, parentId);
                            receipt["nativeOperation"] = new JObject { ["stage"] = "project" };
                            bool projected = WwpProjectionHelper.TryProjectHostOntoParent(parent, host, out var projection);
                            receipt["projection"] = LayoutProjectionReceipt(projection);
                            if (!projected)
                                throw new WwpTabException("WwpProjectionFailed", "The WorkWithPlus projection callback did not complete; transaction aborted.");
                            host = (PatternInstance)EventsSaveIsolation.Fresh(kb, hostId);
                            parent = EventsSaveIsolation.Fresh(kb, parentId);
                            JObject verification = VerifyLayoutState(before, host, parent, args, variables, formBefore, eventsBefore, hostSnapshot, parentSnapshot);
                            receipt["preCommitVerification"] = verification;
                            if (verification["confirmed"]?.Value<bool>() != true)
                            {
                                try
                                {
                                    var attemptedForm = EditSnapshotStore.SaveSnapshot(snapshotRoot, parentId.ToString(),
                                        "AttemptedWebForm", WebFormXmlHelper.ReadEditableXml(parent));
                                    var attemptedEvents = EditSnapshotStore.SaveSnapshot(snapshotRoot, parentId.ToString(),
                                        "AttemptedEvents", LayoutEvents(parent));
                                    receipt["attemptedStateSnapshots"] = new JObject
                                    {
                                        ["webForm"] = attemptedForm?.Path, ["events"] = attemptedEvents?.Path
                                    };
                                }
                                catch (Exception diagnosticError)
                                {
                                    Logger.Error("[WWP-LAYOUT] Failed to snapshot attempted projection: " + diagnosticError);
                                    receipt["attemptedStateSnapshotsUnavailable"] = true;
                                }
                                throw new WwpTabException("WwpLayoutNotVerified", "The native save did not preserve and project the requested state; transaction aborted.");
                            }
                            transaction.Commit();
                            committed = true;
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("[WWP-LAYOUT] Native mutation/save failed: " + ex);
                            failure = (ex.InnerException ?? ex).Message;
                            receipt["failureCode"] = (ex as WwpTabException)?.Code ?? "WwpLayoutSaveFailed";
                            try { transaction.Rollback(); rolledBack = true; }
                            catch (Exception rollbackError) { receipt["rollbackError"] = rollbackError.Message; }
                        }
                    }
                }
                catch (Exception ex) { failure = ex.Message; receipt["transactionFinalizationError"] = ex.Message; }
                finally
                {
                    FinalizeLayoutReadCaches(mutationAttempted, receipt, () =>
                        InvalidateLayoutReadCaches(true, readIndex,
                            target, parentName, parentId.ToString(), hostName, hostId.ToString()));
                }
                receipt["commitCompleted"] = committed;
                receipt["rollbackPerformed"] = rolledBack;
                try
                {
                    host = (PatternInstance)EventsSaveIsolation.Fresh(kb, hostId);
                    parent = EventsSaveIsolation.Fresh(kb, parentId);
                    receipt["versionToken"] = WriteService.ComputeContentVersionToken(host, LayoutPatternText(host));
                    var restoredHost = CompareLayoutSnapshot(hostSnapshot, host);
                    var restoredParent = CompareLayoutSnapshot(parentSnapshot, parent);
                    bool restored = restoredHost.Equal && restoredParent.Equal;
                    receipt["restorationVerification"] = new JObject
                    {
                        ["host"] = LayoutComparisonReceipt(restoredHost), ["parent"] = LayoutComparisonReceipt(restoredParent)
                    };
                    receipt["stateRestoredExactly"] = restored;
                    receipt["rollbackVerified"] = rolledBack && restored;
                    if (committed)
                    {
                        JObject verification = VerifyLayoutState(before, host, parent, args, variables, formBefore, eventsBefore, hostSnapshot, parentSnapshot);
                        receipt["postSaveVerification"] = verification;
                        if (verification["confirmed"]?.Value<bool>() == true)
                        {
                            receipt["persisted"] = true;
                            WriteService.NotePerTargetWrite(target);
                            return McpResponse.Ok(code: "WwpLayoutUpdated", target: target, result: receipt);
                        }
                    }
                    receipt["partialPersistenceDetected"] = !restored;
                    receipt["persisted"] = restored ? (JToken)false : JValue.CreateNull();
                }
                catch (Exception ex)
                {
                    receipt["persisted"] = JValue.CreateNull();
                    receipt["persistedStateKnown"] = false;
                    receipt["verificationError"] = ex.Message;
                }
                return McpResponse.Err(code: (string)receipt["failureCode"] ?? "WwpLayoutNotVerified", target: target,
                    message: failure ?? "Post-save verification failed. Inspect saved state before retrying; no compensating write was attempted.", extra: receipt);
            }
        }

        // SourcePart.SerializeData consumes its lazy m_Source field before the getter
        // materializes it. Snapshot native bytes only after reading all source parts.
        internal static void PrepareLayoutSnapshotSources(IEnumerable<ISource> sources)
        {
            foreach (ISource source in sources) { _ = source.Source; }
        }

        private static ObjectMoveSnapshot CaptureLayoutSnapshot(KBObject obj)
        {
            PrepareLayoutSnapshotSources(obj.Parts.Cast<KBObjectPart>().OfType<ISource>());
            return ObjectMoveSnapshot.Capture(obj);
        }

        private static ObjectMoveSnapshot.Comparison CompareLayoutSnapshot(ObjectMoveSnapshot snapshot,
            KBObject obj, params string[] ignoredParts)
        {
            PrepareLayoutSnapshotSources(obj.Parts.Cast<KBObjectPart>().OfType<ISource>());
            return snapshot.Compare(obj, ignoredParts);
        }

        private static JObject LayoutComparisonReceipt(ObjectMoveSnapshot.Comparison comparison) => new JObject
        {
            ["equal"] = comparison.Equal, ["changedParts"] = comparison.ChangedParts.DeepClone(),
            ["changedPartKeys"] = new JArray(comparison.ChangedPartKeys), ["persistedHash"] = comparison.PersistedHash
        };

        internal static JObject LayoutProjectionReceipt(WwpProjectionHelper.ProjectionResult result) => new JObject
        {
            ["lifecycleAttempted"] = result.LifecycleAttempted, ["shouldBuild"] = result.ShouldBuild,
            ["beforeStartBuild"] = result.BeforeStartBuild, ["afterImportResources"] = result.AfterImportResources,
            ["updateParentObject"] = result.UpdateParentObject, ["afterEndBuild"] = result.AfterEndBuild,
            ["parentSaved"] = result.ParentSaved, ["confirmed"] = result.LifecycleExecuted,
            ["failure"] = result.Failure
        };

        private string LayoutPatternText(KBObject host)
        {
            string xml = _patterns.ReadPatternPartXml(host, "PatternInstance", PatternRegistry.WorkWithPlusPatternId, out KBObject resolved, out _);
            if (resolved?.Guid != host.Guid || string.IsNullOrWhiteSpace(xml))
                throw new InvalidOperationException("PatternInstance identity could not be confirmed.");
            return xml;
        }

        internal static JObject PlanLayout(XDocument document, JObject args)
        {
            try
            {
                XElement table = FindXmlTableOrNull(document, (string)args["tablePath"], out string error);
                if (table == null) throw new WwpTabException("WwpTableNotFound", error);
                if (!(args["children"] is JArray children) || children.Count == 0)
                    throw new WwpTabException("WwpLayoutChildrenRequired", "children must be a non-empty typed array.");
                var names = new HashSet<string>(document.Descendants().Select(e => Attr(e, "name"))
                    .Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);
                var additions = children.Select(c => PlanLayoutChild(c as JObject, names, 0)).ToList();
                if (!string.IsNullOrWhiteSpace(Attr(table, "childrenOrderedList"))
                    && additions.Any(e => Is(e, "table") || Is(e, "userAction")))
                    throw new WwpTabException("WwpOrderedContainerUnsupported",
                        "Adding tables or actions to a container with an existing childrenOrderedList requires an independently verified native IDE ordering adapter. Choose an unordered container; no metadata was changed.");
                table.Add(additions);
                return new JObject { ["tablePath"] = args["tablePath"], ["children"] = children.DeepClone(), ["changed"] = true };
            }
            catch (WwpTabException ex) { return new JObject { ["code"] = ex.Code, ["error"] = ex.Message }; }
        }

        private static XElement PlanLayoutChild(JObject child, HashSet<string> names, int depth)
        {
            if (child == null || depth > 10) throw new WwpTabException("InvalidWwpChild", "A typed child object is required; maximum nesting depth is 10.");
            string type = (string)child["type"], name = (string)child["name"];
            string[] allowed = type == "variable" ? new[] { "type", "name" }
                : type == "userAction" ? new[] { "type", "name", "caption" }
                : type == "table" ? new[] { "type", "name", "tableType", "children" } : null;
            if (allowed == null || child.Properties().Any(p => !allowed.Contains(p.Name)))
                throw new WwpTabException("InvalidWwpChild", "Only variable(name), userAction(name,caption), and table(name,tableType,children) are supported; declarations and default metadata cannot be edited.");
            if (name == null || !Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$") || !names.Add(name))
                throw new WwpTabException("WwpLayoutNameConflict", "Every new layout name must be valid and unique in the instance.");
            var element = new XElement(type, new XAttribute("name", name));
            if (type == "userAction") element.SetAttributeValue("caption", (string)child["caption"] ?? name);
            if (type == "table")
            {
                string tableType = (string)child["tableType"] ?? "Responsive";
                if (tableType != "Responsive" && tableType != "Regular")
                    throw new WwpTabException("InvalidWwpTableType", "tableType must be Responsive or Regular.");
                element.SetAttributeValue("type", tableType);
                if (child["children"] != null && !(child["children"] is JArray))
                    throw new WwpTabException("InvalidWwpChild", "Table children must be a typed array.");
                foreach (JToken nested in child["children"] as JArray ?? new JArray())
                    element.Add(PlanLayoutChild(nested as JObject, names, depth + 1));
            }
            return element;
        }

        private static IEnumerable<JObject> LayoutChildren(JArray children)
        {
            foreach (JObject child in children)
            {
                yield return child;
                foreach (JObject nested in LayoutChildren(child["children"] as JArray ?? new JArray())) yield return nested;
            }
        }

        private static Dictionary<string, Variable> LayoutVariables(KBObject parent, JArray children)
        {
            var declared = PartAccessor.GetVariablesPart(parent)?.Variables
                ?? throw new InvalidOperationException("The parent Variables part is unavailable.");
            var result = new Dictionary<string, Variable>(StringComparer.OrdinalIgnoreCase);
            foreach (JObject child in LayoutChildren(children).Where(c => (string)c["type"] == "variable"))
            {
                string name = (string)child["name"];
                var matches = declared.Where(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1) throw new WwpTabException("WwpVariableNotDeclared", "Declare variable '" + name + "' on the parent before adding it to the layout.");
                Variable variable = matches[0];
                if (!string.IsNullOrWhiteSpace(DomainPropertyApplier.GetAttributeBasedOnName(variable)))
                    throw new WwpTabException("WwpVariableBindingUnsupported", "Attribute-based variables are not supported by add_layout.");
                if (variable.DomainBasedOn == null) LayoutBasicType(variable);
                result.Add(name, variable);
            }
            return result;
        }

        private static string LayoutBasicType(Variable variable)
        {
            switch (variable.Type.ToString().ToUpperInvariant())
            {
                case "CHAR": case "CHARACTER": return "Character";
                case "VARCHAR": return "VarChar";
                case "LONGVARCHAR": return "LongVarChar";
                case "NUMERIC": return "Numeric";
                case "DATE": return "Date";
                case "DATETIME": return "DateTime";
                case "BOOLEAN": return "Boolean";
                case "GUID": return "GUID";
                default: throw new WwpTabException("WwpVariableBindingUnsupported", "This variable type has no verified layout adapter: " + variable.Type);
            }
        }

        private static void AttachLayoutChild(object parent, JObject spec, Dictionary<string, Variable> variables, JObject receipt)
        {
            string type = (string)spec["type"], name = (string)spec["name"];
            Action<string> stage = operation => receipt["nativeOperation"] = new JObject
            {
                ["stage"] = operation, ["childType"] = type, ["childName"] = name
            };
            object child = null;
            Action<string, object> set = (attribute, value) =>
            {
                stage("set:" + attribute);
                if (!PatternSemanticAttributeWriter.ApplySemanticAttributeObject(child, attribute, value,
                    value is string || value is int ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : null))
                    throw new WwpTabException("WwpAttributeRejected", "WorkWithPlus rejected layout attribute '" + attribute + "'.");
            };
            stage("create");
            child = CreateNativeChild(parent, type);
            set("name", name);
            if (type == "variable")
            {
                Variable variable = variables[name];
                if (variable.DomainBasedOn != null)
                {
                    set("dataType", "Based on");
                    set("domain", variable.DomainBasedOn);
                }
                else
                {
                    string basicType = LayoutBasicType(variable);
                    set("dataType", "Basic");
                    set("basicType", basicType);
                    if (basicType == "Numeric")
                    {
                        // The native specification declares these attributes as int;
                        // ChangeAttributeValueCommand does not parse string values.
                        set("basicLength", (int)variable.Length);
                        set("basicDecimals", (int)variable.Decimals);
                    }
                    else if (basicType == "Character" || basicType == "VarChar" || basicType == "LongVarChar")
                        set(basicType == "LongVarChar" ? "basicLVCLength" : "basicCLength", (int)variable.Length);
                }
            }
            else if (type == "userAction") set("caption", (string)spec["caption"] ?? name);
            else
            {
                set("type", (string)spec["tableType"] ?? "Responsive");
                foreach (JObject nested in spec["children"] as JArray ?? new JArray()) AttachLayoutChild(child, nested, variables, receipt);
            }
            stage("attach");
            ExecuteElementCommand(parent, child, "AddElementCommand", null);
        }

        // PatternInstanceElement.Type is the specification type (TableVariable),
        // while Name is the XML child name (variable).
        internal static bool IsLayoutVariableElement(object element) =>
            string.Equals(GetProperty(element, "Name")?.ToString(), "variable", StringComparison.OrdinalIgnoreCase);

        private JObject VerifyLayoutState(XDocument before, PatternInstance host, KBObject parent, JObject args,
            Dictionary<string, Variable> variables, string formBefore, string eventsBefore, ObjectMoveSnapshot hostSnapshot, ObjectMoveSnapshot parentSnapshot)
        {
            bool pattern = VerifyLayoutPattern(before, XDocument.Parse(LayoutPatternText(host)), args);
            JObject projection = VerifyLayoutProjection(formBefore, WebFormXmlHelper.ReadEditableXml(parent), (JArray)args["children"],
                variables.ToDictionary(v => v.Key, v => "var:" + v.Value.Id, StringComparer.OrdinalIgnoreCase));
            // Variables, Events, Rules, all other parts, and authored object properties must remain intact.
            bool preserved = CompareLayoutSnapshot(hostSnapshot, host, host.PatternPart.TypeDescriptor?.Name ?? host.PatternPart.GetType().Name).Equal
                && CompareLayoutSnapshot(parentSnapshot, parent, "WebForm", "Events").Equal;
            bool events = VerifyLayoutEvents(eventsBefore, LayoutEvents(parent), (JArray)args["children"]);
            bool bindings = true;
            object nativeRoot = GetProperty(host.PatternPart, "RootElement");
            foreach (var pair in variables)
            {
                var nativeMatches = Walk(nativeRoot).Where(e => IsLayoutVariableElement(e)
                    && string.Equals(NativeAttribute(e, "name"), pair.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                if (nativeMatches.Count != 1) { bindings = false; break; }
                if (pair.Value.DomainBasedOn != null)
                {
                    var domain = PatternSemanticAttributeWriter.ReadSemanticAttributeObject(nativeMatches[0], "domain") as KBObject;
                    bindings &= domain?.Guid == pair.Value.DomainBasedOn.Guid && NativeAttribute(nativeMatches[0], "dataType") == "Based on";
                }
                else bindings &= NativeAttribute(nativeMatches[0], "dataType") == "Basic"
                    && NativeAttribute(nativeMatches[0], "basicType") == LayoutBasicType(pair.Value);
            }
            return new JObject { ["confirmed"] = pattern && projection["confirmed"]?.Value<bool>() == true && preserved && events && bindings && host.KBObject?.Guid == parent.Guid,
                ["patternReReadConfirmed"] = pattern, ["projection"] = projection, ["unrelatedStatePreserved"] = preserved, ["eventsPreserved"] = events, ["nativeVariableBindingsConfirmed"] = bindings };
        }

        private static string LayoutEvents(KBObject parent)
        {
            if (!(PartAccessor.GetPart(parent, "Events") is ISource source))
                throw new InvalidOperationException("The Events source is required.");
            return source.Source ?? string.Empty;
        }

        internal static bool VerifyLayoutEvents(string before, string after, JArray children)
        {
            if (before == null || after == null) return false;
            foreach (JObject action in LayoutChildren(children).Where(c => (string)c["type"] == "userAction"))
            {
                string eventName = "Do" + (string)action["name"];
                string header = @"(?im)^[ \t]*Event[ \t]+['"" ]?" + Regex.Escape(eventName) + @"['"" ]?[ \t]*\r?$";
                // Existing event bodies are never rewritten or stripped.
                if (Regex.IsMatch(before, header)) continue;
                string markers = Regex.Escape("/* Generated by DVelop Work With Plus Pattern [Start] - Do not change */")
                    + @"[ \t\r\n]*" + Regex.Escape("/* Generated by DVelop Work With Plus Pattern [End] - Do not change */");
                string empty = @"(?im)^[ \t]*Event[ \t]+['"" ]" + Regex.Escape(eventName)
                    + @"['"" ][ \t]*\r?\n[ \t\r\n]*(?:(?-i:" + markers + @")[ \t\r\n]*)?EndEvent[ \t\r\n]*(?=\S|$)";
                var matches = Regex.Matches(after, empty);
                if (matches.Count > 1) return false;
                if (matches.Count == 1) after = after.Remove(matches[0].Index, matches[0].Length);
            }
            return string.Equals(before.TrimEnd('\r', '\n'), after.TrimEnd('\r', '\n'), StringComparison.Ordinal);
        }

        internal static bool VerifyLayoutPattern(XDocument before, XDocument after, JObject args)
        {
            var oldCopy = new XDocument(before);
            var newCopy = new XDocument(after);
            XElement oldTable = FindXmlTableOrNull(oldCopy, (string)args["tablePath"]);
            XElement newTable = FindXmlTableOrNull(newCopy, (string)args["tablePath"]);
            if (oldTable == null || newTable == null) return false;
            int count = oldTable.Elements().Count();
            var additions = newTable.Elements().Skip(count).ToList();
            var requested = (JArray)args["children"];
            if (additions.Count != requested.Count) return false;
            for (int i = 0; i < additions.Count; i++)
                if (!LayoutChildMatches(additions[i], (JObject)requested[i])) return false;
            additions.Remove();
            return LayoutXmlEqual(oldCopy.Root, newCopy.Root);
        }

        private static bool LayoutChildMatches(XElement element, JObject spec)
        {
            string type = (string)spec["type"];
            if (!Is(element, type) || Attr(element, "name") != (string)spec["name"]) return false;
            if (type == "userAction" && Attr(element, "caption") != ((string)spec["caption"] ?? (string)spec["name"])) return false;
            if (type == "table" && Attr(element, "type") != ((string)spec["tableType"] ?? "Responsive")) return false;
            var children = spec["children"] as JArray ?? new JArray();
            var actual = element.Elements().ToList();
            if (actual.Count != children.Count) return false;
            for (int i = 0; i < actual.Count; i++) if (!LayoutChildMatches(actual[i], (JObject)children[i])) return false;
            return true;
        }

        internal static JObject VerifyLayoutProjection(string beforeXml, string afterXml, JArray children, IDictionary<string, string> bindings)
        {
            var result = new JObject { ["confirmed"] = false };
            Func<string, JObject> failed = stage => { result["failureStage"] = stage; return result; };
            try
            {
                var before = XDocument.Parse(beforeXml);
                var after = XDocument.Parse(afterXml);
                NormalizeLayoutStructuralIds(before);
                NormalizeLayoutStructuralIds(after);
                var originalNames = before.Descendants().Select(e => Attr(e, "ControlName")).Where(n => n.Length > 0).ToList();
                var originalSet = new HashSet<string>(originalNames, StringComparer.OrdinalIgnoreCase);
                var persistedOrder = after.Descendants().Select(e => Attr(e, "ControlName")).Where(originalSet.Contains).ToList();
                if (!originalNames.SequenceEqual(persistedOrder, StringComparer.OrdinalIgnoreCase)) return failed("existingControlOrder");
                // Compare every original named control, including binding, position and authored properties.
                foreach (XElement oldControl in before.Descendants().Where(e => Attr(e, "ControlName").Length > 0))
                {
                    string name = Attr(oldControl, "ControlName");
                    var current = after.Descendants().Where(e => Attr(e, "ControlName").Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (current.Count != 1 || oldControl.Name != current[0].Name
                        || !LayoutXmlEqual(new XElement(oldControl.Name, oldControl.Attributes()), new XElement(current[0].Name, current[0].Attributes())))
                        return failed("existingControlIdentityOrAttributes");
                    var oldParents = oldControl.Ancestors().Select(e => Attr(e, "ControlName")).Where(n => n.Length > 0);
                    var newParents = current[0].Ancestors().Select(e => Attr(e, "ControlName")).Where(n => n.Length > 0);
                    if (!oldParents.SequenceEqual(newParents, StringComparer.OrdinalIgnoreCase)) return failed("existingControlAncestry");
                    // Leaf controls can carry event/property subelements; preserve those too.
                    bool container = Is(oldControl, "table") || Is(oldControl, "gxTable") || Is(oldControl, "gxResponsiveTable");
                    if (!container && !oldControl.Descendants().Any(e => Attr(e, "ControlName").Length > 0)
                        && !LayoutXmlEqual(new XElement(oldControl), new XElement(current[0]))) return failed("existingControlContent");
                }
                var addedControls = new HashSet<XElement>();
                foreach (JObject child in LayoutChildren(children))
                {
                    string type = (string)child["type"], name = (string)child["name"];
                    List<XElement> matches;
                    if (type == "variable")
                    {
                        if (!bindings.TryGetValue(name, out string binding)) return failed("variableDeclarationMissing");
                        matches = after.Descendants().Where(e => LayoutControlBinding(e) == binding).ToList();
                        if (before.Descendants().Any(e => LayoutControlBinding(e) == binding)) return failed("variableBindingAlreadyPresent");
                    }
                    else
                    {
                        matches = after.Descendants().Where(e => type == "table"
                            ? Attr(e, "ControlName").Equals(name, StringComparison.OrdinalIgnoreCase) && (Is(e, "table") || Is(e, "gxTable") || Is(e, "gxResponsiveTable"))
                            : Is(e, "action") ? Attr(e, "ControlName").Equals("Btn" + name, StringComparison.OrdinalIgnoreCase)
                            : Is(e, "gxButton") && Attr(e, "ControlName").Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (before.Descendants().Any(e => Attr(e, "ControlName").Equals(name, StringComparison.OrdinalIgnoreCase))) return failed("controlNameAlreadyPresent");
                    }
                    if (matches.Count != 1) return failed(type + "ControlMissingOrAmbiguous");
                    addedControls.Add(matches[0]);
                    if (type == "userAction" && (Attr(matches[0], "Caption") != ((string)child["caption"] ?? name)
                        || (Is(matches[0], "action") ? Attr(matches[0], "onClickEvent") != "'Do" + name + "'"
                            : !WebFormContainsEvent(after, name)))) return failed("actionCaptionOrEventBinding");
                }
                if (!LayoutProjectionPreserves(before.Root, after.Root, addedControls)) return failed("unnamedContentOrStructure");
                result["confirmed"] = true;
                result["unnamedContentPreserved"] = true;
                result["bindingConfirmed"] = true;
                result["existingControlsPreserved"] = true;
            }
            catch (Exception ex) when (ex is System.Xml.XmlException || ex is ArgumentException) { result["code"] = "WwpProjectionSchemaUnsupported"; }
            return result;
        }

        // WEB_COMP changes the descriptor to WebComponent while retaining the
        // same native WebPanel CLR type and entity type GUID.
        internal static bool IsLayoutParentType(Type type) => type == typeof(Artech.Genexus.Common.Objects.WebPanel);

        internal static void InvalidateLayoutReadCaches(bool mutationAttempted, IndexCacheService index = null, params string[] targets)
        {
            // The public read path may return a cached payload before fresh SDK lookup.
            // Clear every identity alias after either commit or rollback, never on preview.
            if (mutationAttempted) WriteService.InvalidatePatternMutationCaches(index, targets);
        }

        internal static void FinalizeLayoutReadCaches(bool mutationAttempted, JObject receipt, Action invalidate)
        {
            if (!mutationAttempted) return;
            try { invalidate(); }
            catch (Exception ex)
            {
                receipt["readCacheInvalidationError"] = ex.Message;
                // Preserve the native persistence receipt even when indexed invalidation
                // fails, and still attempt the existing managed-only cache clear.
                try { WriteService.InvalidateIsolatedEventsReadCaches(); }
                catch (Exception fallback) { receipt["managedReadCacheInvalidationError"] = fallback.Message; }
            }
        }

        private static string LayoutControlBinding(XElement element) => Is(element, "data")
            ? Attr(element, "attribute") : Attr(element, "AttId");

        private static void NormalizeLayoutStructuralIds(XDocument document)
        {
            foreach (var element in document.Descendants().Where(e =>
                (Is(e, "layout") || Is(e, "table") || Is(e, "row") || Is(e, "cell"))
                && e.AncestorsAndSelf().Any(a => Is(a, "layout"))))
            {
                var id = element.Attribute("id");
                if (id != null && Guid.TryParse(id.Value, out _)) id.Value = "native-structural-guid";
            }
        }

        private static bool LayoutProjectionPreserves(XNode before, XNode after, HashSet<XElement> additions)
        {
            if (!(before is XElement oldElement) || !(after is XElement newElement))
                return XNode.DeepEquals(before, after);
            if (!LayoutXmlEqual(new XElement(oldElement.Name, oldElement.Attributes()),
                new XElement(newElement.Name, newElement.Attributes()))) return false;
            var original = oldElement.Nodes().ToList();
            var persisted = newElement.Nodes().ToList();
            int position = 0;
            foreach (XNode node in original)
            {
                while (position < persisted.Count && !LayoutProjectionPreserves(node, persisted[position], additions))
                {
                    if (!IsAddedProjectionSubtree(persisted[position], additions)) return false;
                    position++;
                }
                if (position == persisted.Count) return false;
                position++;
            }
            return persisted.Skip(position).All(n => IsAddedProjectionSubtree(n, additions));
        }

        private static bool IsAddedProjectionSubtree(XNode node, HashSet<XElement> additions)
        {
            if (!(node is XElement element)) return false;
            if (additions.Contains(element)) return true;
            // New anonymous layout wrappers may surround only requested controls.
            // Existing text, comments, HTML, labels and empty cells must still match.
            return string.IsNullOrEmpty(Attr(element, "ControlName"))
                && element.Elements().Any()
                && element.Nodes().All(n => n is XText text && string.IsNullOrWhiteSpace(text.Value)
                    || IsAddedProjectionSubtree(n, additions));
        }

        private static bool LayoutXmlEqual(XElement left, XElement right)
        {
            foreach (XElement element in left.DescendantsAndSelf().Concat(right.DescendantsAndSelf()))
            {
                element.ReplaceAttributes(element.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).ToList());
            }
            return XNode.DeepEquals(left, right);
        }
    }
}
