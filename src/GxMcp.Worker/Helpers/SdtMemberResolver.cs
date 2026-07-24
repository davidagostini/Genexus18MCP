using System;
using System.Reflection;
using System.Text.RegularExpressions;
using Artech.Architecture.Common.Objects;

namespace GxMcp.Worker.Helpers
{
    // issue #47: a structure-read leaf whose SDK type is GX_SDT (a member typed as a reference
    // to another SDT) — or a user-defined type — carries the referenced object only in its
    // ItemEntity.CustomType, so the plain eDBType read surfaces the raw "GX_SDT" enum instead of
    // the SDT name the IDE shows. This mirrors the persisted-form resolution SdtDslParser already
    // does on the write/DSL side (issue #33), extracted so the JSON read paths (genexus_inspect,
    // genexus_structure get_visual) can reuse it without duplicating the reflection.
    public static class SdtMemberResolver
    {
        // Returns the referenced SDT/type object name for a reference-typed SDT member, or null
        // when the member is primitive or the reference can't be resolved (caller keeps the raw type).
        public static string ResolveReferencedTypeName(object item, KBModel model)
        {
            if (item == null || model == null) return null;
            try
            {
                var itemType = item.GetType();
                object ie = itemType.GetProperty("ItemEntity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(item)
                         ?? itemType.GetProperty("SDTItemEntity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(item);
                if (ie == null) return null;

                object ct = ie.GetType().GetProperty("CustomType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(ie);
                if (ct == null) return null;

                string guid = ct.GetType().GetField("m_guid", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(ct) as string;
                if (string.IsNullOrEmpty(guid)) return null;

                // Persisted StructureTypeReference is an EntityKey pair: <Type> = the referenced
                // object's class guid, <Id> = its numeric id. Resolve via model.Objects.Get(EntityKey).
                var mRef = Regex.Match(guid, @"<Type>([0-9a-fA-F\-]{36})</Type>\s*<Id>(\d+)</Id>");
                if (mRef.Success && Guid.TryParse(mRef.Groups[1].Value, out var classGuid)
                    && int.TryParse(mRef.Groups[2].Value, out var objId))
                {
                    try
                    {
                        var ek = new global::Artech.Udm.Framework.EntityKey(classGuid, objId);
                        var o = model.Objects.Get(ek);
                        if (o != null) return o.Name;
                    }
                    catch { }
                }
                // Pre-save form: a bare type token — resolve via the shared type resolver.
                if (guid.IndexOf('<') < 0)
                {
                    try
                    {
                        var o = VariableInjector.ResolveTypeObject(model, guid.Trim());
                        if (o != null) return o.Name;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        // A GeneXus eDBType token that denotes a reference to another object (SDT or user-defined
        // type / domain) rather than a primitive. Only these warrant a CustomType lookup.
        public static bool IsReferenceType(string typeStr)
        {
            if (string.IsNullOrEmpty(typeStr)) return false;
            return typeStr.StartsWith("GX_SDT", StringComparison.OrdinalIgnoreCase)
                || typeStr.StartsWith("GX_BUSCOMP", StringComparison.OrdinalIgnoreCase)
                || typeStr.StartsWith("GX_USRDEFTYP", StringComparison.OrdinalIgnoreCase);
        }
    }
}
