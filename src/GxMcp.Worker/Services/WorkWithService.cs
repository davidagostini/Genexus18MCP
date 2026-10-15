using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Compatibility;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Service for typed authoring of standard WorkWith (WorkWith for Web) pattern instances.
    /// Strictly isolated from WorkWithPlus (which is handled by WwpActionService).
    /// </summary>
    public sealed class WorkWithService
    {
        private readonly ObjectService _objects;
        private readonly PatternAnalysisService _patterns;
        private readonly WriteService _write;

        public WorkWithService(ObjectService objects, PatternAnalysisService patterns, WriteService write)
        {
            _objects = objects;
            _patterns = patterns;
            _write = write;
        }

        public string Run(string target, JObject args)
        {
            string action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) action = "list_actions";

            string name = !string.IsNullOrWhiteSpace(target) ? target : args?["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
                return McpResponse.Err(code: "MissingTarget", message: "Target object name is required.", target: target);

            KBObject targetObj = _objects.FindObject(name);
            if (targetObj == null)
                return McpResponse.Err(code: "ObjectNotFound",
                    message: $"Target object '{name}' was not found in the Knowledge Base.",
                    nextSteps: new JArray(McpResponse.NextStep("genexus_query",
                        new JObject { ["query"] = name },
                        $"Search for objects matching '{name}'.")),
                    target: name);

            var matches = _patterns.FindPatternInstances(targetObj);
            var wwMatches = matches.Where(m => m.Pattern != null && m.Pattern.IsWorkWith).ToList();
            if (wwMatches.Count > 1)
                return McpResponse.Err(code: "WorkWithInstanceAmbiguous", message: "Specify a standard WorkWith PatternInstance directly; multiple instances were resolved.", target: name);
            var wwMatch = wwMatches.SingleOrDefault();

            if (wwMatch == null)
            {
                var wwpMatch = matches.FirstOrDefault(m => m.Pattern != null && m.Pattern.IsWorkWithPlus);
                if (wwpMatch != null)
                {
                    return McpResponse.Err(code: "WorkWithPatternMismatch",
                        message: $"'{targetObj.Name}' has a WorkWithPlus instance '{wwpMatch.Candidate.Name}', not a standard WorkWith instance.",
                        hint: "Use genexus_wwp for WorkWithPlus instances; genexus_workwith is dedicated to standard WorkWith.",
                        nextSteps: new JArray(McpResponse.NextStep("genexus_wwp",
                            new JObject { ["action"] = "list", ["name"] = wwpMatch.Candidate.Name },
                            "genexus_wwp manages WorkWithPlus actions.")),
                        target: name);
                }

                return McpResponse.Err(code: "WorkWithInstanceNotFound",
                    message: $"No standard WorkWith instance was resolved for '{targetObj.Name}'.",
                    hint: "Ensure the Transaction has the standard WorkWith pattern applied (WorkWithWeb{0}).",
                    target: name);
            }

            KBObject instanceObj = wwMatch.Candidate.Source as KBObject ?? _objects.FindObject(wwMatch.Candidate.Name);
            if (instanceObj == null)
                return McpResponse.Err(code: "WorkWithInstanceObjectNotFound",
                    message: $"WorkWith instance '{wwMatch.Candidate.Name}' could not be loaded.", target: name);

            switch (action.ToLowerInvariant())
            {
                case "list_actions":
                case "list":
                    return ExecuteListActions(name, instanceObj);

                case "add_action":
                    return ExecuteAddAction(name, instanceObj, args);
                case "remove_action":
                    return ExecuteRemoveAction(name, instanceObj, args);

                default:
                    return McpResponse.Err(code: "UnknownAction",
                        message: $"Action '{action}' is not supported by genexus_workwith. Supported actions: list_actions, add_action, remove_action.",
                        nextSteps: new JArray(McpResponse.NextStep("genexus_workwith",
                            new JObject { ["action"] = "list_actions", ["name"] = name },
                            "List actions on the target WorkWith object.")),
                        target: name);
            }
        }

        private string ExecuteListActions(string target, KBObject instanceObj)
        {
            if (!WorkWithSdkAdapter.ListActions(instanceObj, _patterns, out var tabs, out var error))
            {
                return McpResponse.Err(code: error ?? "ListActionsFailed",
                    message: $"Failed to list actions for '{instanceObj.Name}': {error}",
                    target: target);
            }

            return McpResponse.Ok(target: target, code: "WorkWithActionsRead", result: new JObject
            {
                ["instance"] = instanceObj.Name,
                ["tabs"] = tabs
            });
        }

        private string ExecuteRemoveAction(string target, KBObject instance, JObject args)
        {
            string tab = args?["tab"]?.ToString()?.Trim();
            string action = args?["actionName"]?.ToString()?.Trim();
            bool dryRun = args?["dryRun"]?.ToObject<bool?>() == true;
            if (string.IsNullOrWhiteSpace(tab) || string.IsNullOrWhiteSpace(action))
                return McpResponse.Err(code: "MissingActionSelector", message: "remove_action requires an explicit tab and actionName.", target: target);
            lock (WriteService.AcquirePerTargetLock(instance.Name))
            {
                bool saved = false;
                string savedVersion = null;
                object originalModel = null;
                JArray before = null;
                try
                {
                    instance = _objects.FindObjectFresh(instance.Name, guid: instance.Guid.ToString())
                        ?? throw new UsageException("FreshReadUnavailable", "An independent instance read is required before removal.");
                    string xml = _patterns.ReadPatternPartXml(instance, "PatternInstance", PatternRegistry.WorkWithPatternId, out _, out _);
                    if (string.IsNullOrWhiteSpace(xml)) throw new UsageException("PatternReadFailed", "The current WorkWith XML could not be read.");
                    string token = WriteService.ComputeContentVersionToken(instance, xml);
                    string expectedToken = args?["baseVersion"]?.ToString();
                    if (string.IsNullOrWhiteSpace(expectedToken))
                        return McpResponse.Err(code: "BaseVersionRequired", message: "remove_action requires baseVersion, including dryRun. Read the PatternInstance first.", target: target,
                            extra: new JObject { ["currentVersion"] = token, ["saved"] = false, ["persisted"] = false });
                    if (!string.Equals(token, expectedToken, StringComparison.Ordinal))
                        return McpResponse.Err(code: "StaleObject", message: "The WorkWith instance changed since the supplied baseVersion.", target: target,
                            extra: new JObject { ["currentVersion"] = token, ["saved"] = false, ["persisted"] = false });
                    if (!WorkWithSdkAdapter.ListActions(instance, _patterns, out before, out string listError))
                        throw new UsageException("PatternReadFailed", listError);
                    var expected = WorkWithSdkAdapter.PlanActionRemoval(before, tab, action);
                    originalModel = WorkWithSdkAdapter.LoadNativeModel(instance);
                    var editedModel = WorkWithSdkAdapter.LoadNativeModel(instance);
                    if (ReferenceEquals(originalModel, editedModel)) throw new UsageException("NativeWorkWithSnapshotUnavailable", "The SDK did not provide an independent model for rollback.");
                    WorkWithSdkAdapter.RemoveNativeAction(editedModel, tab, action);
                    var plan = new JObject { ["instance"] = instance.Name, ["tab"] = tab, ["actionName"] = action,
                        ["before"] = before, ["after"] = expected, ["baseVersion"] = token,
                        ["route"] = "native-sdk", ["patternApplySuppression"] = "SkipApplyPattern flag is set and restored around SDK Save; callback isolation requires live validation",
                        ["defaultNormalization"] = WorkWithSdkAdapter.NativeDefaultNormalization };
                    if (dryRun)
                        return McpResponse.Ok(target: target, code: "WorkWithActionRemovalPlan", result: new JObject
                            { ["plan"] = plan, ["dryRun"] = true, ["saved"] = false, ["persisted"] = false });
                    string snapshot = PatternSnapshotStore.SaveSnapshot(instance.Guid.ToString(), "PatternInstance", xml);
                    if (string.IsNullOrWhiteSpace(snapshot)) throw new UsageException("SnapshotUnavailable", "A recovery snapshot is required before removing an action.");
                    using (var transaction = instance.Model.KB.BeginTransaction())
                    {
                        try
                        {
                            var preSave = _objects.FindObjectFresh(instance.Name, guid: instance.Guid.ToString())
                                ?? throw new UsageException("FreshReadUnavailable", "A fresh concurrency check is required before Save.");
                            string preSaveXml = _patterns.ReadPatternPartXml(preSave, "PatternInstance", PatternRegistry.WorkWithPatternId, out _, out _);
                            if (!string.Equals(token, WriteService.ComputeContentVersionToken(preSave, preSaveXml), StringComparison.Ordinal))
                                throw new UsageException("StaleObject", "The WorkWith instance changed while planning removal.");
                            instance = preSave;
                            WorkWithSdkAdapter.SaveNativeModel(editedModel, instance);
                            transaction.Commit();
                            saved = true;
                        }
                        catch { transaction.Rollback(); throw; }
                    }
                    string savedXml = _patterns.ReadPatternPartXml(instance, "PatternInstance", PatternRegistry.WorkWithPatternId, out _, out _);
                    savedVersion = WriteService.ComputeContentVersionToken(instance, savedXml);
                    var persisted = _objects.FindObjectFresh(instance.Name, guid: instance.Guid.ToString());
                    if (persisted == null || !WorkWithSdkAdapter.ListActions(persisted, _patterns, out var after, out _)
                        || !JToken.DeepEquals(expected, after))
                        throw new UsageException("WorkWithRemovalVerificationFailed", "The fresh persisted actions did not match the complete removal plan, including neighboring tabs/actions.");
                    string afterXml = _patterns.ReadPatternPartXml(persisted, "PatternInstance", PatternRegistry.WorkWithPatternId, out _, out _);
                    return McpResponse.Ok(target: target, code: "WorkWithActionRemoved", result: new JObject
                    {
                        ["instance"] = instance.Name, ["tab"] = tab, ["actionName"] = action,
                        ["saved"] = true, ["persisted"] = true, ["verified"] = true,
                        ["versionToken"] = WriteService.ComputeContentVersionToken(persisted, afterXml),
                        ["verificationScope"] = "fresh persisted instance: complete tabs and action definitions",
                        ["route"] = "native-sdk", ["patternApplySuppression"] = "SkipApplyPattern flag is set and restored around SDK Save; callback isolation requires live validation",
                        ["defaultNormalization"] = WorkWithSdkAdapter.NativeDefaultNormalization
                    });
                }
                catch (Exception ex)
                {
                    bool restored = false;
                    string rollbackError = null;
                    if (saved && originalModel != null)
                    {
                        try
                        {
                            var current = _objects.FindObjectFresh(instance.Name, guid: instance.Guid.ToString())
                                ?? throw new UsageException("FreshReadUnavailable", "Rollback needs an independent instance read.");
                            using (var rollback = current.Model.KB.BeginTransaction())
                            {
                                try
                                {
                                    current = _objects.FindObjectFresh(instance.Name, guid: instance.Guid.ToString())
                                        ?? throw new UsageException("FreshReadUnavailable", "Rollback needs a fresh concurrency check.");
                                    string currentXml = _patterns.ReadPatternPartXml(current, "PatternInstance", PatternRegistry.WorkWithPatternId, out _, out _);
                                    if (string.IsNullOrWhiteSpace(savedVersion) || !string.Equals(savedVersion, WriteService.ComputeContentVersionToken(current, currentXml), StringComparison.Ordinal))
                                        throw new UsageException("RollbackConcurrencyConflict", "The instance changed after this Save; automatic rollback was refused to protect concurrent edits.");
                                    WorkWithSdkAdapter.SaveNativeModel(originalModel, current);
                                    rollback.Commit();
                                }
                                catch { rollback.Rollback(); throw; }
                            }
                            var reread = _objects.FindObjectFresh(instance.Name, guid: instance.Guid.ToString());
                            restored = reread != null && WorkWithSdkAdapter.ListActions(reread, _patterns, out var restoredTabs, out _)
                                && JToken.DeepEquals(before, restoredTabs);
                        }
                        catch (Exception rollbackEx) { rollbackError = rollbackEx.Message; }
                    }
                    return McpResponse.Err(code: (ex as UsageException)?.Code ?? "WorkWithActionRemovalFailed", message: ex.Message, target: target,
                        hint: saved && !restored ? "Read the complete current instance before another edit; do not retry automatically." : null,
                        extra: new JObject { ["saved"] = saved, ["persisted"] = saved && !restored, ["verified"] = false,
                            ["rollbackAttempted"] = saved, ["rollbackVerified"] = restored, ["rollbackError"] = rollbackError });
                }
            }
        }

        private string ExecuteAddAction(string target, KBObject instanceObj, JObject args)
        {
            string tabCode = args?["tab"]?.ToString();
            if (string.IsNullOrWhiteSpace(tabCode)) tabCode = "General";

            string actionName = args?["actionName"]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(actionName))
                return McpResponse.Err(code: "MissingActionName", message: "actionName is required for add_action.", target: target);

            if (!Regex.IsMatch(actionName, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
                return McpResponse.Err(code: "InvalidActionName", message: $"Action name '{actionName}' is not a valid identifier.", target: target);

            string procedure = args?["procedure"]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(procedure))
                return McpResponse.Err(code: "MissingProcedure", message: "procedure is required for add_action.", target: target);

            KBObject procObj = _objects.FindObject(procedure);
            if (procObj == null)
                return McpResponse.Err(code: "ProcedureNotFound", message: $"Target Procedure '{procedure}' was not found in the Knowledge Base.", target: target);

            if (!string.Equals(procObj.TypeDescriptor?.Name, "Procedure", StringComparison.OrdinalIgnoreCase))
                return McpResponse.Err(code: "InvalidProcedureType",
                    message: $"Target object '{procedure}' is of type '{procObj.TypeDescriptor?.Name}', but must be a Procedure.",
                    target: target);

            string caption = args?["caption"]?.ToString();
            string condition = args?["condition"]?.ToString();
            var parametersToken = args?["parameters"] as JArray;
            var parameters = parametersToken != null
                ? parametersToken.Select(t => t.ToString()).ToList()
                : new List<string>();

            bool dryRun = args?["dryRun"]?.ToObject<bool?>() == true;

            if (dryRun)
            {
                if (!WorkWithSdkAdapter.AddAction(instanceObj, _patterns, _write, tabCode, actionName, caption, procObj, condition, parameters, dryRun: true, out var plan, out var errCode, out var errMsg))
                {
                    return McpResponse.Err(code: errCode ?? "AddActionPlanFailed", message: errMsg ?? "Failed to create add_action plan.", target: target,
                        extra: new JObject { ["plan"] = plan, ["dryRun"] = true, ["saved"] = false, ["persisted"] = false });
                }

                return McpResponse.Ok(target: target, code: "WorkWithActionPlan", result: new JObject
                {
                    ["plan"] = plan,
                    ["dryRun"] = true
                });
            }

            lock (WriteService.AcquirePerTargetLock(instanceObj.Name))
            {
                KBObject freshInstance = _objects.FindObject(instanceObj.Name) ?? instanceObj;
                if (!WorkWithSdkAdapter.AddAction(freshInstance, _patterns, _write, tabCode, actionName, caption, procObj, condition, parameters, dryRun: false, out var receipt, out var errCode, out var errMsg))
                {
                    return McpResponse.Err(code: errCode ?? "AddActionFailed", message: errMsg ?? "Failed to add action.", target: target,
                        extra: new JObject { ["plan"] = receipt });
                }

                return McpResponse.Ok(target: target, code: "WorkWithActionAdded", result: receipt);
            }
        }
    }
}
