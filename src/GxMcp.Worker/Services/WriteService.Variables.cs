using System;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    // Variable CRUD (add/delete/modify) extracted from WriteService.cs (plan 007).
    // Pure move, no logic changes — see plans/007-decompose-writeservice.md.
    public partial class WriteService
    {
        private string ResolveVariableTarget(string target, ref string varName,
            out global::Artech.Architecture.Common.Objects.KBObject obj,
            out global::Artech.Genexus.Common.Parts.VariablesPart varPart,
            out global::Artech.Genexus.Common.Variable existing)
        {
            obj = null; varPart = null; existing = null;
            if (string.IsNullOrEmpty(varName)) return McpResponse.Err(
                code: "MissingParameter",
                message: "Variable name is required.",
                hint: "Pass the variable name without the leading '&'.",
                nextSteps: new JArray(McpResponse.NextStep(
                    tool: "genexus_read",
                    args: new JObject { ["name"] = target, ["part"] = "Variables" },
                    why: "Lists current variables on the object.")),
                target: target);
            varName = varName.TrimStart('&');

            obj = _objectService.FindObject(target);
            if (obj == null) return CreateWriteError("Object not found", target, "Variables", "The requested object is not available in the active Knowledge Base.");

            // v2.3.8 Task 4.4 — kind-aware accessor. Falls back through typed Get<>,
            // name-based candidates, and reflective Variables-property discovery so that
            // WebPanel / Transaction / WorkPanel / DataProvider resolve symmetrically.
            varPart = GxMcp.Worker.Structure.PartAccessor.GetVariablesPart(obj);
            if (varPart == null) return CreateWriteError("Variables part not found", target, "Variables", "The object does not expose a Variables part.", obj);

            string searchName = varName;
            existing = varPart.Variables.FirstOrDefault(v => string.Equals(v.Name, searchName, StringComparison.OrdinalIgnoreCase));
            return null;
        }

        /// Batch variant: removes all `varNames` from `target`, calling EnsureSave / ScheduleFlush once.
        /// Skips framework-managed names. Returns per-name outcomes plus aggregate counts.
        public string DeleteVariables(string target, System.Collections.Generic.IEnumerable<string> varNames)
        {
            return WrapWithPersistedState(DeleteVariablesInternal(target, varNames), target, "Variables", GxMcp.Worker.Helpers.WriteResultMeta.TypedWriter);
        }

        private string DeleteVariablesInternal(string target, System.Collections.Generic.IEnumerable<string> varNames)
        {
            try
            {
                if (varNames == null) return McpResponse.Ok(target: target, code: "WriteNoChange");
                string firstName = null;
                foreach (var n in varNames) { firstName = n; break; }
                if (firstName == null) return McpResponse.Ok(target: target, code: "WriteNoChange");

                string scratch = firstName;
                var err = ResolveVariableTarget(target, ref scratch, out var obj, out var varPart, out _);
                if (err != null) return err;

                var outcomes = new JArray();
                int removed = 0, refused = 0, missing = 0;
                foreach (var raw in varNames)
                {
                    if (string.IsNullOrEmpty(raw)) continue;
                    var name = raw.TrimStart('&');
                    if (GxMcp.Worker.Helpers.FrameworkManagedVariables.IsManaged(name))
                    {
                        outcomes.Add(new JObject { ["name"] = name, ["status"] = "Refused", ["reason"] = "framework-managed" });
                        refused++;
                        continue;
                    }
                    var hit = varPart.Variables.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (hit == null) { outcomes.Add(new JObject { ["name"] = name, ["itemStatus"] = "NotFound" }); missing++; continue; }
                    varPart.Variables.Remove(hit);
                    outcomes.Add(new JObject { ["name"] = name, ["status"] = "Removed" });
                    removed++;
                }

                if (removed > 0)
                {
                    obj.EnsureSave();
                    ScheduleFlush();
                }

                return McpResponse.Ok(
                    target: target,
                    code: removed > 0 ? "AttributeRemoved" : "WriteNoChange",
                    result: new JObject
                    {
                        ["counts"] = new JObject { ["removed"] = removed, ["refused"] = refused, ["missing"] = missing },
                        ["outcomes"] = outcomes,
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "DeleteVariableFailed",
                    message: ex.Message,
                    hint: "Check that the variable names are correct and not framework-managed.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = target, ["part"] = "Variables" },
                        why: "Lists the current variables so you can verify names before retrying.")),
                    target: target);
            }
        }

        public string DeleteVariable(string target, string varName, bool dryRun = false)
        {
            if (dryRun)
                return McpResponse.Ok(
                    target: target,
                    code: "DryRun",
                    result: new Newtonsoft.Json.Linq.JObject
                    {
                        ["preview"] = new Newtonsoft.Json.Linq.JObject
                        {
                            ["action"] = "delete",
                            ["target"] = target,
                            ["varName"] = varName
                        }
                    });
            var raw = DeleteVariableInternal(target, varName);
            MarkDirtyIfSuccess(raw, target);
            return WrapWithPersistedState(raw, target, "Variables", GxMcp.Worker.Helpers.WriteResultMeta.TypedWriter);
        }

        // v2.6.9 — parse the typed-writer raw response for a Success/NoChange
        // status and mark the target dirty. NoChange does NOT mark dirty (no
        // edit actually persisted), Success/PartialSuccess/ok do.
        private static void MarkDirtyIfSuccess(string raw, string target)
        {
            if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(target)) return;
            try
            {
                var jo = Newtonsoft.Json.Linq.JObject.Parse(raw);
                string status = jo?["status"]?.ToString();
                if (string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "PartialSuccess", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "partial", StringComparison.OrdinalIgnoreCase))
                {
                    NotePerTargetWrite(target);
                }
            }
            catch { /* best-effort */ }
        }

        private string DeleteVariableInternal(string target, string varName)
        {
            try
            {
                var err = ResolveVariableTarget(target, ref varName, out var obj, out var varPart, out var existing);
                if (err != null) return err;

                if (existing == null)
                    return McpResponse.Ok(
                        target: target,
                        code: "WriteNoChange",
                        result: new JObject { ["details"] = "Variable not present; nothing to delete." });

                if (GxMcp.Worker.Helpers.FrameworkManagedVariables.IsManaged(varName))
                {
                    return McpResponse.Err(
                        code: "FrameworkManagedVariable",
                        message: "Framework-managed variable",
                        hint: "Variable '&" + varName + "' is managed by " + GxMcp.Worker.Helpers.FrameworkManagedVariables.GetManagedBy(varName) + " and will be re-injected on save. Do not delete it.",
                        nextSteps: new JArray(McpResponse.NextStep(
                            tool: "genexus_read",
                            args: new JObject { ["name"] = target, ["part"] = "Variables" },
                            why: "Lists the current variables so you can verify which ones are user-defined.")),
                        target: target);
                }

                // Snapshot the var's internal id BEFORE Remove() — some SDK
                // builds null out the parent reference once a variable is
                // detached, which would otherwise lose the id needed to scan
                // for ghost bindings if the save throws.
                int? existingId = null;
                try
                {
                    int idx = 1;
                    foreach (var v in varPart.Variables)
                    {
                        if (ReferenceEquals(v, existing))
                        {
                            existingId = GxMcp.Worker.Helpers.VariableInjector.GetVariableInternalId(v, idx);
                            break;
                        }
                        idx++;
                    }
                }
                catch { /* best-effort */ }

                try
                {
                    varPart.Variables.Remove(existing);
                    obj.EnsureSave();
                    ScheduleFlush();
                    return McpResponse.Ok(target: target, code: "AttributeRemoved");
                }
                catch (Exception saveEx)
                {
                    var boundResp = TryBuildBoundToControlsError(saveEx, obj, varName, existingId);
                    if (boundResp != null) return boundResp;
                    throw;
                }
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "DeleteVariableFailed",
                    message: ex.Message,
                    hint: "Check that the variable is not bound to controls or used by generated code.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = target, ["part"] = "Variables" },
                        why: "Lists current variables to verify state before retrying.")),
                    target: target);
            }
        }

        // Task 4.5 — When the SDK rejects a delete/modify because the variable
        // is still bound to a control, surface a structured envelope instead of
        // a raw error string. We use a heuristic message match because the
        // concrete SDK exception type that signals this varies across GeneXus
        // builds and isn't documented; the regex catches both EN and PT-BR
        // phrasings observed in friction reports.
        private static readonly System.Text.RegularExpressions.Regex _boundToControlsRegex =
            new System.Text.RegularExpressions.Regex(
                @"(\[var:\d+\])|(control reference)|(referência de controle)|(bound to control)|(is being used)|(está sendo (usada|utilizada))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Compiled);

        internal string TryBuildBoundToControlsError(Exception ex, global::Artech.Architecture.Common.Objects.KBObject obj, string varName, int? variableId)
        {
            if (ex == null) return null;
            string flat = FlattenExceptionMessages(ex);
            if (string.IsNullOrEmpty(flat) || !_boundToControlsRegex.IsMatch(flat)) return null;

            string resolved = GxMcp.Worker.Helpers.WebFormSchemaHints.ResolveVarBindings(flat, obj);

            var bindings = new JArray();
            try
            {
                if (variableId.HasValue && variableId.Value > 0)
                {
                    string xml = GxMcp.Worker.Helpers.WebFormXmlHelper.ReadEditableXml(obj);
                    var hits = GxMcp.Worker.Helpers.WebFormSchemaHints.FindVarBindings(xml, variableId.Value);
                    foreach (var b in hits)
                    {
                        bindings.Add(new JObject
                        {
                            ["element"] = b.Element,
                            ["attribute"] = b.Attribute,
                            ["controlId"] = b.ControlId,
                            ["controlName"] = b.ControlName,
                        });
                    }
                }
            }
            catch { /* best-effort — bindings list is advisory */ }

            return McpResponse.Err(
                code: "BoundToControls",
                message: $"Variable '&{varName}' is bound to one or more controls; remove the bindings before deleting/modifying.",
                hint: "Remove or rebind the controls listed in 'bindings' from the WebForm layout before deleting/modifying this variable.",
                nextSteps: new JArray(McpResponse.NextStep(
                    tool: "genexus_read",
                    args: new JObject { ["name"] = resolved ?? varName, ["part"] = "WebForm" },
                    why: "Read the WebForm layout to locate and remove the controls bound to this variable.")),
                target: null,
                extra: new JObject { ["details"] = resolved, ["bindings"] = bindings });
        }

        private static string FlattenExceptionMessages(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            for (var cur = ex; cur != null; cur = cur.InnerException)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(cur.Message);
            }
            return sb.ToString();
        }

        // issue #32 item 1 — shared SDK construction used by AddVariable (single) and
        // AddVariables (batch). Result of building one typed variable into a part.
        private enum VarBuildResult { Added, DomainNotFound, PrimitiveNotApplied }

        // Builds one Variable from an already-validated TypeResolution and adds it to
        // varPart IN MEMORY (no save, no envelope — the caller owns those). Returns
        // DomainNotFound when the typeName looked like an SDT/BC/Domain reference but the
        // SDK couldn't resolve it in the KB, so the caller can surface UnknownType.
        private VarBuildResult BuildResolvedVariableInto(
            global::Artech.Genexus.Common.Parts.VariablesPart varPart, string varName,
            GxMcp.Worker.Helpers.TypeResolution resolution, string resolvedTypeForSdk,
            int? resolvedLength, int? resolvedDecimals,
            int? length, int? decimals, bool? collection, string originalTypeName)
        {
            var newVar = new global::Artech.Genexus.Common.Variable(varPart);
            newVar.Name = varName;

            if (resolution != null && resolution.CanonicalType != "DomainReference"
                && VariableInjector.TryParseDbType(resolvedTypeForSdk, out var dbType))
            {
                newVar.Type = dbType;
                try
                {
                    // Explicit length/decimals args (issue #28 item 8) win over the
                    // value parsed out of typeName; otherwise fall back to the parsed one.
                    int? effLen = length ?? resolvedLength;
                    int? effDec = decimals ?? resolvedDecimals;
                    if (effLen.HasValue) newVar.Length = effLen.Value;
                    if (effDec.HasValue) newVar.Decimals = effDec.Value;
                }
                catch { /* best-effort — SDK may reject for some types */ }
            }
            else
            {
                // issue #34: a recognized primitive (resolver said so, not a DomainReference)
                // that TryParseDbType can't map is a mapping bug, not a KB-object reference.
                // Never fall through to add a default-typed (NUMERIC) variable and report
                // success — surface it so the caller sees the type wasn't applied.
                if (resolution != null && resolution.CanonicalType != "DomainReference")
                {
                    return VarBuildResult.PrimitiveNotApplied;
                }

                var targetObj = VariableInjector.ResolveTypeObject(varPart.Model, resolvedTypeForSdk);
                // Dotted SDT-item type (e.g. "Messages.Message") — a single element of a collection
                // SDT. Tried BEFORE the ResolveTypeObject binding, which strips ".Message" and binds
                // the whole (collection) SDT, collapsing item and collection to the same variable.
                // The SDK's type-picker resolver returns the item-level AttCustomType for the dotted
                // form. Only fires for dotted names, so the plain-SDT path below is unaffected.
                if (VariableInjector.TryBindSdtItemType(newVar, resolvedTypeForSdk))
                {
                }
                else if (targetObj != null)
                {
                    if (targetObj is global::Artech.Genexus.Common.Objects.Domain dom)
                        newVar.DomainBasedOn = dom;
                    else if (targetObj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                        VariableInjector.BindVariableToSdt(newVar, targetObj);
                    else if (targetObj is global::Artech.Genexus.Common.Objects.Transaction trn && trn.IsBusinessComponent)
                        VariableInjector.BindVariableToBC(newVar, targetObj);
                }
                // Built-in GeneXus data types (HttpClient, WebSession, Location, ...) aren't KB
                // objects, so ResolveTypeObject can't find them — resolve by name through the SDK's
                // own type registry (issue #45). Covers all ~137 built-ins generically.
                else if (VariableInjector.TryBindGenexusDataType(newVar, resolvedTypeForSdk))
                {
                }
                // Legacy hardcoded fallback (issue #33) — only if the SDK registry path above is
                // unavailable in a given headless build.
                else if (VariableInjector.TryBindBuiltinUserDefinedType(newVar, resolvedTypeForSdk))
                {
                }
                else if (resolution != null && resolution.CanonicalType == "DomainReference"
                         && !string.IsNullOrEmpty(originalTypeName) && !originalTypeName.StartsWith("&"))
                {
                    return VarBuildResult.DomainNotFound;
                }
            }
            if (collection == true) { try { newVar.IsCollection = true; } catch { /* not all types collectible */ } }
            varPart.Variables.Add(newVar);
            return VarBuildResult.Added;
        }

        // No typeName: CreateVariable inherits a same-named attribute's type (issue #28
        // item 11) or applies the naming heuristic. Explicit length/decimals/collection
        // args still override the result. Adds to varPart in memory (no save).
        private void AddInferredVariableInto(global::Artech.Genexus.Common.Parts.VariablesPart varPart,
            string varName, int? length, int? decimals, bool? collection)
        {
            var newVar = VariableInjector.CreateVariable(varPart, varName);
            try
            {
                if (length.HasValue) newVar.Length = length.Value;
                if (decimals.HasValue) newVar.Decimals = decimals.Value;
            }
            catch { /* best-effort */ }
            if (collection == true) { try { newVar.IsCollection = true; } catch { } }
            varPart.Variables.Add(newVar);
        }

        // issue #32 item 1 — batch add. Resolves the target once and adds every variable in
        // `variables` before a single EnsureSave / ScheduleFlush. Each item is
        // { varName|name, typeName?, length?, decimals?, collection? }. Per-item outcomes let
        // the agent see which vars were Added / already Exist / Failed without N round-trips.
        public string AddVariables(string target, JArray variables, bool dryRun = false)
        {
            if (dryRun)
            {
                var preview = new JArray();
                if (variables != null)
                    foreach (var v in variables) preview.Add(v.DeepClone());
                return McpResponse.Ok(
                    target: target,
                    code: "DryRun",
                    result: new JObject
                    {
                        ["preview"] = new JObject
                        {
                            ["action"] = "add",
                            ["target"] = target,
                            ["variables"] = preview
                        }
                    });
            }
            var raw = AddVariablesInternal(target, variables);
            MarkDirtyIfSuccess(raw, target);
            return WrapWithPersistedState(raw, target, "Variables", GxMcp.Worker.Helpers.WriteResultMeta.TypedWriter);
        }

        private string AddVariablesInternal(string target, JArray variables)
        {
            try
            {
                if (variables == null || variables.Count == 0)
                    return McpResponse.Ok(target: target, code: "WriteNoChange",
                        result: new JObject { ["details"] = "No variables provided." });

                // Resolve the target object / VariablesPart once for the whole batch.
                string scratch = "_";
                var err = ResolveVariableTarget(target, ref scratch, out var obj, out var varPart, out _);
                if (err != null) return err;

                var outcomes = new JArray();
                int added = 0, existed = 0, failed = 0;
                var domainBound = new System.Collections.Generic.List<(string VarName, string DomainName)>();

                foreach (var item in variables)
                {
                    var jo = item as JObject;
                    if (jo == null)
                    {
                        failed++;
                        outcomes.Add(new JObject { ["status"] = "Failed", ["reason"] = "Item is not an object." });
                        continue;
                    }

                    string vName = (jo["varName"] ?? jo["name"])?.ToString();
                    if (string.IsNullOrWhiteSpace(vName))
                    {
                        failed++;
                        outcomes.Add(new JObject { ["status"] = "Failed", ["reason"] = "Missing varName." });
                        continue;
                    }
                    vName = vName.TrimStart('&');

                    string vType = jo["typeName"]?.ToString();
                    int? vLen = jo["length"]?.ToObject<int?>();
                    int? vDec = jo["decimals"]?.ToObject<int?>();
                    bool? vColl = jo["collection"]?.ToObject<bool?>();

                    if (varPart.Variables.Any(v => string.Equals(v.Name, vName, StringComparison.OrdinalIgnoreCase)))
                    {
                        existed++;
                        outcomes.Add(new JObject { ["name"] = vName, ["itemStatus"] = "Exists" });
                        continue;
                    }

                    // Type resolution (mirrors AddVariableInternal's Task 4.2 gate).
                    GxMcp.Worker.Helpers.TypeResolution res = null;
                    string rSdk = vType;
                    int? rLen = null, rDec = null;
                    if (!string.IsNullOrEmpty(vType))
                    {
                        res = GxMcp.Worker.Helpers.VariableTypeResolver.Resolve(vType);
                        if (!res.Recognized)
                        {
                            failed++;
                            outcomes.Add(new JObject
                            {
                                ["name"] = vName,
                                ["status"] = "Failed",
                                ["reason"] = "UnknownType",
                                ["suggestion"] = res.Suggestion
                            });
                            continue;
                        }
                        if (res.CanonicalType == "DomainReference" && !string.IsNullOrEmpty(res.DomainName))
                        {
                            rSdk = res.DomainName;
                            domainBound.Add((vName, res.DomainName));
                        }
                        else { rLen = res.Length; rDec = res.Decimals; rSdk = res.CanonicalType; }
                    }

                    try
                    {
                        if (!string.IsNullOrEmpty(vType))
                        {
                            var batchBuild = BuildResolvedVariableInto(varPart, vName, res, rSdk, rLen, rDec, vLen, vDec, vColl, vType);
                            if (batchBuild == VarBuildResult.DomainNotFound)
                            {
                                failed++;
                                outcomes.Add(new JObject
                                {
                                    ["name"] = vName,
                                    ["status"] = "Failed",
                                    ["reason"] = "UnknownType",
                                    ["details"] = $"Type '{vType}' not found in KB."
                                });
                                continue;
                            }
                            if (batchBuild == VarBuildResult.PrimitiveNotApplied)
                            {
                                // issue #34: don't silently persist a default NUMERIC(4) for a
                                // recognized-but-unmappable primitive.
                                failed++;
                                outcomes.Add(new JObject
                                {
                                    ["name"] = vName,
                                    ["status"] = "Failed",
                                    ["reason"] = "TypeNotApplied",
                                    ["details"] = $"Recognized type '{vType}' could not be applied (SDK type-map gap); variable skipped."
                                });
                                continue;
                            }
                        }
                        else
                        {
                            AddInferredVariableInto(varPart, vName, vLen, vDec, vColl);
                        }
                        added++;
                        outcomes.Add(new JObject { ["name"] = vName, ["status"] = "Added" });
                    }
                    catch (Exception exItem)
                    {
                        failed++;
                        outcomes.Add(new JObject { ["name"] = vName, ["status"] = "Failed", ["reason"] = exItem.Message });
                    }
                }

                if (added > 0)
                {
                    obj.EnsureSave();
                    ScheduleFlush();

                    // issue #56: the SDK silently drops DomainBasedOn on some 18.0.16 builds
                    // (variable persists with an empty BasedOnReference and spec fails with
                    // spc0056). Re-read the persisted Variables part and confirm every
                    // Domain-bound variable kept its reference; fail loudly if not.
                    var verifyErr = VerifyDomainReferencesPersisted(target, domainBound);
                    if (verifyErr != null) return verifyErr;
                }

                return McpResponse.Ok(
                    target: target,
                    code: added > 0 ? "VariableAdded" : "WriteNoChange",
                    result: new JObject
                    {
                        ["counts"] = new JObject { ["added"] = added, ["existed"] = existed, ["failed"] = failed },
                        ["outcomes"] = outcomes
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "AddVariableFailed",
                    message: ex.Message,
                    hint: "Verify each variable name and type. Check that the object exists and has a Variables part.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = target, ["part"] = "Variables" },
                        why: "Lists current variables to confirm state.")),
                    target: target);
            }
        }

        public string AddVariable(string target, string varName, string typeName = null, bool dryRun = false,
            int? length = null, int? decimals = null, bool? collection = null)
        {
            if (dryRun)
                return McpResponse.Ok(
                    target: target,
                    code: "DryRun",
                    result: new Newtonsoft.Json.Linq.JObject
                    {
                        ["preview"] = new Newtonsoft.Json.Linq.JObject
                        {
                            ["action"] = "add",
                            ["target"] = target,
                            ["varName"] = varName,
                            ["typeName"] = typeName,
                            ["length"] = length,
                            ["decimals"] = decimals,
                            ["collection"] = collection
                        }
                    });
            var raw = AddVariableInternal(target, varName, typeName, length, decimals, collection);
            MarkDirtyIfSuccess(raw, target);
            return WrapWithPersistedState(raw, target, "Variables", GxMcp.Worker.Helpers.WriteResultMeta.TypedWriter);
        }

        // issue #28 items 8/9/11:
        //   length/decimals  — explicit override of the type-embedded length (fixes the
        //                       Character(20) default that was too short for API keys /
        //                       message strings). When omitted, the length parsed from
        //                       typeName (e.g. Character(200)) still applies.
        //   collection        — sets Variable.IsCollection so SDT/scalar collection vars
        //                       are declarable directly, without the AttCollection dance.
        //   (item 11) when typeName is omitted, CreateVariable already inherits the type of
        //   a same-named attribute via FindAttribute — length/decimals below still override.
        private string AddVariableInternal(string target, string varName, string typeName = null,
            int? length = null, int? decimals = null, bool? collection = null)
        {
            try
            {
                // Task 4.2 — validate typeName via VariableTypeResolver before any SDK work,
                // so unknown types never silently default to NUMERIC.
                GxMcp.Worker.Helpers.TypeResolution resolution = null;
                string resolvedTypeForSdk = typeName;
                int? resolvedLength = null;
                int? resolvedDecimals = null;
                var domainBound = new System.Collections.Generic.List<(string VarName, string DomainName)>();
                if (!string.IsNullOrEmpty(typeName))
                {
                    resolution = GxMcp.Worker.Helpers.VariableTypeResolver.Resolve(typeName);
                    if (!resolution.Recognized)
                    {
                        var accepted = new JArray();
                        if (resolution.AcceptedList != null)
                            foreach (var a in resolution.AcceptedList) accepted.Add(a);
                        return McpResponse.Err(
                            code: "UnknownType",
                            message: $"Unknown typeName '{typeName}'. Did you mean '{resolution.Suggestion}'?",
                            hint: $"Use one of the accepted type names. Nearest match: '{resolution.Suggestion}'.",
                            nextSteps: new JArray(McpResponse.NextStep(
                                tool: "genexus_add_variable",
                                args: new JObject { ["target"] = target, ["varName"] = varName, ["typeName"] = resolution.Suggestion },
                                why: "Retries the add with the nearest recognized type name.")),
                            target: target,
                            extra: new JObject { ["suggestion"] = resolution.Suggestion, ["accepted"] = accepted });
                    }
                    if (resolution.CanonicalType == "DomainReference" && !string.IsNullOrEmpty(resolution.DomainName))
                    {
                        // Pass the raw name to the existing ResolveTypeObject path (SDT / BC / Domain).
                        resolvedTypeForSdk = resolution.DomainName;
                        domainBound.Add((varName, resolution.DomainName));
                    }
                    else
                    {
                        // Canonicalise — e.g. VarChar(120) → Character(120) — so TryParseDbType picks
                        // up the canonical eDBType instead of an alias that may not round-trip.
                        resolvedLength = resolution.Length;
                        resolvedDecimals = resolution.Decimals;
                        resolvedTypeForSdk = resolution.CanonicalType;
                    }
                }

                var err = ResolveVariableTarget(target, ref varName, out var obj, out var varPart, out var existing);
                if (err != null) return err;

                if (existing != null)
                    return McpResponse.Ok(
                        target: target,
                        code: "WriteNoChange",
                        result: new JObject { ["details"] = "Variable already exists; no change applied." });

                if (!string.IsNullOrEmpty(typeName))
                {
                    // issue #32 item 1: construction extracted into BuildResolvedVariableInto so
                    // the batch AddVariables path reuses the exact same SDK binding logic.
                    var buildResult = BuildResolvedVariableInto(varPart, varName, resolution, resolvedTypeForSdk,
                            resolvedLength, resolvedDecimals, length, decimals, collection, typeName);
                    if (buildResult == VarBuildResult.DomainNotFound)
                    {
                        // FR#4 (friction-report 2026-05-19): resolver accepted the bare name as a
                        // potential SDT/BC/Domain reference but SDK couldn't find it in the KB.
                        return McpResponse.Err(
                            code: "UnknownType",
                            message: $"Type '{typeName}' not found in KB. Expected primitive (Character/Numeric/etc), SDT name (e.g. SdtFoo), BC, or Domain.",
                            hint: "Verify the SDT/Domain name via genexus_list_objects or use a primitive type like Character(40).",
                            nextSteps: new JArray(McpResponse.NextStep(
                                tool: "genexus_list_objects",
                                args: new JObject { ["name"] = typeName },
                                why: "Finds SDTs and Domains whose name matches, confirming the correct spelling.")),
                            target: target,
                            extra: new JObject { ["typeName"] = typeName });
                    }
                    if (buildResult == VarBuildResult.PrimitiveNotApplied)
                    {
                        // issue #34: the resolver recognized a primitive but the SDK type map
                        // couldn't apply it. Fail loudly instead of persisting a default NUMERIC(4).
                        return McpResponse.Err(
                            code: "TypeNotApplied",
                            message: $"Recognized type '{typeName}' could not be applied to the variable (no matching SDK type). The variable was NOT created to avoid a silent NUMERIC fallback.",
                            hint: "Report this type name; it is a mapping gap. Use a known primitive (Character/Numeric/Date/DateTime/Boolean/Blob/Image/GUID) meanwhile.",
                            target: target,
                            extra: new JObject { ["typeName"] = typeName });
                    }
                }
                else
                {
                    AddInferredVariableInto(varPart, varName, length, decimals, collection);
                }

                obj.EnsureSave();
                ScheduleFlush();

                // issue #56: read back the persisted Variables part and confirm every
                // Domain-bound variable kept its reference (see VerifyDomainReferencesPersisted).
                var verifyErr = VerifyDomainReferencesPersisted(target, domainBound);
                if (verifyErr != null) return verifyErr;

                return McpResponse.Ok(target: target, code: "VariableAdded");
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "AddVariableFailed",
                    message: ex.Message,
                    hint: "Verify the variable name and type are valid. Check that the object exists and has a Variables part.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = target, ["part"] = "Variables" },
                        why: "Lists current variables to confirm state.")),
                    target: target);
            }
        }

        // issue #56 — post-save read-back for Domain-based variable types. On some
        // GeneXus 18.0.16 builds the SDK accepts DomainBasedOn but drops it at save,
        // leaving the variable with an empty BasedOnReference — the persisted variable
        // then fails spec with spc0056 (Data:249,...,[]). Returns a serialized error
        // envelope (code VariableDomainReferenceNotPersisted) listing every binding the
        // re-read no longer shows, or null when all Domain references persisted.
        private string VerifyDomainReferencesPersisted(string target,
            System.Collections.Generic.List<(string VarName, string DomainName)> expected)
        {
            if (expected == null || expected.Count == 0) return null;

            string text = "";
            try
            {
                string readJson = _objectService.ReadObjectSource(target, "Variables", null, null, "mcp", true, null);
                if (!string.IsNullOrWhiteSpace(readJson))
                {
                    var readObj = JObject.Parse(readJson);
                    text = readObj["source"]?.ToString()
                        ?? readObj["content"]?.ToString()
                        ?? readObj["parts"]?["Variables"]?.ToString()
                        ?? "";
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("[VARIABLES-POST-CHECK] Re-read failed for " + target + ": " + ex.Message);
                return null;
            }
            if (string.IsNullOrWhiteSpace(text)) return null;

            var dropped = new JArray();
            foreach (var (vName, domain) in DroppedDomainBindings(text, expected))
                dropped.Add(new JObject { ["name"] = vName, ["domain"] = domain });
            if (dropped.Count == 0) return null;

            return McpResponse.Err(
                code: "VariableDomainReferenceNotPersisted",
                message: $"The SDK saved the variable(s) but dropped their Domain type reference (BasedOnReference is empty) — spec would fail with spc0056. Re-create the variable from the GeneXus IDE.",
                hint: "On this GeneXus build, Domain-based variable types written through the SDK do not persist their reference. Create the variable in the IDE (right-click object → Properties → Variables → New), or add it with a primitive type.",
                nextSteps: new JArray(McpResponse.NextStep(
                    tool: "genexus_read",
                    args: new JObject { ["name"] = target, ["part"] = "Variables" },
                    why: "Shows the persisted state; the listed variables exist but without their Domain type.")),
                target: target,
                extra: new JObject { ["variables"] = dropped });
        }

        // Static matcher: which of the expected (varName, domain) bindings are absent from
        // the persisted Variables text? The text format is one `&Name : Type` per line.
        internal static System.Collections.Generic.List<(string VarName, string DomainName)> DroppedDomainBindings(
            string variablesText, System.Collections.Generic.IEnumerable<(string VarName, string DomainName)> expected)
        {
            var dropped = new System.Collections.Generic.List<(string, string)>();
            if (string.IsNullOrEmpty(variablesText) || expected == null) return dropped;
            foreach (var (vName, domain) in expected)
            {
                // Optional module qualifier (e.g. RootModule.IDManual) is a valid
                // persisted form of the same reference.
                string pattern = @"&\b" + Regex.Escape(vName.TrimStart('&')) + @"\b\s*:\s*(?:\w+\.)*" + Regex.Escape(domain) + @"\b";
                if (!Regex.IsMatch(variablesText, pattern, RegexOptions.IgnoreCase))
                    dropped.Add((vName, domain));
            }
            return dropped;
        }

        // ── Task 4.3 (v2.3.8) — genexus_modify_variable ──────────────────────────
        // Atomically change a variable's type while preserving its name (and
        // description when possible). Implemented as delete+add over the same
        // VariablesPart, with a snapshot of the pre-change variable set so we
        // can roll back if obj.Save() throws.
        public string ModifyVariable(string target, string varName, string newTypeName, string basedOn = null, bool dryRun = false,
            int? length = null, int? decimals = null, bool? collection = null)
        {
            if (dryRun)
                return McpResponse.Ok(
                    target: target,
                    code: "DryRun",
                    result: new Newtonsoft.Json.Linq.JObject
                    {
                        ["preview"] = new Newtonsoft.Json.Linq.JObject
                        {
                            ["action"] = "modify",
                            ["target"] = target,
                            ["varName"] = varName,
                            ["newTypeName"] = newTypeName,
                            ["basedOn"] = basedOn,
                            ["length"] = length,
                            ["decimals"] = decimals,
                            ["collection"] = collection
                        }
                    });
            var raw = ModifyVariableInternal(target, varName, newTypeName, basedOn, length, decimals, collection);
            MarkDirtyIfSuccess(raw, target);
            return WrapWithPersistedState(raw, target, "Variables", GxMcp.Worker.Helpers.WriteResultMeta.TypedWriter);
        }

        private string ModifyVariableInternal(string target, string varName, string newTypeName, string basedOn,
            int? length = null, int? decimals = null, bool? collection = null)
        {
            // Gate 1 — resolve newTypeName up front, before any SDK / KB call.
            // Mirrors AddVariable's Task 4.2 envelope shape exactly.
            GxMcp.Worker.Helpers.TypeResolution resolution = null;
            string resolvedTypeForSdk = newTypeName;
            int? resolvedLength = null;
            int? resolvedDecimals = null;
            if (string.IsNullOrEmpty(newTypeName))
            {
                return McpResponse.Err(
                    code: "UnknownType",
                    message: "newTypeName is required for genexus_modify_variable.",
                    hint: "Pass a valid type such as Character(40), Numeric(8.0), Date, DateTime, Boolean, VarChar(N), or a Domain name.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_modify_variable",
                        args: new JObject { ["target"] = target, ["varName"] = varName, ["newTypeName"] = "Character(40)" },
                        why: "Example retry with Character(40).")),
                    target: target,
                    extra: new JObject
                    {
                        ["suggestion"] = "Character(40)",
                        ["accepted"] = new JArray { "Character(N)", "Numeric(N.D)", "Date", "DateTime", "Boolean", "VarChar(N)", "<DomainName>" }
                    });
            }

            resolution = GxMcp.Worker.Helpers.VariableTypeResolver.Resolve(newTypeName);
            if (!resolution.Recognized)
            {
                var accepted = new JArray();
                if (resolution.AcceptedList != null)
                    foreach (var a in resolution.AcceptedList) accepted.Add(a);
                return McpResponse.Err(
                    code: "UnknownType",
                    message: $"Unknown typeName '{newTypeName}'. Did you mean '{resolution.Suggestion}'?",
                    hint: $"Use one of the accepted type names. Nearest match: '{resolution.Suggestion}'.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_modify_variable",
                        args: new JObject { ["target"] = target, ["varName"] = varName, ["newTypeName"] = resolution.Suggestion },
                        why: "Retries the modify with the nearest recognized type name.")),
                    target: target,
                    extra: new JObject { ["suggestion"] = resolution.Suggestion, ["accepted"] = accepted });
            }

            if (resolution.CanonicalType == "DomainReference" && !string.IsNullOrEmpty(resolution.DomainName))
            {
                resolvedTypeForSdk = resolution.DomainName;
            }
            else
            {
                resolvedLength = resolution.Length;
                resolvedDecimals = resolution.Decimals;
                resolvedTypeForSdk = resolution.CanonicalType;
            }
            // `basedOn` (optional) takes precedence over a parsed DomainReference —
            // gives the caller explicit control when the typeName is ambiguous.
            if (!string.IsNullOrWhiteSpace(basedOn))
            {
                resolvedTypeForSdk = basedOn;
                resolution = new GxMcp.Worker.Helpers.TypeResolution
                {
                    Recognized = true,
                    CanonicalType = "DomainReference",
                    DomainName = basedOn,
                    Suggestion = basedOn,
                    AcceptedList = resolution?.AcceptedList
                };
            }

            try
            {
                var err = ResolveVariableTarget(target, ref varName, out var obj, out var varPart, out var existing);
                if (err != null) return err;

                if (existing == null)
                {
                    return McpResponse.Err(
                        code: "VariableNotFound",
                        message: $"Variable '&{varName}' not found on '{target}'.",
                        hint: "Read the Variables part to see which variables exist on this object.",
                        nextSteps: new JArray(McpResponse.NextStep(
                            tool: "genexus_read",
                            args: new JObject { ["name"] = target, ["part"] = "Variables" },
                            why: "Lists all declared variables on the object.")),
                        target: target);
                }

                if (GxMcp.Worker.Helpers.FrameworkManagedVariables.IsManaged(varName))
                {
                    return McpResponse.Err(
                        code: "FrameworkManagedVariable",
                        message: "Framework-managed variable",
                        hint: "Variable '&" + varName + "' is managed by " + GxMcp.Worker.Helpers.FrameworkManagedVariables.GetManagedBy(varName) + " and will be re-injected on save. Do not modify it.",
                        nextSteps: new JArray(McpResponse.NextStep(
                            tool: "genexus_read",
                            args: new JObject { ["name"] = target, ["part"] = "Variables" },
                            why: "Lists current variables so you can identify user-defined ones.")),
                        target: target);
                }

                // Snapshot for rollback: capture every variable's identity + shape so we
                // can re-add the original if obj.Save() throws halfway through.
                string preservedDescription = null;
                try { preservedDescription = existing.Description; } catch { /* SDK may not expose */ }

                // Task 4.5 — capture internal id before Remove() so a
                // BoundToControls rejection can still scan the layout XML.
                int? existingVarId = null;
                try
                {
                    int idx = 1;
                    foreach (var v in varPart.Variables)
                    {
                        if (ReferenceEquals(v, existing))
                        {
                            existingVarId = GxMcp.Worker.Helpers.VariableInjector.GetVariableInternalId(v, idx);
                            break;
                        }
                        idx++;
                    }
                }
                catch { /* best-effort */ }

                // Atomic delete + add: keep the VariablesPart change in memory until
                // obj.Save() either succeeds or we restore the original variable.
                global::Artech.Genexus.Common.Variable originalSnapshot = existing;

                // issue #47 — capture the original variable's non-primitive type name (SDT / BC /
                // built-in GeneXus data type) BEFORE Remove(), via the same read-path resolver
                // GetVariablesAsText uses internally. Rollback needs this to re-bind the original
                // shape instead of silently downgrading to a bare scalar. Stays null for a plain
                // scalar/domain original, or when the resolver itself can't name the binding —
                // either way rollback then falls back to exactly today's scalar-only restore.
                string originalTypeName = null;
                bool bindingNotRestored = false;
                try
                {
                    if (originalSnapshot.Type == global::Artech.Genexus.Common.eDBType.GX_SDT
                        || originalSnapshot.Type == global::Artech.Genexus.Common.eDBType.GX_BUSCOMP
                        || originalSnapshot.Type == global::Artech.Genexus.Common.eDBType.GX_USRDEFTYP
                        || originalSnapshot.Type == global::Artech.Genexus.Common.eDBType.GX_EXTERNAL_OBJECT)
                    {
                        string dumpedText = VariableInjector.GetVariablesAsText(varPart);
                        originalTypeName = ExtractOriginalTypeNameFromDump(dumpedText, varName);
                    }
                }
                catch { /* best-effort; null means fall back to scalar-only restore */ }

                // issue #36.2 — track whether a primitive eDBType was actually applied so the
                // success message can report the REAL persisted type (e.g. Blob→BINARY), never
                // a bare echo of the requested canonical.
                bool appliedPrimitive = false;
                // issue #46 — the meaningful type name for a non-primitive bind (SDT / BC / Domain /
                // built-in GeneXus data type like "Properties"). Stays null until a bind actually
                // succeeds, so a bind that silently applied nothing can be caught below instead of
                // persisting a default NUMERIC(4) and reporting the internal "DomainReference" token.
                string boundTypeName = null;
                try
                {
                    varPart.Variables.Remove(existing);

                    var newVar = new global::Artech.Genexus.Common.Variable(varPart);
                    newVar.Name = varName;
                    if (!string.IsNullOrEmpty(preservedDescription))
                    {
                        try { newVar.Description = preservedDescription; } catch { /* best-effort */ }
                    }

                    if (resolution.CanonicalType != "DomainReference"
                        && VariableInjector.TryParseDbType(resolvedTypeForSdk, out var dbType))
                    {
                        newVar.Type = dbType;
                        appliedPrimitive = true;
                        try
                        {
                            // Explicit length/decimals args (issue #28 item 8) win over the parsed value.
                            int? effLen = length ?? resolvedLength;
                            int? effDec = decimals ?? resolvedDecimals;
                            if (effLen.HasValue) newVar.Length = effLen.Value;
                            if (effDec.HasValue) newVar.Decimals = effDec.Value;
                        }
                        catch { /* SDK may reject for some types */ }
                    }
                    else if (resolution.CanonicalType != "DomainReference")
                    {
                        // issue #34: recognized primitive that TryParseDbType couldn't map —
                        // throw so the rollback restores the original variable instead of
                        // silently retyping it to the default NUMERIC(4) and reporting success.
                        throw new InvalidOperationException(
                            $"Recognized type '{newTypeName}' could not be applied (no matching SDK type); original variable preserved.");
                    }
                    else
                    {
                        var targetObj = VariableInjector.ResolveTypeObject(varPart.Model, resolvedTypeForSdk);
                        // Dotted SDT-item type (e.g. "Messages.Message") — bind the item level, not the
                        // whole (collection) SDT ResolveTypeObject strips down to. Tried first; only
                        // fires for dotted names so plain-SDT/Domain/BC retypes are unaffected.
                        if (VariableInjector.TryBindSdtItemType(newVar, resolvedTypeForSdk))
                        {
                            boundTypeName = resolvedTypeForSdk;
                        }
                        else if (targetObj is global::Artech.Genexus.Common.Objects.Domain dom)
                        {
                            newVar.DomainBasedOn = dom;
                            boundTypeName = dom.Name;
                        }
                        else if (targetObj != null && targetObj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                        {
                            VariableInjector.BindVariableToSdt(newVar, targetObj);
                            boundTypeName = targetObj.Name;
                        }
                        else if (targetObj is global::Artech.Genexus.Common.Objects.Transaction trn && trn.IsBusinessComponent)
                        {
                            VariableInjector.BindVariableToBC(newVar, targetObj);
                            boundTypeName = targetObj.Name;
                        }
                        // Built-in GeneXus data types (HttpClient, WebSession, Properties, ...) via the
                        // SDK registry (issue #45/#46), with the legacy hardcoded map as fallback (#33).
                        else if (VariableInjector.TryBindGenexusDataType(newVar, resolvedTypeForSdk))
                        {
                            boundTypeName = resolvedTypeForSdk;
                        }
                        else if (VariableInjector.TryBindBuiltinUserDefinedType(newVar, resolvedTypeForSdk))
                        {
                            boundTypeName = resolvedTypeForSdk;
                        }
                        else
                        {
                            // issue #46 — nothing resolved the name: not a KB Domain/SDT/BC, not a
                            // built-in GeneXus data type. Throwing here rolls back to the original
                            // variable instead of persisting a silent default NUMERIC(4) and reporting
                            // success — the exact failure the reporter hit on the pre-#45 build.
                            throw new InvalidOperationException(
                                $"Type '{newTypeName}' could not be resolved to a Domain, SDT, Business Component, or built-in GeneXus data type; original variable preserved.");
                        }
                    }

                    // issue #28 item 9: collection flag (null = leave as-is on retype).
                    if (collection.HasValue) { try { newVar.IsCollection = collection.Value; } catch { } }
                    varPart.Variables.Add(newVar);

                    obj.EnsureSave();
                    ScheduleFlush();

                    // issue #36.2 — report the ACTUAL persisted type. For a primitive, read the
                    // eDBType the SDK stored (Blob/Binary persist as BINARY) plus the effective
                    // length/decimals; if it differs from what was requested, say so explicitly so
                    // a coercion can never masquerade as the requested type. Non-primitive (SDT /
                    // Domain / BC / WebSession) binds keep the canonical name, which is meaningful.
                    // issue #46 — the requested type as the caller named it. "DomainReference" is an
                    // internal resolver token, never a real type: for a non-primitive bind report the
                    // name that was actually requested/bound (e.g. "Properties", a Domain/SDT name).
                    string requestedDisplay = appliedPrimitive || boundTypeName == null
                        ? resolution.CanonicalType
                        : boundTypeName;
                    var resultPayload = new JObject { ["requestedType"] = requestedDisplay };
                    string persistedDesc = requestedDisplay;
                    if (appliedPrimitive)
                    {
                        try
                        {
                            persistedDesc = newVar.Type.ToString();
                            int? shownLen = length ?? resolvedLength;
                            int? shownDec = decimals ?? resolvedDecimals;
                            if (shownLen.HasValue)
                                persistedDesc += "(" + shownLen.Value + (shownDec.HasValue && shownDec.Value > 0 ? "," + shownDec.Value : "") + ")";
                        }
                        catch { persistedDesc = resolution.CanonicalType; }
                    }
                    else if (boundTypeName != null)
                    {
                        persistedDesc = boundTypeName;
                    }
                    resultPayload["persistedType"] = persistedDesc;
                    bool coerced = appliedPrimitive && !string.Equals(persistedDesc.Split('(')[0], resolution.CanonicalType, StringComparison.OrdinalIgnoreCase);
                    resultPayload["details"] = coerced
                        ? $"Variable '&{varName}' retyped to '{persistedDesc}' (requested '{resolution.CanonicalType}', persisted as its SDK type)."
                        : $"Variable '&{varName}' retyped to '{persistedDesc}'.";

                    return McpResponse.Ok(
                        target: target,
                        code: "VariableRenamed",
                        result: resultPayload);
                }
                catch (Exception ex)
                {
                    // Best-effort rollback: re-add the original variable if it was
                    // removed but the new one failed to save. We can't re-insert the
                    // captured `originalSnapshot` directly (SDK may consider it
                    // detached after Remove), so reconstruct from preserved fields.
                    try
                    {
                        if (!varPart.Variables.Any(v => string.Equals(v.Name, varName, StringComparison.OrdinalIgnoreCase)))
                        {
                            var restored = new global::Artech.Genexus.Common.Variable(varPart);
                            restored.Name = varName;
                            try { if (preservedDescription != null) restored.Description = preservedDescription; } catch { }
                            try { restored.Type = originalSnapshot.Type; } catch { }
                            try { restored.Length = originalSnapshot.Length; } catch { }
                            try { restored.Decimals = originalSnapshot.Decimals; } catch { }
                            try { if (originalSnapshot.DomainBasedOn != null) restored.DomainBasedOn = originalSnapshot.DomainBasedOn; } catch { }

                            // issue #47 — best-effort re-bind of the original SDT/BC/built-in GeneXus
                            // data type binding, mirroring the forward bind path above. A plain
                            // scalar/domain original leaves originalTypeName null, so this block is a
                            // no-op and rollback behaves exactly as it did before this change.
                            if (!string.IsNullOrEmpty(originalTypeName))
                            {
                                try
                                {
                                    bool rebound = false;
                                    if (originalSnapshot.Type == global::Artech.Genexus.Common.eDBType.GX_SDT
                                        || originalSnapshot.Type == global::Artech.Genexus.Common.eDBType.GX_BUSCOMP)
                                    {
                                        var originalBoundObject = VariableInjector.ResolveTypeObject(varPart.Model, originalTypeName);
                                        if (originalBoundObject != null && originalBoundObject.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                                        {
                                            VariableInjector.BindVariableToSdt(restored, originalBoundObject);
                                            rebound = true;
                                        }
                                        else if (originalBoundObject is global::Artech.Genexus.Common.Objects.Transaction originalBoundTrn && originalBoundTrn.IsBusinessComponent)
                                        {
                                            VariableInjector.BindVariableToBC(restored, originalBoundObject);
                                            rebound = true;
                                        }
                                    }
                                    else
                                    {
                                        rebound = VariableInjector.TryBindGenexusDataType(restored, originalTypeName)
                                                  || VariableInjector.TryBindBuiltinUserDefinedType(restored, originalTypeName);
                                    }
                                    if (!rebound) bindingNotRestored = true;
                                }
                                catch { bindingNotRestored = true; }
                            }

                            varPart.Variables.Add(restored);
                        }
                    }
                    catch { /* swallow — rollback is best-effort */ }
                    // Task 4.5 — prefer a structured BoundToControls envelope
                    // when the SDK rejection message looks like a ghost-binding
                    // failure; falls back to the legacy raw error envelope
                    // when the message doesn't match the heuristic.
                    var boundResp = TryBuildBoundToControlsError(ex, obj, varName, existingVarId);
                    if (boundResp != null) return boundResp;
                    // issue #47 — don't claim a full restore when the original had a non-primitive
                    // binding (SDT / BC / built-in) that couldn't be re-bound; only the scalar
                    // shape was recovered. The common primitive/domain case keeps the plain hint.
                    string rollbackHint = "The modify+save failed; the original variable was restored. Check if the variable is bound to controls.";
                    if (bindingNotRestored)
                    {
                        rollbackHint += " The original had a non-primitive type; verify its binding with genexus_read part=Variables.";
                    }
                    return McpResponse.Err(
                        code: "ModifyVariableFailed",
                        message: ex.Message,
                        hint: rollbackHint,
                        nextSteps: new JArray(McpResponse.NextStep(
                            tool: "genexus_read",
                            args: new JObject { ["name"] = target, ["part"] = "Variables" },
                            why: "Verifies which variables exist after the rollback.")),
                        target: target);
                }
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "ModifyVariableFailed",
                    message: ex.Message,
                    hint: "Verify the variable name and type are valid.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = target, ["part"] = "Variables" },
                        why: "Lists current variables to confirm state.")),
                    target: target);
            }
        }

        // issue #47 — pure helper: given the "&Name : TypeRepr [Collection]" text
        // VariableInjector.GetVariablesAsText emits for a VariablesPart, extract the type token
        // for `varName`, or null when it's unresolvable/absent. ResolveTypeRepresentation's
        // fallback format ("<eDBType>(<len>[,<dec>])") means the read path couldn't name the
        // binding either, so that shape is treated the same as "not found" — the caller must not
        // guess a type name from it. Internal + no SDK types in its signature so it's unit-testable
        // without a live KB (see GxMcp.Worker.Tests via InternalsVisibleTo).
        internal static string ExtractOriginalTypeNameFromDump(string dumpedText, string varName)
        {
            if (string.IsNullOrEmpty(dumpedText) || string.IsNullOrEmpty(varName)) return null;
            foreach (var dumpedLine in dumpedText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    dumpedLine,
                    @"^&" + System.Text.RegularExpressions.Regex.Escape(varName) + @"\s*:\s*(.+?)(\s+Collection)?$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    string candidate = m.Groups[1].Value.Trim();
                    if (System.Text.RegularExpressions.Regex.IsMatch(candidate, @"^GX_\w+\(\d+(,\d+)?\)$"))
                        return null;
                    return candidate;
                }
            }
            return null;
        }
    }
}
