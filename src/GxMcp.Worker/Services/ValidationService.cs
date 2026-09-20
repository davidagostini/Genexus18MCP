using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using Artech.Common.Diagnostics;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Structure;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class ValidationService
    {
        private readonly KbService _kbService;
        private ObjectService _objectService;

        public ValidationService(KbService kbService)
        {
            _kbService = kbService;
        }

        public void SetObjectService(ObjectService os) { _objectService = os; }

        public string ValidateCode(string target, string partName, string code)
        {
            try
            {
                string styleError = ThemeStyleEditHelper.IsStylePartName(partName)
                    ? ThemeStyleEditHelper.ValidateInlineDataUris(code) : null;
                if (styleError != null)
                    return McpResponse.Err(code: "SyntaxError", message: styleError, target: target,
                        extra: new JObject { ["isPreflight"] = true });

                var busyMsg = _kbService.EnsureNotIndexing();
                if (busyMsg != null) return busyMsg;

                var obj = _objectService.FindObject(target);
                if (obj == null) return McpResponse.Err(code: "ObjectNotFound", message: "Object not found for validation.", hint: "The requested object is not available in the active Knowledge Base. Check the target name and KB state.", nextSteps: new JArray(McpResponse.NextStep("genexus_list_objects", null, "Lists available objects so you can verify the target name.")), target: target);

                // When no code is supplied, validate the object's CURRENT source in place
                // (lightweight per-object check, e.g. Procedure Source / Rules) rather than
                // overwriting the part with null. Resolve the part early so we can read it.
                string normalizedPartNameEarly = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;
                if (string.IsNullOrEmpty(code))
                {
                    var existingPart = PartAccessor.GetPart(obj, normalizedPartNameEarly);
                    if (existingPart is ISource existingSource)
                        code = existingSource.Source ?? string.Empty;
                }

                // 1. FAST PRE-FLIGHT CHECK (Regex based)
                var structuralErrors = CodeParser.Validate(code);
                if (structuralErrors.Count > 0)
                {
                    var errorList = new JArray();
                    foreach (var err in structuralErrors)
                    {
                        var eObj = new JObject();
                        eObj["description"] = err;
                        eObj["severity"] = "Error";
                        
                        var lineMatch = System.Text.RegularExpressions.Regex.Match(err, @"Line (\d+):");
                        eObj["line"] = lineMatch.Success && int.TryParse(lineMatch.Groups[1].Value, out int l) ? l : 1;
                        
                        errorList.Add(eObj);
                    }
                    return McpResponse.Err(
                        code: "SyntaxError",
                        message: structuralErrors[0],
                        hint: "Fix the structural errors listed in error.nextSteps and retry.",
                        nextSteps: new JArray(McpResponse.NextStep("genexus_validate", new JObject { ["target"] = target, ["code"] = "<fixed code>" }, "Re-run validation after fixing the reported syntax errors.")),
                        target: target,
                        extra: new JObject { ["errors"] = errorList, ["isPreflight"] = true });
                }

                // 2. Find the exact part that is being validated.
                string normalizedPartName = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;
                KBObjectPart part = PartAccessor.GetPart(obj, normalizedPartName);

                if (part == null) return McpResponse.Ok(target: target, code: "ValidationSkipped", result: new JObject { ["message"] = "Validation not applicable for this part type." });

                if (ThemeStyleEditHelper.Applies(obj, normalizedPartName, out _))
                {
                    styleError = ThemeStyleEditHelper.ValidateInlineDataUris(code);
                    if (styleError != null)
                        return McpResponse.Err(code: "SyntaxError", message: styleError, target: target,
                            extra: new JObject { ["isPreflight"] = true });
                }

                // 3. Capture errors using a mock transaction
                var kb = _kbService.GetKB();
                using (var transaction = kb.BeginTransaction())
                {
                    string originalSource = (part as ISource)?.Source;
                    try
                    {
                        if (part is ISource sPart) sPart.Source = code;
                        
                        string saveError = null;
                        // 3. Attempt to Save the PART (this triggers the parser)
                        try {
                            part.Save();
                        } catch (Exception saveEx) {
                            saveError = saveEx.Message;
                            
                            // Check for SDK messages even if it threw
                            var sdkMsgs = part.GetSdkMessages();
                            if (!string.IsNullOrEmpty(sdkMsgs)) saveError += " | Details: " + sdkMsgs;

                            Logger.Debug("[VALIDATION] part.Save() threw: " + saveError);
                            
                            // Check for SDK lock common in GX18
                            if (saveError == "Erro" && _kbService.IsIndexing)
                                saveError = "Generic 'Erro' during SDK Save. The Knowledge Base is currently indexing, which may be blocking part updates.";
                        }

                        // 4. Capture diagnostics from ALL parts and the Object itself
                        var issues = SdkDiagnosticsHelper.GetDiagnostics(obj);
                        
                        // Also check for part-local messages that might not be in the global list
                        string localMsgs = part.GetSdkMessages();
                        if (!string.IsNullOrEmpty(localMsgs) && !issues.Any(i => localMsgs.Contains(i["description"]?.ToString() ?? "???")))
                        {
                            var localIssue = new JObject();
                            localIssue["description"] = localMsgs;
                            localIssue["severity"] = "Error";
                            localIssue["line"] = 1;
                            localIssue["part"] = normalizedPartName;
                            issues.Add(localIssue);
                        }

                        // Filter for errors
                        var errors = new JArray(issues.Where(i => i["severity"]?.ToString() == "Error"));

                        if (errors.Count == 0 && !string.IsNullOrEmpty(saveError))
                        {
                            // Fallback: If no formal diagnostics but Save failed, create one from exception.
                            // Friction-report #2: enrich a bare "Erro" with any GetSdkMessages / diagnostics
                            // text we already collected so the caller doesn't get an opaque envelope.
                            string enriched = WritePolicy.PreferDetailedMessage(saveError, localMsgs, issues);
                            var err = new JObject();
                            err["description"] = enriched;
                            err["severity"] = "Error";
                            err["line"] = 1;
                            err["part"] = normalizedPartName;
                            if (!string.Equals(enriched, saveError, StringComparison.Ordinal))
                            {
                                err["originalError"] = saveError;
                            }
                            errors.Add(err);
                        }

                        if (errors.Count > 0)
                        {
                            string topError = errors[0]["description"]?.ToString() ?? "Syntax Error";
                            // If the very first error is still bare, scan the rest for a better message.
                            if (WritePolicy.IsBareGenericError(topError))
                            {
                                foreach (var candidate in errors.OfType<JObject>())
                                {
                                    string desc = candidate["description"]?.ToString();
                                    if (!string.IsNullOrWhiteSpace(desc) && !WritePolicy.IsBareGenericError(desc))
                                    {
                                        topError = desc;
                                        break;
                                    }
                                }
                            }
                            return McpResponse.Err(
                                code: "ValidationFailed",
                                message: topError,
                                hint: "Fix the errors listed in error.nextSteps and retry validation.",
                                nextSteps: new JArray(McpResponse.NextStep("genexus_validate", new JObject { ["target"] = target, ["code"] = "<fixed code>" }, "Re-run validation after applying fixes.")),
                                target: target,
                                extra: new JObject { ["errors"] = errors });
                        }

                        return McpResponse.Ok(target: target, code: "ValidationCompleted", result: new JObject { ["message"] = "Syntax check passed" });
                    }
                    finally
                    {
                        // 5. Restore original state and ROLLBACK
                        if (part is ISource sPart && originalSource != null) sPart.Source = originalSource;
                        transaction.Rollback();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[VALIDATION] Critical Error: " + ex.Message);
                return McpResponse.Err(
                    code: "ValidationCriticalError",
                    message: ex.Message,
                    hint: "A critical error occurred during validation. Check the worker log for details.",
                    nextSteps: new JArray(McpResponse.NextStep("genexus_logs", null, "Read worker logs to diagnose the underlying exception.")),
                    target: target,
                    extra: new JObject { ["errors"] = new JArray(new JObject { ["description"] = ex.Message, ["severity"] = "Error", ["line"] = 1 }) });
            }
        }

        public string Check(string target, string code)
        {
            return ValidateCode(target, "Source", code);
        }
    }
}
