using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Parts;
using Artech.Architecture.Common.Services;
using GxMcp.Worker.Services;
using Artech.Genexus.Common.Objects;
using Artech.Common.Collections;
using Artech.Genexus.Common;

namespace GxMcp.Worker.Helpers
{
    public static class VariableInjector
    {
        private static readonly HashSet<string> StandardVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Pgmname", "Pgmdesc", "Today", "Time", "Mode", "Message", "EventName", "CtlName"
        };

        public static void InjectVariables(KBObject obj, string code, Models.SearchIndex index = null)
        {
            var variablesPart = obj.Parts.Get<VariablesPart>();
            if (variablesPart == null) return;

            // Scan for &-tokens on source with string literals and comments blanked out, so an
            // ampersand that is DATA rather than a variable — a URL query ("...&status=paid"),
            // an HTML entity ("&nbsp;"), a commented-out line — no longer auto-declares a spurious
            // VARCHAR(100) variable (issue #45). The literal text itself is untouched on save; only
            // the name-extraction view is masked.
            string scanCode = StripLiteralsAndComments(code);

            var matches = System.Text.RegularExpressions.Regex.Matches(scanCode, @"&(\w+)");
            var varNames = matches.Cast<System.Text.RegularExpressions.Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();

            // Detect &var.Field usage — these vars are likely SDTs/BCs, not scalars
            var sdtCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(scanCode, @"&(\w+)\."))
            {
                sdtCandidates.Add(m.Groups[1].Value);
            }

            foreach (var varName in varNames)
            {
                if (!variablesPart.Variables.Any(v => v.Name.Equals(varName, StringComparison.OrdinalIgnoreCase)))
                {
                    global::Artech.Genexus.Common.Variable v = CreateVariable(variablesPart, varName, index, sdtCandidates.Contains(varName));
                    if (v != null)
                    {
                        variablesPart.Variables.Add(v);
                        Logger.Info($"Injected variable: {varName} into {obj.Name}");
                    }
                }
            }
        }

        // Blank out string-literal contents and comments in GeneXus source, preserving length and
        // newlines, so only ampersands in CODE are seen by the auto-declare scanner. Handles GeneXus
        // doubled-quote escaping inside strings (""), both string delimiters, and // and /* */
        // comments. issue #45. Used only for &-token extraction — never for what gets persisted.
        internal static string StripLiteralsAndComments(string code)
        {
            if (string.IsNullOrEmpty(code)) return code ?? string.Empty;
            var sb = new System.Text.StringBuilder(code.Length);
            int i = 0, n = code.Length;
            while (i < n)
            {
                char c = code[i];
                if (c == '"' || c == '\'')
                {
                    char delim = c;
                    sb.Append(' '); i++;
                    while (i < n)
                    {
                        char d = code[i];
                        if (d == delim)
                        {
                            // Doubled delimiter inside a string is an escaped quote — stay in-string.
                            if (i + 1 < n && code[i + 1] == delim) { sb.Append("  "); i += 2; continue; }
                            sb.Append(' '); i++; break;
                        }
                        sb.Append(d == '\n' || d == '\r' ? d : ' ');
                        i++;
                    }
                    continue;
                }
                if (c == '/' && i + 1 < n && code[i + 1] == '/')
                {
                    while (i < n && code[i] != '\n' && code[i] != '\r') { sb.Append(' '); i++; }
                    continue;
                }
                if (c == '/' && i + 1 < n && code[i + 1] == '*')
                {
                    sb.Append("  "); i += 2;
                    while (i < n && !(code[i] == '*' && i + 1 < n && code[i + 1] == '/'))
                    {
                        sb.Append(code[i] == '\n' || code[i] == '\r' ? code[i] : ' ');
                        i++;
                    }
                    if (i < n) { sb.Append("  "); i += 2; }
                    continue;
                }
                sb.Append(c); i++;
            }
            return sb.ToString();
        }

        internal static global::Artech.Genexus.Common.Variable CreateVariable(VariablesPart part, string name, Models.SearchIndex index = null, bool sdtMemberAccessHint = false)
        {
            global::Artech.Genexus.Common.Variable v = new global::Artech.Genexus.Common.Variable(part);
            v.Name = name;

            // 0. SDT/BC heuristic: name starts with Sdt/SDT or used as &var.Field — try resolving as SDT first
            bool sdtNamePrefix = name.StartsWith("Sdt", StringComparison.OrdinalIgnoreCase) || name.StartsWith("SDT", StringComparison.Ordinal);
            if (sdtMemberAccessHint || sdtNamePrefix)
            {
                // Try direct match by the variable name
                var tryNames = new List<string> { name };
                // Strip Sdt/SDT prefix and try suffix-only too
                if (name.StartsWith("Sdt", StringComparison.OrdinalIgnoreCase) && name.Length > 3) tryNames.Add(name.Substring(3));

                foreach (var candidateName in tryNames)
                {
                    var sdtObj = ResolveTypeObject(part.Model, candidateName);
                    if (sdtObj != null && sdtObj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                    {
                        BindVariableToSdt(v, sdtObj);
                        Logger.Info($"Injected variable {name} bound to SDT {sdtObj.Name} (heuristic: prefix={sdtNamePrefix}, memberAccess={sdtMemberAccessHint})");
                        return v;
                    }
                    if (sdtObj is Transaction bc && bc.IsBusinessComponent)
                    {
                        BindVariableToBC(v, sdtObj);
                        Logger.Info($"Injected variable {name} bound to BC {sdtObj.Name}");
                        return v;
                    }
                }
                // Friction-report #3: when the source uses &Var.Field, the variable is structurally
                // an SDT/BC reference. Falling through to the VARCHAR(100) default poisons subsequent
                // validation with confusing "VARCHAR has no member 'Field'" errors. Skip the injection
                // entirely so the agent gets a single clear "variable not declared" signal and can
                // call genexus_add_variable with the correct SDT typeName.
                if (sdtMemberAccessHint)
                {
                    Logger.Warn($"Variable {name} used with .Field access but no matching SDT/BC found in KB. Skipping auto-inject (will surface as undeclared variable; declare via genexus_add_variable typeName=<SDT>).");
                    return null;
                }
            }

            // 1. Inherit from Attribute (FAST INDEX LOOKUP)
            if (index != null)
            {
                string key = "Attribute:" + name;
                if (index.Objects.TryGetValue(key, out var entry))
                {
                    if (TryParseDbType(entry.DataType, out var itype))
                    {
                        v.Type = itype;
                        v.Length = entry.Length;
                        v.Decimals = entry.Decimals;
                        Logger.Info($"Injected variable {name} inheriting from INDEXED attribute {name}");
                        return v;
                    }
                }
            }

            // Fallback (SDK lookup - only if not in index or index not provided)
            var attribute = FindAttribute(part.Model, name);
            if (attribute != null)
            {
                v.Type = attribute.Type;
                v.Length = attribute.Length;
                v.Decimals = attribute.Decimals;
                v.Signed = attribute.Signed;
                Logger.Info($"Injected variable {name} inheriting from SDK attribute {attribute.Name}");
                return v;
            }

            // 2. Naming Heuristics
            string lowerName = name.ToLower();

            // Boolean
            if (lowerName.StartsWith("is") || lowerName.StartsWith("has") || lowerName.StartsWith("flg") || 
                lowerName.Contains("ativo") || lowerName.Contains("pode") || lowerName.EndsWith("ok"))
            {
                v.Type = global::Artech.Genexus.Common.eDBType.Boolean;
                Logger.Info($"Injected Boolean variable: {name}");
                return v;
            }

            // Date / DateTime
            if (lowerName.EndsWith("data") || lowerName.EndsWith("dt") || lowerName.Contains("emissao") || lowerName.Contains("vencimento"))
            {
                v.Type = global::Artech.Genexus.Common.eDBType.DATE;
                Logger.Info($"Injected Date variable: {name}");
                return v;
            }
            if (lowerName.Contains("hora") || lowerName.Contains("timestamp") || lowerName.Contains("moment"))
            {
                v.Type = global::Artech.Genexus.Common.eDBType.DATETIME;
                Logger.Info($"Injected DateTime variable: {name}");
                return v;
            }

            // Numeric
            if (lowerName.EndsWith("id") || lowerName.EndsWith("seq") || lowerName.EndsWith("qtd") || 
                lowerName.Contains("valor") || lowerName.Contains("preco") || lowerName.Contains("total"))
            {
                v.Type = global::Artech.Genexus.Common.eDBType.NUMERIC;
                v.Length = 10;
                v.Decimals = lowerName.Contains("valor") || lowerName.Contains("preco") || lowerName.Contains("total") ? 2 : 0;
                Logger.Info($"Injected Numeric variable: {name} ({v.Length},{v.Decimals})");
                return v;
            }

            // 3. Fallback: VarChar(100)
            v.Type = global::Artech.Genexus.Common.eDBType.VARCHAR;
            v.Length = 100;
            Logger.Info($"Injected Default VarChar variable: {name}");

            return v;
        }

        // FR#1 + FR#13 (friction-report 2026-05-14) + FR#3 (2026-05-19): Layout XML uses
        // AttID="var:N" — the SDK's stable layout id is the C# instance property
        // Variable.Id. Accessed via reflection because the SDK assembly doesn't expose it
        // as a typed interface member, and GetPropertyValue("Id") returns null (the
        // Properties bag doesn't carry Id). Live-probe confirmed: TotalHorasCredito.Id=22
        // matches AttID="var:22" in ListaAtiCPAlunoUniGra layout XML, SaldoHoras.Id=33
        // matches "var:33", etc. System vars Today/Time/Pgmname/Pgmdesc get ids 1-4
        // (WWP creates them first), and deleted variables leave gaps in the ID sequence
        // — so enumeration-position fallback is wrong for any non-trivial object.
        public static int? GetVariableInternalId(global::Artech.Genexus.Common.Variable v, int fallbackIndex)
        {
            if (v == null) return null;

            // Primary: Variable.Id via C# reflection.
            try
            {
                var idProp = v.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                if (idProp != null && idProp.CanRead)
                {
                    object raw = idProp.GetValue(v);
                    if (raw is int i && i > 0) return i;
                    if (raw is short s && s > 0) return s;
                    if (raw is long l && l > 0 && l <= int.MaxValue) return (int)l;
                }
            }
            catch { }

            // Fallback for resilience across SDK versions: try the Properties bag with names
            // that might carry the id in builds where Variable.Id is renamed/missing.
            foreach (var prop in new[] { "InternalId", "VariableId", "VarId", "Index", "AttId", "Number" })
            {
                try
                {
                    object raw = v.GetPropertyValue(prop);
                    if (raw is int i && i > 0) return i;
                    if (raw is short s && s > 0) return s;
                    if (raw is long l && l > 0 && l <= int.MaxValue) return (int)l;
                    if (raw is string ss && int.TryParse(ss, out var parsed) && parsed > 0) return parsed;
                }
                catch { }
            }

            // Last resort. Almost always WRONG for objects touched by WorkWithPlus patterns
            // since those inject system vars early — kept only so behavior is defined.
            return fallbackIndex;
        }


        public static string GetVariablesAsText(KBObject obj)
        {
            var varPart = obj.Parts.Get<VariablesPart>();
            if (varPart == null) return string.Empty;
            return GetVariablesAsText(varPart);
        }

        public static string GetVariablesAsText(VariablesPart varPart)
        {
            var sb = new System.Text.StringBuilder();
            foreach (global::Artech.Genexus.Common.Variable v in varPart.Variables)
            {
                string typeRepr = ResolveTypeRepresentation(varPart.Model, v);
                string collectionSuffix = v.IsCollection ? " Collection" : "";
                sb.AppendLine(string.Format("&{0} : {1}{2}", v.Name, typeRepr, collectionSuffix));
            }
            return sb.ToString();
        }

        private static bool _loggedVarProps = false;

        private static string TryResolveBoundObjectName(global::Artech.Genexus.Common.Variable v, KBModel model)
        {
            if (model == null) return null;

            // One-time diagnostic dump to discover the actual property name carrying SDT/BC binding
            if (!_loggedVarProps && v.Type == global::Artech.Genexus.Common.eDBType.GX_SDT)
            {
                try
                {
                    var dump = new System.Text.StringBuilder();
                    var propsProp = v.GetType().GetProperty("Properties");
                    if (propsProp != null)
                    {
                        var coll = propsProp.GetValue(v) as System.Collections.IEnumerable;
                        if (coll != null)
                        {
                            foreach (object p in coll)
                            {
                                try
                                {
                                    string name = (string)p.GetType().GetProperty("Name")?.GetValue(p);
                                    object val = p.GetType().GetProperty("Value")?.GetValue(p);
                                    if (val != null) dump.Append(name + "=" + val.GetType().Name + "[" + val.ToString() + "]; ");
                                }
                                catch { }
                            }
                        }
                    }
                    Logger.Info("[VAR INNER PROPS] " + v.Name + ": " + dump.ToString());
                    _loggedVarProps = true;
                }
                catch { _loggedVarProps = true; }
            }

            // Fast path: GX18 stores the SDT/BC name directly in DataTypeString
            try
            {
                object dts = v.GetPropertyValue("DataTypeString");
                if (dts is string dtsStr && !string.IsNullOrEmpty(dtsStr)) return dtsStr;
            }
            catch { }

            // Friction-report #4: BindVariableToSdt stores the structural reference in ATTCUSTOMTYPE
            // (AttCustomType.Guid actually carries the SDT *name*, per the constructor comment).
            // When DataTypeString isn't persisted (older KBs or read-only setter), this is the
            // authoritative source — without it we serialize the bound SDT variable as "GX_SDT(4)".
            try
            {
                object custom = v.GetPropertyValue("ATTCUSTOMTYPE");
                if (custom != null)
                {
                    var ct = custom.GetType();
                    string guidVal = ct.GetProperty("Guid")?.GetValue(custom) as string
                                  ?? ct.GetField("Guid", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(custom) as string
                                  ?? ct.GetField("m_guid", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(custom) as string;
                    if (!string.IsNullOrEmpty(guidVal))
                    {
                        // First try as object name (the convention this codebase uses).
                        if (model != null)
                        {
                            try
                            {
                                foreach (var candidate in model.Objects.GetByName(null, null, guidVal))
                                {
                                    if (candidate != null) return candidate.Name;
                                }
                            }
                            catch { }
                            // Could also be a stringified guid — fall through to TryGetObjectFromKey.
                            var byKey = TryGetObjectFromKey(model, guidVal);
                            if (byKey != null) return byKey.Name;
                        }
                        // No model lookup possible — surface the raw token rather than GX_SDT(4).
                        return guidVal;
                    }
                }
            }
            catch { }

            // Fallback: try known key-bearing properties
            string[] candidateProps = { "DataType", "DataTypeKey", "ItemType", "BasedOn", "BasedOnKey", "TypeKey", "ObjectKey", "DataItemTypeName" };
            foreach (var prop in candidateProps)
            {
                object value = null;
                try { value = v.GetPropertyValue(prop); } catch { continue; }
                if (value == null) continue;

                KBObject obj = TryGetObjectFromKey(model, value);
                if (obj != null) return obj.Name;
            }

            // Fallback: enumerate all properties of the variable and look for any value
            // that resolves to an object in the model
            try
            {
                foreach (var prop in v.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (!prop.CanRead) continue;
                    object value;
                    try { value = prop.GetValue(v); } catch { continue; }
                    if (value == null) continue;
                    KBObject obj = TryGetObjectFromKey(model, value);
                    if (obj != null && (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase)
                                      || (obj is Transaction trn && trn.IsBusinessComponent)))
                        return obj.Name;
                }
            }
            catch { }

            return null;
        }

        private static KBObject TryGetObjectFromKey(KBModel model, object key)
        {
            try
            {
                if (key is global::Artech.Udm.Framework.EntityKey ek) return model.Objects.Get(ek);
                if (key is Guid g) return model.Objects.Get(g);
                if (key is string s && Guid.TryParse(s, out var gp)) return model.Objects.Get(gp);
            }
            catch { }
            return null;
        }

        private static string ResolveTypeRepresentation(KBModel model, global::Artech.Genexus.Common.Variable v)
        {
            // Domain binding wins over raw type
            try
            {
                if (v.DomainBasedOn != null) return v.DomainBasedOn.Name;
            }
            catch { }

            // SDT or BC binding via DataType property (stored as object Key)
            if (v.Type == global::Artech.Genexus.Common.eDBType.GX_SDT || v.Type == global::Artech.Genexus.Common.eDBType.GX_BUSCOMP)
            {
                string boundName = TryResolveBoundObjectName(v, model);
                if (!string.IsNullOrEmpty(boundName)) return boundName;
                // Fallback: emit raw enum + length so user sees something is up
                return string.Format("{0}({1}{2})", v.Type, v.Length, v.Decimals > 0 ? "," + v.Decimals : "");
            }

            // Built-in GeneXus data type (WebSession, HttpClient, ...): DataTypeString holds the type
            // name. Without this a WebSession variable serializes as "GX_USRDEFTYP(4)" and round-tripping
            // through an edit would drop the type (issue #33). GX_EXTERNAL_OBJECT is covered too so any
            // external-object-category built-in reads back by name rather than "GX_EXTERNAL_OBJECT(4)"
            // (issue #45).
            if (v.Type == global::Artech.Genexus.Common.eDBType.GX_USRDEFTYP
                || v.Type == global::Artech.Genexus.Common.eDBType.GX_EXTERNAL_OBJECT)
            {
                try
                {
                    if (v.GetPropertyValue("DataTypeString") is string dts && !string.IsNullOrEmpty(dts)) return dts;
                }
                catch { }
                return string.Format("{0}({1}{2})", v.Type, v.Length, v.Decimals > 0 ? "," + v.Decimals : "");
            }

            return string.Format("{0}({1}{2})", v.Type, v.Length, v.Decimals > 0 ? "," + v.Decimals : "");
        }

        public static void SetVariablesFromText(VariablesPart part, string text)
        {
            var lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var seenVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in lines)
            {
                // Format: &Name : Type(Length,Decimals) [Collection]
                var match = System.Text.RegularExpressions.Regex.Match(line, @"&?(\w+)\s*:\s*([\w\.\-]+)(?:\s*\(\s*(\d+)(?:\s*,\s*(\d+))?\s*\))?(?:\s+(Collection))?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    string name = match.Groups[1].Value;
                    string typeStr = match.Groups[2].Value;
                    int length = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
                    int decimals = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0;
                    bool isCollection = match.Groups[5].Success;

                    seenVars.Add(name);

                    var v = part.Variables.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (v == null)
                    {
                        v = new global::Artech.Genexus.Common.Variable(part);
                        v.Name = name;
                        part.Variables.Add(v);
                    }

                    // 1. Map string type to eDBType (including Aliases)
                    if (TryParseDbType(typeStr, out var dbType))
                    {
                        v.Type = dbType;
                        v.Length = length;
                        v.DomainBasedOn = null; 
                        v.SetPropertyValue("DataType", null); // Reset user type if it was set
                    }
                    else if (TryBindGenexusDataType(v, typeStr))
                    {
                        // Built-in GeneXus data types (HttpClient, WebSession, Location, ...) via the
                        // SDK type registry (issue #45). Without this the DSL silently left the var at
                        // its default NUMERIC(4) type for any non-primitive built-in.
                        Logger.Info($"Resolved variable {name} type to GeneXus data type {typeStr}");
                    }
                    else if (TryBindBuiltinUserDefinedType(v, typeStr))
                    {
                        // Legacy hardcoded WebSession fallback (issue #33).
                        Logger.Info($"Resolved variable {name} type to built-in user-defined type {typeStr}");
                    }
                    else
                    {
                        // 2. Resolve as Domain, SDT, or BC
                        var targetObj = ResolveTypeObject(part.Model, typeStr);
                        if (targetObj != null)
                        {
                            if (targetObj is global::Artech.Genexus.Common.Objects.Domain dom)
                                v.DomainBasedOn = dom;
                            else if (targetObj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                            {
                                BindVariableToSdt(v, targetObj);
                            }
                            else if (targetObj is global::Artech.Genexus.Common.Objects.Transaction trn && trn.IsBusinessComponent)
                            {
                                BindVariableToBC(v, targetObj);
                            }
                            Logger.Info($"Resolved variable {name} type to {targetObj.TypeDescriptor.Name}: {targetObj.Name}");
                        }
                    }

                    // Collection flag LAST: setting v.Type (above) resets IsCollection in the SDK,
                    // so assigning it before the type silently produced a non-collection variable
                    // (its .Count failed as "unknown function"). Only the typed add path — which
                    // sets collection after the type — yielded a real collection until now (issue #45).
                    try { v.IsCollection = isCollection; } catch { /* not all types are collectible */ }
                }
            }

            // Remove variables not in the text, except standard variables
            var toRemove = part.Variables
                .Where(v => !seenVars.Contains(v.Name) && !StandardVariables.Contains(v.Name))
                .ToList();

            foreach (var v in toRemove)
            {
                part.Variables.Remove(v);
                Logger.Info($"Removed variable {v.Name} (no longer in text)");
            }
        }

        public static void BindVariableToSdt(global::Artech.Genexus.Common.Variable v, KBObject sdtObj)
        {
            Logger.Info($"[BindVariableToSdt] Binding {v.Name} -> SDT {sdtObj.Name} (Guid={sdtObj.Guid})");
            v.Type = global::Artech.Genexus.Common.eDBType.GX_SDT;
            v.SetPropertyValue("DataType", sdtObj.Key);
            try { v.SetPropertyValue("DataTypeString", sdtObj.Name); } catch (Exception ex) { Logger.Warn("DataTypeString set failed: " + ex.Message); }

            // GeneXus stores the actual structural type reference in ATTCUSTOMTYPE. For an SDT the
            // custom type carries the object NAME as its guid string with dataType 254 (the SDT
            // category); the SDK resolves that to the StructureTypeReference at save time. The
            // "Reference X by name can't be saved" error surfaces if a real guid string is used here.
            var inst = BuildAttCustomType(sdtObj.GetType().Assembly, sdtObj.Name, 254, null);
            if (inst != null)
            {
                try { v.SetPropertyValue("ATTCUSTOMTYPE", inst); }
                catch (Exception ex) { Logger.Error("[BindVariableToSdt] SetPropertyValue ATTCUSTOMTYPE failed: " + ex.Message); }
            }
            else Logger.Error("[BindVariableToSdt] Could not construct AttCustomType for " + sdtObj.Name);
        }

        // Built-in GeneXus "user-defined" effective types that live in eDBType.GX_USRDEFTYP,
        // keyed by their AttCustomType subtype id (category 255). Confirmed live for issue #33: a
        // WebSession variable persists as AttCustomType{ dataType=255, guid="31", description=
        // "WebSession" } (ToString "255:31"). The subtype id is a GeneXus built-in constant per GX
        // version; add other externally-backed types (e.g. HttpRequest) here as they're verified.
        private static readonly Dictionary<string, int> BuiltinUserDefinedTypes =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "WebSession", 31 },
            };

        public static bool IsBuiltinUserDefinedType(string typeName)
            => !string.IsNullOrWhiteSpace(typeName) && BuiltinUserDefinedTypes.ContainsKey(typeName.Trim());

        // Returns the canonical name (correct casing) when typeName is a known built-in
        // user-defined type, else null.
        public static string CanonicalUserDefinedTypeName(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            foreach (var key in BuiltinUserDefinedTypes.Keys)
                if (key.Equals(typeName.Trim(), StringComparison.OrdinalIgnoreCase)) return key;
            return null;
        }

        // Atomically type a variable as a built-in user-defined type (e.g. WebSession) so that
        // &var.Get/.Set validate. Returns false when typeName isn't a known built-in.
        public static bool TryBindBuiltinUserDefinedType(global::Artech.Genexus.Common.Variable v, string typeName)
        {
            var canonical = CanonicalUserDefinedTypeName(typeName);
            if (canonical == null) return false;
            BindVariableToExternalObject(v, canonical, BuiltinUserDefinedTypes[canonical]);
            return true;
        }

        // issue #45 — bind a variable to ANY built-in GeneXus data type (HttpClient, HttpRequest,
        // HttpResponse, WebSession, Location, MailMessage, ExcelDocument, …) by resolving the name
        // through the SDK's own type registry, exactly as the IDE's variable "Type" picker does.
        // DataTypeProvider.GetTypeByName returns the AttCustomType the SDK wants persisted — carrying
        // the correct effective-type category (Variable.Type) and the runtime subtype guid — so we
        // don't hardcode a per-type id table (the WebSession=31 map only covered one of ~137 types).
        // Returns false when the name isn't a known GeneXus data type, so callers still surface
        // UnknownType. Setting DataTypeString mirrors the name for read-back round-tripping.
        public static bool TryBindGenexusDataType(global::Artech.Genexus.Common.Variable v, string typeName)
        {
            if (v == null || string.IsNullOrWhiteSpace(typeName)) return false;
            try
            {
                var model = v.Model;
                if (model == null) return false;
                var provider = Artech.Genexus.Common.Types.DataTypeProvider.GetProvider(model);
                if (provider == null) return false;
                var att = provider.GetTypeByName(typeName.Trim(), model);
                if (att == null) return false;
                v.Type = (global::Artech.Genexus.Common.eDBType)att.DataType;
                try { v.SetPropertyValue("ATTCUSTOMTYPE", att); }
                catch (Exception ex) { Logger.Warn("[TryBindGenexusDataType] SetPropertyValue ATTCUSTOMTYPE failed: " + ex.Message); return false; }
                try { v.SetPropertyValue("DataTypeString", typeName.Trim()); } catch { /* read-back mirror; best-effort */ }
                Logger.Info($"[TryBindGenexusDataType] Bound {v.Name} -> {typeName} (category {att.DataType}, guid {att.Guid})");
                return true;
            }
            catch (Exception ex) { Logger.Warn("[TryBindGenexusDataType] " + ex.Message); return false; }
        }

        // GX_USRDEFTYP = user-defined effective type (AttCustomType category 255). The concrete
        // type (WebSession, ...) is identified by ATTCUSTOMTYPE where guid=<subtype id> and
        // description=<type name>; DataTypeString mirrors the name for read-back.
        public static void BindVariableToExternalObject(global::Artech.Genexus.Common.Variable v, string typeName, int subtype)
        {
            Logger.Info($"[BindVariableToExternalObject] Binding {v.Name} -> {typeName} (255:{subtype})");
            v.Type = global::Artech.Genexus.Common.eDBType.GX_USRDEFTYP;
            try { v.SetPropertyValue("DataTypeString", typeName); } catch (Exception ex) { Logger.Warn("[ExtObj] DataTypeString set failed: " + ex.Message); }
            var inst = BuildAttCustomType(v.GetType().Assembly, subtype.ToString(), 255, typeName);
            if (inst != null)
            {
                try { v.SetPropertyValue("ATTCUSTOMTYPE", inst); }
                catch (Exception ex) { Logger.Error("[ExtObj] SetPropertyValue ATTCUSTOMTYPE failed: " + ex.Message); }
            }
            else Logger.Error("[ExtObj] Could not construct AttCustomType for " + typeName);
        }

        // Type an SDT structure member as a reference to another SDT (item.Type=GX_SDT +
        // ATTCUSTOMTYPE carrying the SDT name / category 254). Mirrors BindVariableToSdt but on an
        // SDTItem reached via reflection (the parser holds it as a dynamic). Returns false on failure.
        public static bool BindSdtItemToSdt(object item, KBObject sdtObj)
        {
            if (item == null || sdtObj == null) return false;
            try
            {
                var itemType = item.GetType();
                var eDBTypeT = itemType.Assembly.GetType("Artech.Genexus.Common.eDBType");
                var setType = itemType.GetProperty("Type");
                if (eDBTypeT != null && setType != null && setType.CanWrite)
                    setType.SetValue(item, Enum.Parse(eDBTypeT, "GX_SDT"), null);

                var inst = BuildAttCustomType(itemType.Assembly, sdtObj.Name, 254, null);
                if (inst == null) { Logger.Error("[BindSdtItemToSdt] Could not construct AttCustomType for " + sdtObj.Name); return false; }

                var setProp = itemType.GetMethod("SetPropertyValue", new[] { typeof(string), typeof(object) });
                if (setProp == null) { Logger.Error("[BindSdtItemToSdt] SetPropertyValue(string,object) not found on " + itemType.FullName); return false; }
                setProp.Invoke(item, new object[] { "ATTCUSTOMTYPE", inst });
                Logger.Info($"[BindSdtItemToSdt] Bound structure member -> SDT {sdtObj.Name}");
                return true;
            }
            catch (Exception ex) { Logger.Error("[BindSdtItemToSdt] failed: " + (ex.InnerException?.Message ?? ex.Message)); return false; }
        }

        // Construct an Artech AttCustomType via reflection (the class isn't statically referenced).
        // Ctors seen on GX18: (), (string guid, int dataType), (string,int,string), (string,int,string,string).
        // dataType is the type category (254=SDT, 255=user-defined). Returns null on failure.
        public static object BuildAttCustomType(System.Reflection.Assembly probeAsm, string guid, int dataType, string description)
        {
            try
            {
                Type customTypeT = null;
                foreach (var loadedAsm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        foreach (var t in loadedAsm.GetTypes())
                            if (t.Name.Equals("AttCustomType", StringComparison.Ordinal)) { customTypeT = t; break; }
                    }
                    catch { }
                    if (customTypeT != null) break;
                }
                if (customTypeT == null) { Logger.Warn("[BuildAttCustomType] AttCustomType type not found"); return null; }

                object inst = null;
                if (!string.IsNullOrEmpty(description))
                {
                    var ctor3 = customTypeT.GetConstructor(new[] { typeof(string), typeof(int), typeof(string) });
                    if (ctor3 != null) { try { inst = ctor3.Invoke(new object[] { guid, dataType, description }); } catch (Exception ex) { Logger.Warn("[BuildAttCustomType] ctor(str,int,str) failed: " + ex.Message); } }
                }
                if (inst == null)
                {
                    var ctor2 = customTypeT.GetConstructor(new[] { typeof(string), typeof(int) });
                    if (ctor2 != null) { try { inst = ctor2.Invoke(new object[] { guid, dataType }); } catch (Exception ex) { Logger.Warn("[BuildAttCustomType] ctor(str,int) failed: " + ex.Message); } }
                }
                if (inst == null)
                {
                    var ctor0 = customTypeT.GetConstructor(Type.EmptyTypes);
                    if (ctor0 != null)
                    {
                        try
                        {
                            inst = ctor0.Invoke(null);
                            customTypeT.GetProperty("Guid")?.SetValue(inst, guid);
                            customTypeT.GetProperty("DataType")?.SetValue(inst, dataType);
                        }
                        catch (Exception ex) { Logger.Warn("[BuildAttCustomType] ctor() failed: " + ex.Message); }
                    }
                }
                // Ensure the description lands even when only a shorter ctor was available.
                if (inst != null && !string.IsNullOrEmpty(description))
                {
                    try
                    {
                        var descField = customTypeT.GetField("m_description", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (descField != null && string.IsNullOrEmpty(descField.GetValue(inst) as string))
                            descField.SetValue(inst, description);
                    }
                    catch { }
                }
                if (inst == null) Logger.Error("[BuildAttCustomType] Could not construct AttCustomType (guid=" + guid + ", dataType=" + dataType + ")");
                return inst;
            }
            catch (Exception ex) { Logger.Warn("[BuildAttCustomType] failed: " + ex.Message); return null; }
        }

        public static void BindVariableToBC(global::Artech.Genexus.Common.Variable v, KBObject bcObj)
        {
            v.Type = global::Artech.Genexus.Common.eDBType.GX_BUSCOMP;
            v.SetPropertyValue("DataType", bcObj.Key);
            try { v.SetPropertyValue("DataTypeString", bcObj.Name); } catch { }
        }

        public static bool TryParseDbType(string typeStr, out global::Artech.Genexus.Common.eDBType type)
        {
            // Type Aliases mapping. NOTE: 'Character' is NOT mapped to VARCHAR — it must
            // round-trip to eDBType.CHARACTER via the case-insensitive Enum.TryParse below.
            // Aliasing CHARACTER → VARCHAR makes Variables-DSL patch verification fail because
            // GetVariablesAsText emits the original eDBType.CHARACTER string while
            // SetVariablesFromText would persist VARCHAR (friction-report #5 write side).
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "VarChar", "VARCHAR" },
                { "Numeric", "NUMERIC" },
                { "Boolean", "Boolean" },
                { "Date", "DATE" },
                { "DateTime", "DATETIME" },
                // issue #34: eDBType has no BLOB/IMAGE member — a blob is BINARY, an image
                // is BITMAP. The old "BLOB"/"IMAGE" aliases failed Enum.TryParse and the
                // variable silently kept its default NUMERIC(4) type.
                { "Blob", "BINARY" },
                { "Binary", "BINARY" },
                { "Image", "BITMAP" },
                { "Bitmap", "BITMAP" },
                { "Audio", "AUDIO" },
                { "Video", "VIDEO" },
                { "GUID", "GUID" },
                { "Geography", "GEOGRAPHY" }
            };

            if (aliases.TryGetValue(typeStr, out var mappedType))
            {
                typeStr = mappedType;
            }

            return Enum.TryParse<global::Artech.Genexus.Common.eDBType>(typeStr, true, out type);
        }

        public static KBObject ResolveTypeObject(KBModel model, string typeName)
        {
            try
            {
                foreach (var obj in model.Objects.GetByName(null, null, typeName))
                {
                    // Check for Domain
                    if (obj is global::Artech.Genexus.Common.Objects.Domain) return obj;

                    // Check for SDT
                    if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase)) return obj;

                    // Check for Transaction (as BC)
                    if (obj is Transaction trn && trn.IsBusinessComponent) return obj;
                }

                // issue #43 #7 — nested SDT member/item type, e.g. "SdtCandUNIEDU.SdtCandUNIEDUItem".
                // This dotted form is exactly what the SDK emits on READ for a variable bound to a
                // Collection SDT as a single item (VariableTypeResolver already flags "SdtFoo.Item" as
                // expected here). The whole dotted string is not a KB object name — only the parent SDT
                // is — so split off the parent and resolve it. BindVariableToSdt(newVar, parentSdt) then
                // produces the "<Sdt>.<Sdt>Item" item type, since the SDK derives the ".Item" suffix
                // from the SDT's own collection-ness (IsCollection left false), matching the read form.
                if (typeName != null && typeName.IndexOf('.') > 0)
                {
                    string parentName = typeName.Substring(0, typeName.IndexOf('.'));
                    foreach (var obj in model.Objects.GetByName(null, null, parentName))
                    {
                        if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase)) return obj;
                    }
                }
            }
            catch { /* Ignore model errors */ }
            return null;
        }

        private static global::Artech.Genexus.Common.Objects.Attribute FindAttribute(global::Artech.Architecture.Common.Objects.KBModel model, string name)
        {
            try
            {
                foreach (var result in model.Objects.GetByName(null, null, name))
                {
                    if (result is global::Artech.Genexus.Common.Objects.Attribute attr) return attr;
                }
            }
            catch { /* Object not found or model access error */ }
            return null;
        }
    }
}
