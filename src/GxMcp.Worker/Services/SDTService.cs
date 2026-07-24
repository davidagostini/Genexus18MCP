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
