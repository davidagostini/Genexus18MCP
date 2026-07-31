using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services.Structure
{
    // issue #54: SubType Group membership. A Group is a KBObject whose GroupStructurePart holds
    // a BaseCollection<GroupMember>; each member binds a subtype Attribute to its supertype
    // (GroupMember.Supertype setter writes Subtype.SuperType — one call registers the member
    // AND asserts the attribute's subtype link, exactly like the IDE's Group editor).
    public class GroupStructureService
    {
        private readonly ObjectService _objectService;

        public GroupStructureService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        // payload = { members?: [{ name, subtypeOf }], remove?: [subtypeName] }
        // members upsert by subtype name; the subtype's SuperType is (re)asserted.
        public string UpdateGroupStructure(string groupName, string payload)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(payload)) return Models.McpResponse.Err(
                    code: "InvalidPayload", message: "payload is required.",
                    hint: "e.g. { \"members\": [ { \"name\": \"orgao_exercicio_id\", \"subtypeOf\": \"exercicio_id\" } ] }.",
                    target: groupName);

                var obj = _objectService.FindObject(groupName);
                if (obj == null) return HealingService.FormatNotFoundError(groupName, _objectService.GetKbService().GetIndexCache().GetIndex());

                if (!(obj is Group group)) return Models.McpResponse.Err(
                    code: "NotAGroup",
                    message: $"Object '{groupName}' is not a Group.",
                    hint: "Create one with genexus_create type=Group first.",
                    target: groupName);

                var json = JObject.Parse(payload);
                var members = json["members"] as JArray;
                var remove = json["remove"] as JArray;
                if ((members == null || members.Count == 0) && (remove == null || remove.Count == 0))
                    return Models.McpResponse.Err(
                        code: "NoMembersToApply",
                        message: "payload contained no members to add or remove.",
                        hint: "Pass members:[{name,subtypeOf}] to upsert, remove:[\"name\"] to detach.",
                        target: groupName);

                var part = group.Parts.Get<GroupStructurePart>();
                if (part == null) return Models.McpResponse.Err(
                    code: "GroupStructurePartMissing",
                    message: $"Group '{groupName}' has no GroupStructure part.",
                    target: groupName);

                var applied = new JArray();
                var removed = new JArray();

                using (var sdkTrans = group.Model.KB.BeginTransaction())
                {
                    try
                    {
                        if (members != null)
                        {
                            foreach (var m in members)
                            {
                                string name = m["name"]?.ToString();
                                string superName = m["subtypeOf"]?.ToString();
                                if (string.IsNullOrWhiteSpace(name)) { sdkTrans.Rollback(); return Models.McpResponse.Err(
                                    code: "MemberNameRequired", message: "Each member needs a 'name' (the subtype attribute).", target: groupName); }
                                if (string.IsNullOrWhiteSpace(superName)) { sdkTrans.Rollback(); return Models.McpResponse.Err(
                                    code: "MemberSubtypeOfRequired", message: $"Member '{name}' needs 'subtypeOf' (the supertype attribute).", target: groupName); }
                                if (name.Equals(superName, StringComparison.OrdinalIgnoreCase)) { sdkTrans.Rollback(); return Models.McpResponse.Err(
                                    code: "CyclicSubtype", message: $"Member '{name}' cannot be its own supertype.", target: groupName); }

                                var subtype = Artech.Genexus.Common.Objects.Attribute.Get(group.Model, name);
                                if (subtype == null) { sdkTrans.Rollback(); return Models.McpResponse.Err(
                                    code: "MemberAttributeNotFound",
                                    message: $"Subtype attribute '{name}' does not exist in the KB.",
                                    hint: "Create it inside a Transaction structure first (genexus_edit part=Structure).",
                                    target: groupName); }
                                var super = Artech.Genexus.Common.Objects.Attribute.Get(group.Model, superName);
                                if (super == null) { sdkTrans.Rollback(); return Models.McpResponse.Err(
                                    code: "SupertypeNotFound",
                                    message: $"Supertype attribute '{superName}' does not exist in the KB.",
                                    hint: "A subtype must point at an existing base attribute.",
                                    target: groupName); }

                                var existing = part.Members.FirstOrDefault(mm => mm.Subtype != null
                                    && string.Equals(mm.Subtype.Name, name, StringComparison.OrdinalIgnoreCase));
                                if (existing == null)
                                {
                                    existing = new GroupMember(part) { Subtype = subtype };
                                    part.Members.Add(existing);
                                }
                                existing.Supertype = super; // writes Subtype.SuperType
                                applied.Add(new JObject { ["name"] = name, ["subtypeOf"] = superName });
                            }
                        }

                        if (remove != null)
                        {
                            foreach (string rn in remove.Select(r => r.ToString()))
                            {
                                var victim = part.Members.FirstOrDefault(mm => mm.Subtype != null
                                    && string.Equals(mm.Subtype.Name, rn, StringComparison.OrdinalIgnoreCase));
                                if (victim == null) { sdkTrans.Rollback(); return Models.McpResponse.Err(
                                    code: "MemberNotFound",
                                    message: $"'{rn}' is not a member of Group '{groupName}'.",
                                    hint: "Read members with genexus_structure action=get_visual.",
                                    target: groupName); }
                                part.Members.Remove(victim);
                                removed.Add(rn);
                            }
                        }

                        group.EnsureSave();
                        sdkTrans.Commit();

                        try { _objectService.GetKbService().GetIndexCache().UpdateEntry(group); }
                        catch (Exception iex) { Logger.Warn("[GroupStructureService] index UpdateEntry failed: " + iex.Message); }

                        return Models.McpResponse.Ok(
                            target: groupName,
                            code: "GroupUpdated",
                            result: new JObject
                            {
                                ["group"] = group.Name,
                                ["members"] = applied,
                                ["removed"] = removed
                            });
                    }
                    catch (Exception ex)
                    {
                        try { sdkTrans.Rollback(); } catch { }
                        return Models.McpResponse.Err(
                            code: "GroupUpdateFailed",
                            message: ex.Message,
                            hint: "A member bind that creates a cyclic subtype chain is rejected by the SDK.",
                            target: groupName,
                            extra: new JObject { ["stackTrace"] = ex.StackTrace });
                    }
                }
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "GroupUpdateFailed",
                    message: ex.Message,
                    hint: "Ensure the Group exists and payload is valid JSON.",
                    target: groupName);
            }
        }

        // Serializes GroupStructurePart.Members as children: [{ name, subtypeOf }] so
        // genexus_structure get_visual gives a write-verify-read round trip.
        public string GetGroupStructure(string groupName)
        {
            try
            {
                var obj = _objectService.FindObject(groupName);
                if (obj == null) return HealingService.FormatNotFoundError(groupName, _objectService.GetKbService().GetIndexCache().GetIndex());
                if (!(obj is Group group)) return Models.McpResponse.Err(
                    code: "NotAGroup", message: $"Object '{groupName}' is not a Group.", target: groupName);

                var part = group.Parts.Get<GroupStructurePart>();
                var children = new JArray();
                if (part != null)
                {
                    foreach (var member in part.Members)
                    {
                        if (member?.Subtype == null) continue;
                        children.Add(new JObject
                        {
                            ["name"] = member.Subtype.Name,
                            ["subtypeOf"] = member.Supertype?.Name
                        });
                    }
                }

                return Models.McpResponse.Ok(
                    target: groupName,
                    code: "StructureRead",
                    result: new JObject
                    {
                        ["name"] = group.Name,
                        ["type"] = "Group",
                        ["description"] = group.Description,
                        ["children"] = children,
                        ["_meta"] = new JObject
                        {
                            ["suggested_next"] = new JObject
                            {
                                ["tool"] = "genexus_analyze",
                                ["args"] = new JObject { ["name"] = group.Name },
                                ["why"] = "Confirms member-based dependencies for the Group."
                            }
                        }
                    });
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "StructureReadFailed",
                    message: ex.Message,
                    hint: "Ensure the target is a Group.",
                    target: groupName);
            }
        }
    }
}
