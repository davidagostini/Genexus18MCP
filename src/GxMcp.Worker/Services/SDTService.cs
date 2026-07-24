using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class SDTService
    {
        private readonly ObjectService _objectService;

        public SDTService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        public string GetSDTStructure(string sdtName)
        {
            try
            {
                var obj = _objectService.FindObject(sdtName);
                if (obj == null) return HealingService.FormatNotFoundError(sdtName, _objectService.GetKbService().GetIndexCache().GetIndex());

                if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                {
                    dynamic sdt = obj;
                    var result = new JObject();
                    result["name"] = sdt.Name;
                    result["type"] = "SDT";

                    var children = new JArray();
                    dynamic structure = FindStructurePart(sdt);
                    dynamic root = null;
                    try { root = structure?.Root; } catch { }

                    // issue #47: the top-level "Collection" flag lives on the structure ROOT level,
                    // not on the SDT KBObject (sdt.IsCollection reads false there), which made a
                    // collection SDT report isCollection=false + a flat structure. Read the root
                    // flag first and surface the collection item name the IDE shows.
                    bool rootIsCollection = false;
                    try { rootIsCollection = (bool)root.IsCollection; } catch { }
                    if (!rootIsCollection) { try { rootIsCollection = (bool)sdt.IsCollection; } catch { } }
                    result["isCollection"] = rootIsCollection;
                    if (rootIsCollection)
                    {
                        string itemName = null;
                        try { itemName = (string)root.CollectionItemName; } catch { }
                        if (string.IsNullOrEmpty(itemName)) { try { itemName = (string)sdt.CollectionItemName; } catch { } }
                        if (!string.IsNullOrEmpty(itemName)) result["collectionItemName"] = itemName;
                    }

                    Artech.Architecture.Common.Objects.KBModel model = null;
                    try { model = obj.Model; } catch { }

                    if (root != null)
                    {
                        foreach (dynamic child in root.Items)
                        {
                            children.Add(MapLevelToResult(child, model));
                        }
                    }
                    result["children"] = children;
                    return result.ToString();
                }

                return "{\"status\":\"Error\",\"message\": \"Object is not an SDT\"}";
            }
            catch (Exception ex)
            {
                Logger.Error("SDTService Error: " + ex.Message);
                return "{\"status\":\"Error\",\"message\": \"" + ex.Message + "\"}";
            }
        }

        // issue #52: author an SDT's structure through genexus_structure action=update_visual.
        // The textual structure DSL cannot express the root Collection flag / item name or a
        // Domain-based member, so update_visual on an SDT routes here with a structured payload:
        //   { isCollection?, collectionItemName?, children:[
        //       { name, type?, length?, decimals?, isCollection? },     // primitive member
        //       { name, basedOnDomain:"<Domain>" },                     // Domain-typed member
        //       { name, type:"<OtherSdt>", isCollection? },             // SDT-reference member
        //       { name, isLevel:true, children:[ ... ] }                // nested level
        //   ] }
        // Members not present in children are removed (declarative sync, matching the Transaction path).
        public string UpdateSDTStructure(string sdtName, string payload)
        {
            try
            {
                var obj = _objectService.FindObject(sdtName);
                if (obj == null) return HealingService.FormatNotFoundError(sdtName, _objectService.GetKbService().GetIndexCache().GetIndex());
                if (!obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                    return Models.McpResponse.Err(code: "NotAnSDT", message: "Object is not an SDT.", target: sdtName);

                JObject json;
                try { json = JObject.Parse(payload); }
                catch (Exception ex) { return Models.McpResponse.Err(code: "InvalidStructurePayload", message: "payload is not valid JSON: " + ex.Message, target: sdtName); }

                var children = json["children"] as JArray;
                if (children == null)
                    return Models.McpResponse.Err(
                        code: "InvalidStructurePayload",
                        message: "payload must contain a 'children' array.",
                        hint: "e.g. {\"isCollection\":true,\"collectionItemName\":\"FooItem\",\"children\":[{\"name\":\"Bar\",\"type\":\"VarChar\",\"length\":100},{\"name\":\"Kind\",\"basedOnDomain\":\"MyDomain\"}]}",
                        target: sdtName);

                dynamic structure = FindStructurePart((dynamic)obj);
                dynamic root = null;
                try { root = structure?.Root; } catch { }
                if (structure == null || root == null)
                    return Models.McpResponse.Err(code: "StructureUpdateFailed", message: "SDT structure part/root not found.", target: sdtName);

                Artech.Architecture.Common.Objects.KBModel model = null;
                try { model = obj.Model; } catch { }

                using (var sdkTrans = obj.Model.KB.BeginTransaction())
                {
                    try
                    {
                        if (json["isCollection"] != null) { try { root.IsCollection = json["isCollection"].ToObject<bool>(); } catch { } }
                        string cin = json["collectionItemName"]?.ToString();
                        if (!string.IsNullOrEmpty(cin)) { try { root.CollectionItemName = cin; } catch { } }

                        int applied = SyncSdtJsonNodes(root, children, model);

                        GxMcp.Worker.Parsers.SdtDslParser.MarkPartDirty((object)structure, sdtName);
                        obj.Save();
                        sdkTrans.Commit();

                        _objectService.GetKbService().GetIndexCache().UpdateEntry(obj);

                        bool isColl = false; try { isColl = (bool)root.IsCollection; } catch { }
                        return Models.McpResponse.Ok(target: sdtName, code: "StructureUpdated",
                            result: new JObject { ["membersApplied"] = applied, ["isCollection"] = isColl });
                    }
                    catch (Exception ex)
                    {
                        try { sdkTrans.Rollback(); } catch { }
                        return Models.McpResponse.Err(
                            code: "StructureUpdateFailed",
                            message: ex.InnerException?.Message ?? ex.Message,
                            hint: "Check the payload children for malformed items or an unresolved basedOnDomain/type name.",
                            target: sdtName);
                    }
                }
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(code: "StructureUpdateFailed", message: ex.Message, target: sdtName);
            }
        }

        // Declaratively sync an SDT structure node's children from a JSON array. Adds/keeps members
        // named in the payload, removes the rest, recurses into nested levels. Handles primitive,
        // Domain-based (basedOnDomain) and SDT-reference (type names an SDT) members.
        private int SyncSdtJsonNodes(dynamic node, JArray children, Artech.Architecture.Common.Objects.KBModel model)
        {
            int applied = 0;
            dynamic items;
            try { items = node.Items; } catch { return 0; }

            var wanted = new System.Collections.Generic.HashSet<string>(
                children.Select(c => c["name"]?.ToString() ?? string.Empty), StringComparer.OrdinalIgnoreCase);
            var existing = new System.Collections.Generic.Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
            var toRemove = new System.Collections.Generic.List<dynamic>();
            foreach (dynamic c in items)
            {
                string cn = (string)c.Name;
                existing[cn] = c;
                if (!wanted.Contains(cn)) toRemove.Add(c);
            }
            foreach (dynamic d in toRemove) { try { items.Remove(d); } catch { } }

            Type nodeType = ((object)node).GetType();
            Type eDBTypeT = nodeType.Assembly.GetType("Artech.Genexus.Common.eDBType");
            var addItem = eDBTypeT != null ? nodeType.GetMethod("AddItem", new[] { typeof(string), eDBTypeT }) : null;
            var addLevel = nodeType.GetMethod("AddLevel", new[] { typeof(string) });

            foreach (var tok in children)
            {
                var child = tok as JObject;
                if (child == null) continue;
                string name = child["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;

                bool isLevel = child["isLevel"]?.ToObject<bool>() ?? (child["children"] is JArray);
                bool isColl = child["isCollection"]?.ToObject<bool>() ?? false;
                string basedOnDomain = child["basedOnDomain"]?.ToString();
                string typeStr = child["type"]?.ToString();

                dynamic target = existing.TryGetValue(name, out var found) ? found : null;

                if (isLevel)
                {
                    if (target == null)
                    {
                        if (addLevel == null) continue;
                        try { target = addLevel.Invoke((object)node, new object[] { name }); }
                        catch (Exception ex) { Logger.Error("[SDT WRITE] AddLevel('" + name + "') failed: " + (ex.InnerException?.Message ?? ex.Message)); continue; }
                    }
                    if (target == null) continue;
                    try { target.IsCollection = isColl; } catch { }
                    var grand = child["children"] as JArray ?? new JArray();
                    applied += 1 + SyncSdtJsonNodes(target, grand, model);
                    continue;
                }

                // Leaf: resolve a Domain (basedOnDomain) or an SDT reference (type names an SDT).
                KBObject domainObj = null, sdtObj = null;
                if (!string.IsNullOrEmpty(basedOnDomain) && model != null)
                {
                    domainObj = GxMcp.Worker.Helpers.VariableInjector.ResolveTypeObject(model, basedOnDomain);
                    if (!(domainObj is Artech.Genexus.Common.Objects.Domain)) domainObj = null;
                    if (domainObj == null)
                        throw new Exception("basedOnDomain '" + basedOnDomain + "' did not resolve to a Domain.");
                }
                else if (!string.IsNullOrEmpty(typeStr) && model != null && !LooksLikePrimitiveType(typeStr))
                {
                    var r = GxMcp.Worker.Helpers.VariableInjector.ResolveTypeObject(model, typeStr);
                    if (r != null && r.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase)) sdtObj = r;
                }

                if (target == null)
                {
                    if (addItem == null || eDBTypeT == null) continue;
                    object baseType;
                    if (domainObj != null) { try { baseType = ((dynamic)domainObj).DataType; } catch { baseType = Enum.Parse(eDBTypeT, "VARCHAR"); } }
                    else if (sdtObj != null) baseType = Enum.Parse(eDBTypeT, "GX_SDT");
                    else if (GxMcp.Worker.Helpers.VariableInjector.TryParseDbType(typeStr, out var pt)) baseType = pt;
                    else baseType = Enum.Parse(eDBTypeT, "VARCHAR");
                    try { target = addItem.Invoke((object)node, new object[] { name, baseType }); }
                    catch (Exception ex) { Logger.Error("[SDT WRITE] AddItem('" + name + "') failed: " + (ex.InnerException?.Message ?? ex.Message)); continue; }
                }
                if (target == null) continue;

                try { target.IsCollection = isColl; } catch { }
                if (domainObj != null)
                {
                    GxMcp.Worker.Helpers.DomainPropertyApplier.ApplyDomainBasedOn((object)target, domainObj);
                }
                else if (sdtObj != null)
                {
                    GxMcp.Worker.Helpers.VariableInjector.BindSdtItemToSdt((object)target, sdtObj);
                }
                else
                {
                    if (child["length"] != null) { try { SetIntProperty((object)target, "Length", child["length"].ToObject<int>()); } catch { } }
                    if (child["decimals"] != null) { try { SetIntProperty((object)target, "Decimals", child["decimals"].ToObject<int>()); } catch { } }
                }
                applied++;
            }
            return applied;
        }

        private static void SetIntProperty(object target, string propName, int value)
        {
            try
            {
                var p = GxMcp.Worker.Helpers.AttributeTypeApplier.GetPropertyUnambiguous(target.GetType(), propName);
                if (p != null && p.CanWrite) p.SetValue(target, value, null);
            }
            catch (Exception ex) { Logger.Debug("[SDT WRITE] SetIntProperty " + propName + " failed: " + ex.Message); }
        }

        internal static bool LooksLikePrimitiveType(string typeStr)
        {
            if (string.IsNullOrWhiteSpace(typeStr)) return true;
            string[] prims = { "Numeric", "Char", "VarChar", "Varchar", "LongVarchar", "Date", "DateTime",
                               "Bool", "Boolean", "Blob", "Binary", "Image", "Bitmap", "Audio", "Video",
                               "GUID", "Geography" };
            foreach (var p in prims)
                if (typeStr.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Finds the SDT Structure Part using multiple strategies.
        /// In GX18 SDK, the part has TypeDescriptor.Name="SDTStructure" and class SDTStructurePart.
        /// </summary>
        private dynamic FindStructurePart(dynamic sdt)
        {
            // Strategy 1: Iterate parts matching by TypeDescriptor name or class name
            foreach (dynamic part in sdt.Parts)
            {
                try {
                    string descName = part.TypeDescriptor?.Name ?? "";
                    string className = part.GetType().Name;
                    if (descName.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        descName.Equals("Structure", StringComparison.OrdinalIgnoreCase) ||
                        className.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0)
                    { return part; }
                } catch { }
            }
            
            // Strategy 2: Parts.Get with known GUID
            try {
                var part = sdt.Parts.Get(Guid.Parse("8597371d-1941-4c12-9c17-48df9911e2f3"));
                if (part != null) return part;
            } catch { }
            
            // Strategy 3: Duck typing - any part with Root.Items
            foreach (dynamic part in sdt.Parts)
            {
                try {
                    if (part.Root != null && part.Root.Items != null) return part;
                } catch { }
            }
            
            return null;
        }

        private JObject MapLevelToResult(dynamic level, Artech.Architecture.Common.Objects.KBModel model = null)
        {
            var res = new JObject();
            try { res["name"] = (string)level.Name; } catch { res["name"] = "?"; }

            bool isLeaf = true;
            try { isLeaf = level.IsLeafItem; } catch { }

            try { res["isCollection"] = (bool)level.IsCollection; } catch { res["isCollection"] = false; }

            if (!isLeaf)
            {
                res["isLevel"] = true;
                var children = new JArray();
                try {
                    foreach (dynamic child in level.Items)
                    {
                        children.Add(MapLevelToResult(child, model));
                    }
                } catch { }
                res["children"] = children;
                res["type"] = "Compound";
            }
            else
            {
                res["isLevel"] = false;
                string typeStr = "Unknown";
                try { typeStr = level.Type.ToString(); } catch { }
                res["type"] = typeStr;
                // issue #51: a member based on a Domain read back only as its base primitive type,
                // hiding the Domain link. Surface the Domain name so a domain-typed member is
                // visible (and round-trips through update_visual's basedOnDomain).
                try
                {
                    var dbo = level.DomainBasedOn;
                    if (dbo != null)
                    {
                        string domName = (string)dbo.Name;
                        if (!string.IsNullOrEmpty(domName)) res["basedOnDomain"] = domName;
                    }
                }
                catch { }
                // issue #47: surface the referenced SDT/type name for reference-typed members
                // instead of the raw "GX_SDT" enum (parity with the Structure DSL read).
                if (model != null && GxMcp.Worker.Helpers.SdtMemberResolver.IsReferenceType(typeStr))
                {
                    string refName = GxMcp.Worker.Helpers.SdtMemberResolver.ResolveReferencedTypeName((object)level, model);
                    if (!string.IsNullOrEmpty(refName)) res["referencedType"] = refName;
                }
                // issue #47: surface Length/Decimals (parity with genexus_inspect and the
                // Structure DSL). Without them a get_visual read dropped element size, e.g. a
                // Numeric(9,2) came back as bare "NUMERIC".
                try { object len = level.Length; if (len != null) res["length"] = Convert.ToInt32(len); } catch { }
                try { object dec = level.Decimals; if (dec != null) res["decimals"] = Convert.ToInt32(dec); } catch { }
            }
            return res;
        }
    }
}
