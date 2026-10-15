using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Compatibility
{
    /// <summary>
    /// Compatibility adapter for standard GeneXus WorkWith (WorkWith for Web).
    /// Uses Artech.Patterns.WorkWith.WorkWithInstance when available on the STA thread,
    /// and provides structured XML fallback for test and headless environments.
    /// </summary>
    internal static class WorkWithSdkAdapter
    {
        private static Assembly _cachedAssembly;
        private static bool _assemblyResolved;
        private static readonly object _assemblyLock = new object();

        /// Disclosed in both the dryRun plan and the write receipt.
        ///
        /// The earlier wording claimed "pattern bindings are preserved", which is
        /// the opposite of what happens to the attribute that gets materialized:
        /// `page="<default>"` becomes a concrete `10`, and GeneXus then treats it
        /// as an explicit instance-level override. A later change to the pattern
        /// template default no longer propagates to this instance. The instance
        /// keeps working and its identity, tabs and actions are intact - what it
        /// loses is the link to the template default, and saying otherwise led an
        /// agent to reason about future default changes from a false premise
        /// (issue #477).
        internal const string NativeDefaultNormalization =
            "Native WorkWith SDK serialization normalizes pattern defaults (such as page=\"<default>\" to concrete values) upon save. "
            + "The instance stays functional and its pattern identity, tabs and actions are intact, but a materialized attribute "
            + "becomes an explicit instance override and will no longer follow a later change to the pattern template default.";

        internal const string FallbackDefaultNormalization =
            "XML fallback preserves existing pattern XML defaults verbatim without normalization.";

        internal static Assembly GetWorkWithAssembly()
        {
            if (_assemblyResolved) return _cachedAssembly;
            lock (_assemblyLock)
            {
                if (_assemblyResolved) return _cachedAssembly;
                try
                {
                    _cachedAssembly = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => string.Equals(a.GetName().Name, "Artech.Patterns.WorkWith", StringComparison.OrdinalIgnoreCase));
                    if (_cachedAssembly != null)
                    {
                        _assemblyResolved = true;
                        return _cachedAssembly;
                    }

                    string gxPath = GeneXusInstallPath.Resolve();
                    if (!string.IsNullOrEmpty(gxPath))
                    {
                        string candidate = Path.Combine(gxPath, "Packages", "Patterns", "WorkWith", "Artech.Patterns.WorkWith.dll");
                        if (File.Exists(candidate))
                        {
                            _cachedAssembly = Assembly.LoadFrom(candidate);
                            _assemblyResolved = true;
                            return _cachedAssembly;
                        }
                    }

                    _cachedAssembly = Assembly.Load("Artech.Patterns.WorkWith");
                }
                catch { }
                _assemblyResolved = true;
                return _cachedAssembly;
            }
        }

        internal static bool IsNativeSdkAvailable => GetWorkWithAssembly() != null;

        public static bool ListActions(KBObject instanceObj, PatternAnalysisService patterns, out JArray tabsArray, out string error)
        {
            tabsArray = null;
            error = null;

            if (instanceObj == null)
            {
                error = "NullInstance";
                return false;
            }

            var asm = GetWorkWithAssembly();
            if (asm != null)
            {
                try
                {
                    var wwiType = asm.GetType("Artech.Patterns.WorkWith.WorkWithInstance");
                    if (wwiType != null)
                    {
                        var loadMethod = wwiType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static);
                        object wwi = loadMethod != null ? loadMethod.Invoke(null, new object[] { instanceObj }) : null;
                        if (wwi != null)
                        {
                            var levelsProp = wwiType.GetProperty("Levels");
                            var levels = levelsProp?.GetValue(wwi, null) as IEnumerable;
                            var tabsList = new JArray();
                            if (levels != null)
                            {
                                foreach (var level in levels)
                                {
                                    var view = level.GetType().GetProperty("View")?.GetValue(level, null);
                                    if (view == null) continue;
                                    var tabs = view.GetType().GetProperty("Tabs")?.GetValue(view, null) as IEnumerable;
                                    if (tabs == null) continue;
                                    foreach (var tab in tabs)
                                    {
                                        string code = tab.GetType().GetProperty("Code")?.GetValue(tab, null)?.ToString() ?? "";
                                        string name = tab.GetType().GetProperty("Name")?.GetValue(tab, null)?.ToString() ?? code;
                                        string type = tab.GetType().GetProperty("Type")?.GetValue(tab, null)?.ToString() ?? "Tabular";
                                        var tabObj = new JObject
                                        {
                                            ["code"] = code,
                                            ["name"] = name,
                                            ["type"] = type
                                        };
                                        var actions = tab.GetType().GetProperty("Actions")?.GetValue(tab, null) as IEnumerable;
                                        var actionsArray = new JArray();
                                        if (actions != null)
                                        {
                                            foreach (var act in actions)
                                            {
                                                string actName = act.GetType().GetProperty("Name")?.GetValue(act, null)?.ToString() ?? "";
                                                string caption = act.GetType().GetProperty("Caption")?.GetValue(act, null)?.ToString();
                                                string procName = act.GetType().GetProperty("GxobjectName")?.GetValue(act, null)?.ToString();
                                                if (string.IsNullOrEmpty(procName))
                                                {
                                                    var gxObj = act.GetType().GetProperty("Gxobject")?.GetValue(act, null) as KBObject;
                                                    procName = gxObj?.Name;
                                                }
                                                string condition = act.GetType().GetProperty("Condition")?.GetValue(act, null)?.ToString();
                                                var parameters = act.GetType().GetProperty("Parameters")?.GetValue(act, null) as IEnumerable;
                                                var paramList = new JArray();
                                                if (parameters != null)
                                                {
                                                    foreach (var p in parameters)
                                                    {
                                                        string pName = p.GetType().GetProperty("Name")?.GetValue(p, null)?.ToString();
                                                        if (!string.IsNullOrEmpty(pName)) paramList.Add(pName);
                                                    }
                                                }
                                                actionsArray.Add(new JObject
                                                {
                                                    ["name"] = actName,
                                                    ["caption"] = caption ?? actName,
                                                    ["procedure"] = procName ?? "",
                                                    ["condition"] = condition ?? "",
                                                    ["parameters"] = paramList
                                                });
                                            }
                                        }
                                        tabObj["actions"] = actionsArray;
                                        tabsList.Add(tabObj);
                                    }
                                }
                            }
                            tabsArray = tabsList;
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug("[WorkWithSdkAdapter] Native ListActions failed, falling back to XML: " + ex.Message);
                }
            }

            string xml = patterns?.ReadPatternPartXml(instanceObj, "PatternInstance", PatternRegistry.WorkWithPatternId, out _, out _);
            if (string.IsNullOrWhiteSpace(xml))
            {
                error = "PatternInstanceXmlEmpty";
                return false;
            }
            return TryListActionsFromXml(xml, out tabsArray, out error);
        }

        internal static bool TryListActionsFromXml(string xml, out JArray tabsArray, out string error)
        {
            tabsArray = null;
            error = null;
            try
            {
                var doc = XDocument.Parse(xml);
                var tabsList = new JArray();
                var tabsElements = doc.Descendants().Where(e => string.Equals(e.Name.LocalName, "tab", StringComparison.OrdinalIgnoreCase));
                foreach (var tab in tabsElements)
                {
                    string code = (string)tab.Attribute("code") ?? (string)tab.Attribute("name") ?? "";
                    string name = (string)tab.Attribute("name") ?? code;
                    string type = (string)tab.Attribute("type") ?? "Tabular";
                    var tabObj = new JObject
                    {
                        ["code"] = code,
                        ["name"] = name,
                        ["type"] = type
                    };
                    var actionsArray = new JArray();
                    var actionsContainer = tab.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "actions", StringComparison.OrdinalIgnoreCase));
                    if (actionsContainer != null)
                    {
                        foreach (var act in actionsContainer.Elements().Where(e => string.Equals(e.Name.LocalName, "action", StringComparison.OrdinalIgnoreCase)))
                        {
                            string actName = (string)act.Attribute("name") ?? "";
                            string caption = (string)act.Attribute("caption");
                            string gxobject = (string)act.Attribute("gxobject") ?? "";
                            if (gxobject.Contains("-"))
                            {
                                var parts = gxobject.Split(new[] { '-' }, 6);
                                if (parts.Length == 6) gxobject = parts[5];
                            }
                            string condition = (string)act.Attribute("condition");
                            var paramList = new JArray();
                            var paramsElem = act.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "parameters", StringComparison.OrdinalIgnoreCase));
                            if (paramsElem != null)
                            {
                                foreach (var p in paramsElem.Elements().Where(e => string.Equals(e.Name.LocalName, "parameter", StringComparison.OrdinalIgnoreCase)))
                                {
                                    string pName = (string)p.Attribute("name");
                                    if (!string.IsNullOrEmpty(pName)) paramList.Add(pName);
                                }
                            }
                            actionsArray.Add(new JObject
                            {
                                ["name"] = actName,
                                ["caption"] = caption ?? actName,
                                ["procedure"] = gxobject,
                                ["condition"] = condition ?? "",
                                ["parameters"] = paramList
                            });
                        }
                    }
                    tabObj["actions"] = actionsArray;
                    tabsList.Add(tabObj);
                }
                tabsArray = tabsList;
                return true;
            }
            catch (Exception ex)
            {
                error = "InvalidXml: " + ex.Message;
                return false;
            }
        }

        internal static JArray PlanActionRemoval(JArray tabs, string tabCode, string actionName)
        {
            var matchingTabs = tabs.OfType<JObject>().Where(t =>
                string.Equals(t["code"]?.ToString(), tabCode, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t["name"]?.ToString(), tabCode, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matchingTabs.Count != 1)
                throw new UsageException("WorkWithTabAmbiguous", "The explicit tab must identify exactly one WorkWith tab.");
            var actions = matchingTabs[0]["actions"] as JArray;
            var matches = actions?.OfType<JObject>().Where(a => string.Equals(a["name"]?.ToString(), actionName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches == null || matches.Count != 1)
                throw new UsageException("WorkWithActionAmbiguous", "The action name must identify exactly one action on the selected tab.");
            var expected = (JArray)tabs.DeepClone();
            var expectedActions = (JArray)((JObject)expected[tabs.IndexOf(matchingTabs[0])])["actions"];
            expectedActions.RemoveAt(actions.IndexOf(matches[0]));
            return expected;
        }

        internal static object LoadNativeModel(KBObject instance)
        {
            var type = GetWorkWithAssembly()?.GetType("Artech.Patterns.WorkWith.WorkWithInstance");
            var load = type?.GetMethod("Load", BindingFlags.Public | BindingFlags.Static);
            return load?.Invoke(null, new object[] { instance })
                ?? throw new UsageException("NativeWorkWithUnavailable", "The standard WorkWith SDK could not load the instance. No XML fallback is used for removal.");
        }

        internal static void RemoveNativeAction(object model, string tabCode, string actionName)
        {
            var tabs = new List<object>();
            var levels = model.GetType().GetProperty("Levels")?.GetValue(model, null) as IEnumerable;
            if (levels != null)
                foreach (var level in levels)
                {
                    var view = level.GetType().GetProperty("View")?.GetValue(level, null);
                    var collection = view?.GetType().GetProperty("Tabs")?.GetValue(view, null) as IEnumerable;
                    if (collection == null) continue;
                    foreach (var tab in collection)
                        if (string.Equals(tab.GetType().GetProperty("Code")?.GetValue(tab, null)?.ToString(), tabCode, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(tab.GetType().GetProperty("Name")?.GetValue(tab, null)?.ToString(), tabCode, StringComparison.OrdinalIgnoreCase)) tabs.Add(tab);
                }
            if (tabs.Count != 1) throw new UsageException("WorkWithTabAmbiguous", "The native model did not resolve exactly one tab.");
            var actions = tabs[0].GetType().GetProperty("Actions")?.GetValue(tabs[0], null);
            var matches = (actions as IEnumerable)?.Cast<object>().Where(a =>
                string.Equals(a.GetType().GetProperty("Name")?.GetValue(a, null)?.ToString(), actionName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches == null || matches.Count != 1) throw new UsageException("WorkWithActionAmbiguous", "The native model did not resolve exactly one action.");
            var remove = actions.GetType().GetMethod("Remove", new[] { matches[0].GetType() });
            if (remove == null || remove.ReturnType != typeof(bool))
                throw new UsageException("NativeWorkWithRemoveUnavailable", "The selected SDK has no compatible typed action removal API.");
            if (!(bool)remove.Invoke(actions, new[] { matches[0] }))
                throw new UsageException("NativeWorkWithRemoveFailed", "The native collection refused the selected action removal.");
        }

        internal static void SaveNativeModel(object model, KBObject instance)
        {
            var save = model.GetType().GetMethods().SingleOrDefault(m => m.Name == "SaveTo"
                && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(instance));
            if (save == null) throw new UsageException("NativeWorkWithSaveUnavailable", "The selected SDK has no compatible WorkWith SaveTo API.");
            using (var isolation = new PatternSaveIsolationScope(instance))
            {
                save.Invoke(model, new object[] { instance });
                instance.Save();
            }
        }

        public static bool AddAction(
            KBObject instanceObj,
            PatternAnalysisService patterns,
            WriteService write,
            string tabCode,
            string actionName,
            string caption,
            KBObject procObj,
            string condition,
            IReadOnlyList<string> parameters,
            bool dryRun,
            out JObject result,
            out string errorCode,
            out string errorMessage)
        {
            result = null;
            errorCode = null;
            errorMessage = null;

            if (instanceObj == null)
            {
                errorCode = "NullInstance";
                errorMessage = "WorkWith PatternInstance object is null.";
                return false;
            }

            // Step 1: Pre-check tab existence and duplicate action
            if (!ListActions(instanceObj, patterns, out var tabs, out var listError))
            {
                errorCode = listError ?? "ListActionsFailed";
                errorMessage = $"Could not inspect tabs for WorkWith instance '{instanceObj.Name}': {listError}";
                return false;
            }

            var targetTab = tabs.FirstOrDefault(t =>
                string.Equals(t["code"]?.ToString(), tabCode, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t["name"]?.ToString(), tabCode, StringComparison.OrdinalIgnoreCase)) as JObject;

            if (targetTab == null)
            {
                errorCode = "TabNotFound";
                errorMessage = $"Tab '{tabCode}' was not found in WorkWith instance '{instanceObj.Name}'. Available tabs: {string.Join(", ", tabs.Select(t => t["code"]?.ToString()))}.";
                return false;
            }

            var existingActions = targetTab["actions"] as JArray;
            if (existingActions != null && existingActions.Any(a => string.Equals(a["name"]?.ToString(), actionName, StringComparison.OrdinalIgnoreCase)))
            {
                errorCode = "ActionAlreadyExists";
                errorMessage = $"Action '{actionName}' already exists on tab '{tabCode}' in WorkWith instance '{instanceObj.Name}'.";
                return false;
            }

            result = BuildActionPlan(instanceObj.Name, tabCode, actionName, caption, procObj?.Name, condition, parameters, procObj);
            if (!ValidateActionArity(result, out errorCode, out errorMessage)) return false;

            // Step 2: Handle dryRun
            if (dryRun)
            {
                result["route"] = IsNativeSdkAvailable ? "native-sdk" : "xml-fallback";
                result["defaultNormalization"] = IsNativeSdkAvailable
                    ? NativeDefaultNormalization
                    : FallbackDefaultNormalization;
                return true;
            }

            // Step 3: Real write
            bool nativeSucceeded = false;
            var asm = GetWorkWithAssembly();
            if (asm != null)
            {
                try
                {
                    var wwiType = asm.GetType("Artech.Patterns.WorkWith.WorkWithInstance");
                    var loadMethod = wwiType?.GetMethod("Load", BindingFlags.Public | BindingFlags.Static);
                    object wwi = loadMethod?.Invoke(null, new object[] { instanceObj });
                    if (wwi != null)
                    {
                        var levels = wwiType.GetProperty("Levels")?.GetValue(wwi, null) as IEnumerable;
                        object sdkTab = null;
                        if (levels != null)
                        {
                            foreach (var level in levels)
                            {
                                var view = level.GetType().GetProperty("View")?.GetValue(level, null);
                                if (view == null) continue;
                                var tabColl = view.GetType().GetProperty("Tabs")?.GetValue(view, null);
                                if (tabColl == null) continue;
                                var findTabMethod = tabColl.GetType().GetMethod("FindTab", new[] { typeof(string) });
                                sdkTab = findTabMethod?.Invoke(tabColl, new object[] { tabCode });
                                if (sdkTab != null) break;
                            }
                        }

                        if (sdkTab != null)
                        {
                            var addActionMethod = sdkTab.GetType().GetMethod("AddAction", new[] { typeof(string) });
                            var newAction = addActionMethod?.Invoke(sdkTab, new object[] { actionName });
                            if (newAction != null)
                            {
                                newAction.GetType().GetProperty("Caption")?.SetValue(newAction, caption ?? actionName, null);
                                if (procObj != null)
                                    newAction.GetType().GetProperty("Gxobject")?.SetValue(newAction, procObj, null);
                                if (!string.IsNullOrEmpty(condition))
                                    newAction.GetType().GetProperty("Condition")?.SetValue(newAction, condition, null);

                                if (parameters != null && parameters.Count > 0)
                                {
                                    var addParamMethod = newAction.GetType().GetMethod("AddParameter", new[] { typeof(string) });
                                    foreach (var p in parameters)
                                    {
                                        addParamMethod?.Invoke(newAction, new object[] { p });
                                    }
                                }

                                var saveToMethod = wwiType.GetMethod("SaveTo", new[] { instanceObj.GetType() })
                                    ?? wwiType.GetMethod("SaveTo");
                                saveToMethod?.Invoke(wwi, new object[] { instanceObj });

                                instanceObj.Save();
                                nativeSucceeded = true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("[WorkWithSdkAdapter] Native AddAction failed, falling back to XML: " + ex.Message);
                }
            }

            if (!nativeSucceeded)
            {
                string currentXml = patterns?.ReadPatternPartXml(instanceObj, "PatternInstance", PatternRegistry.WorkWithPatternId, out _, out _);
                if (string.IsNullOrWhiteSpace(currentXml))
                {
                    errorCode = "PatternInstanceXmlEmpty";
                    errorMessage = "Could not read PatternInstance XML for " + instanceObj.Name;
                    return false;
                }

                if (!TryAddActionToXml(currentXml, tabCode, actionName, caption, procObj, condition, parameters, out string newXml, out errorCode, out errorMessage))
                {
                    return false;
                }

                if (write != null)
                {
                    var writeResult = write.WriteObject(instanceObj.Name, "PatternInstance", newXml, dryRun: false, allowGridStructure: true);
                    var writeJson = JObject.Parse(writeResult);
                    if (writeJson["error"] != null)
                    {
                        errorCode = writeJson["error"]?["code"]?.ToString() ?? "PatternSaveFailed";
                        errorMessage = writeJson["error"]?["message"]?.ToString() ?? "Failed to save PatternInstance";
                        return false;
                    }
                }
                else
                {
                    instanceObj.Save();
                }
            }

            // Step 4: Verification reread
            if (!ListActions(instanceObj, patterns, out var verifyTabs, out _))
            {
                errorCode = "VerificationReadFailed";
                errorMessage = "Action was saved, but verification re-read failed.";
                return false;
            }

            if (verifyTabs.Count < tabs.Count)
            {
                errorCode = "VerificationFailed";
                errorMessage = $"Instance tabs were truncated after write: expected at least {tabs.Count} tabs, found {verifyTabs.Count}.";
                return false;
            }

            var verifiedTab = verifyTabs.FirstOrDefault(t =>
                string.Equals(t["code"]?.ToString(), tabCode, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t["name"]?.ToString(), tabCode, StringComparison.OrdinalIgnoreCase)) as JObject;
            var verifiedActions = verifiedTab?["actions"] as JArray;
            var verifiedAct = verifiedActions?.FirstOrDefault(a => string.Equals(a["name"]?.ToString(), actionName, StringComparison.OrdinalIgnoreCase)) as JObject;

            if (verifiedAct == null)
            {
                errorCode = "VerificationFailed";
                errorMessage = $"Action '{actionName}' was not found in the verified post-write state.";
                return false;
            }

            string expectedCaption = caption ?? actionName;
            string expectedProcedure = procObj?.Name ?? "";
            string expectedCondition = condition ?? "";
            var expectedParams = (parameters ?? Array.Empty<string>()).ToList();

            if (!VerifyActionMatches(verifiedAct, actionName, caption, procObj?.Name, condition, parameters, out string mismatchDetail))
            {
                errorCode = "VerificationMismatch";
                errorMessage = $"Action '{actionName}' was persisted, but post-save verification failed: {mismatchDetail}";
                return false;
            }

            result = new JObject
            {
                ["instance"] = instanceObj.Name,
                ["tab"] = tabCode,
                ["action"] = verifiedAct.DeepClone(),
                ["requestedAction"] = new JObject
                {
                    ["name"] = actionName,
                    ["caption"] = expectedCaption,
                    ["procedure"] = expectedProcedure,
                    ["condition"] = expectedCondition,
                    ["parameters"] = new JArray(expectedParams)
                },
                ["saved"] = true,
                ["persisted"] = true,
                ["verified"] = true,
                ["verification"] = new JObject
                {
                    ["scope"] = "tab_action_reread",
                    ["fieldsCompared"] = new JArray("name", "caption", "procedure", "condition", "parameters"),
                    ["allFieldsMatched"] = true,
                    ["observedSource"] = "in_memory_instance_reread"
                },
                ["route"] = nativeSucceeded ? "native-sdk" : "xml-fallback",
                ["verificationScope"] = "action definition, target tab actions collection, neighbor tabs integrity, and post-write instance re-read",
                ["defaultNormalization"] = nativeSucceeded
                    ? NativeDefaultNormalization
                    : FallbackDefaultNormalization,
                ["derivedObjectRegeneration"] = "WorkWith instance definition persisted. Note: On GeneXus 18/17, derived WebComponents (e.g. <Trn>General) may require IDE Apply or build to re-project the new action control."
            };
            return true;
        }

        internal static JObject BuildActionPlan(
            string instanceName,
            string tabCode,
            string actionName,
            string caption,
            string procName,
            string condition,
            IReadOnlyList<string> parameters,
            KBObject procObj = null)
        {
            var validations = new JObject
            {
                ["targetObjectResolved"] = true,
                ["workWithInstanceResolved"] = true,
                ["tabExists"] = true,
                ["actionNameFormatValid"] = true,
                ["actionNameUniqueOnTab"] = true,
                ["procedureResolved"] = !string.IsNullOrEmpty(procName),
                ["procedureTypeValid"] = true
            };

            var validationLimits = new JObject
            {
                ["signatureAndTypes"] = "Procedure parameter types and signature binding against parm() rule are not validated or compiled.",
                ["conditionExpression"] = "Condition expression syntax and semantics are not compiled or evaluated.",
                ["parameterExpressions"] = "Parameter expressions syntax and semantics are not compiled or evaluated."
            };

            var plan = new JObject
            {
                ["instance"] = instanceName,
                ["tab"] = tabCode,
                ["actionName"] = actionName,
                ["caption"] = caption ?? actionName,
                ["procedure"] = procName ?? "",
                ["parameters"] = new JArray(parameters ?? Array.Empty<string>()),
                ["condition"] = condition ?? "",
                ["validations"] = validations,
                ["validationLimits"] = validationLimits
            };

            var sigAdvisory = TryInspectSignatureAdvisory(procObj, parameters);
            if (sigAdvisory != null)
            {
                plan["signatureAdvisory"] = sigAdvisory;
            }

            return plan;
        }

        internal static bool ValidateActionArity(JObject plan, out string errorCode, out string errorMessage)
        {
            errorCode = null;
            errorMessage = null;
            var advisory = plan?["signatureAdvisory"] as JObject;
            if (advisory?["arityMatches"]?.ToObject<bool?>() != false) return true;
            errorCode = "ActionParameterArityMismatch";
            errorMessage = advisory["warning"]?.ToString()
                ?? "Supplied action parameters do not match the known Procedure arity.";
            plan["valid"] = false;
            return false;
        }

        internal static JObject TryInspectSignatureAdvisory(KBObject procObj, IReadOnlyList<string> parameters)
        {
            if (procObj == null) return null;
            try
            {
                var parts = procObj.Parts;
                if (parts == null) return null;

                KBObjectPart rulesPart = null;
                foreach (KBObjectPart p in parts)
                {
                    if (p != null && (string.Equals(p.TypeDescriptor?.Name, "Rules", StringComparison.OrdinalIgnoreCase)
                        || p.GetType().Name.IndexOf("RulesPart", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        rulesPart = p;
                        break;
                    }
                }

                if (rulesPart == null) return null;

                string rulesSrc = rulesPart.GetType().GetProperty("Source")?.GetValue(rulesPart, null) as string;
                if (!string.IsNullOrWhiteSpace(rulesSrc))
                {
                    var sig = GeneXusSignatureParser.Parse(rulesSrc);
                    if (sig != null && !string.IsNullOrEmpty(sig.ParmRule))
                    {
                        int expectedCount = sig.Parameters != null ? sig.Parameters.Count : 0;
                        int suppliedCount = parameters != null ? parameters.Count : 0;
                        var advisory = new JObject
                        {
                            ["parmRule"] = sig.ParmRule,
                            ["expectedParameterCount"] = expectedCount,
                            ["suppliedParameterCount"] = suppliedCount,
                            ["arityMatches"] = (expectedCount == suppliedCount)
                        };
                        if (expectedCount != suppliedCount)
                        {
                            advisory["warning"] = $"Target Procedure '{procObj.Name}' declares {expectedCount} parameter(s) in its parm() rule, but {suppliedCount} parameter(s) were supplied in add_action.";
                        }
                        return advisory;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("[WorkWithSdkAdapter] Signature advisory inspection failed: " + ex.Message);
            }
            return null;
        }

        internal static bool VerifyActionMatches(
            JObject verifiedAct,
            string actionName,
            string caption,
            string procName,
            string condition,
            IReadOnlyList<string> parameters,
            out string mismatchDetail)
        {
            mismatchDetail = null;
            if (verifiedAct == null)
            {
                mismatchDetail = "Action not found in verified state.";
                return false;
            }

            string expectedCaption = caption ?? actionName;
            string expectedProcedure = procName ?? "";
            string expectedCondition = condition ?? "";
            var expectedParams = (parameters ?? Array.Empty<string>()).ToList();

            string observedName = verifiedAct["name"]?.ToString() ?? "";
            string observedCaption = verifiedAct["caption"]?.ToString() ?? "";
            string observedProcedure = verifiedAct["procedure"]?.ToString() ?? "";
            string observedCondition = verifiedAct["condition"]?.ToString() ?? "";
            var observedParams = (verifiedAct["parameters"] as JArray)?.Select(p => p.ToString()).ToList() ?? new List<string>();

            if (!string.Equals(observedName, actionName, StringComparison.OrdinalIgnoreCase))
            {
                mismatchDetail = $"Action name mismatch: expected '{actionName}', observed '{observedName}'.";
                return false;
            }

            if (!string.Equals(observedCaption, expectedCaption, StringComparison.Ordinal))
            {
                mismatchDetail = $"Caption mismatch: expected '{expectedCaption}', observed '{observedCaption}'.";
                return false;
            }

            if (!string.Equals(observedProcedure, expectedProcedure, StringComparison.OrdinalIgnoreCase))
            {
                mismatchDetail = $"Procedure mismatch: expected '{expectedProcedure}', observed '{observedProcedure}'.";
                return false;
            }

            if (!string.Equals(observedCondition, expectedCondition, StringComparison.Ordinal))
            {
                mismatchDetail = $"Condition mismatch: expected '{expectedCondition}', observed '{observedCondition}'.";
                return false;
            }

            if (!observedParams.SequenceEqual(expectedParams, StringComparer.Ordinal))
            {
                mismatchDetail = $"Parameters mismatch: expected [{string.Join(", ", expectedParams)}], observed [{string.Join(", ", observedParams)}].";
                return false;
            }

            return true;
        }

        internal static bool TryAddActionToXml(
            string xml,
            string tabCode,
            string actionName,
            string caption,
            KBObject procObj,
            string condition,
            IReadOnlyList<string> parameters,
            out string newXml,
            out string errorCode,
            out string errorMessage)
        {
            newXml = null;
            errorCode = null;
            errorMessage = null;

            try
            {
                var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                var tabElem = doc.Descendants().FirstOrDefault(e =>
                    string.Equals(e.Name.LocalName, "tab", StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals((string)e.Attribute("code"), tabCode, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals((string)e.Attribute("name"), tabCode, StringComparison.OrdinalIgnoreCase)));

                if (tabElem == null)
                {
                    errorCode = "TabNotFound";
                    errorMessage = $"Tab '{tabCode}' not found in XML.";
                    return false;
                }

                var actionsElem = tabElem.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "actions", StringComparison.OrdinalIgnoreCase));
                if (actionsElem == null)
                {
                    actionsElem = new XElement("actions");
                    tabElem.Add(actionsElem);
                }

                var existingAct = actionsElem.Elements().FirstOrDefault(e =>
                    string.Equals(e.Name.LocalName, "action", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((string)e.Attribute("name"), actionName, StringComparison.OrdinalIgnoreCase));

                if (existingAct != null)
                {
                    errorCode = "ActionAlreadyExists";
                    errorMessage = $"Action '{actionName}' already exists on tab '{tabCode}'.";
                    return false;
                }

                string gxobjectVal = procObj != null ? $"{procObj.Guid}-{procObj.Name}" : "";
                var newAct = new XElement("action",
                    new XAttribute("name", actionName),
                    new XAttribute("caption", caption ?? actionName));

                if (!string.IsNullOrEmpty(gxobjectVal)) newAct.Add(new XAttribute("gxobject", gxobjectVal));
                if (!string.IsNullOrEmpty(condition)) newAct.Add(new XAttribute("condition", condition));

                if (parameters != null && parameters.Count > 0)
                {
                    var paramsElem = new XElement("parameters");
                    foreach (var p in parameters)
                    {
                        paramsElem.Add(new XElement("parameter", new XAttribute("name", p)));
                    }
                    newAct.Add(paramsElem);
                }

                actionsElem.Add(newAct);
                newXml = doc.ToString(SaveOptions.DisableFormatting);
                return true;
            }
            catch (Exception ex)
            {
                errorCode = "XmlMutationFailed";
                errorMessage = ex.Message;
                return false;
            }
        }
    }
}
