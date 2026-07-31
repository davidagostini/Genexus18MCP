using System;
using Newtonsoft.Json.Linq;
using Artech.Genexus.Common.Objects;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services.Structure
{
    // issue #39 follow-up: edit the enum values (and optionally base type) of an EXISTING Domain.
    // Domain creation already accepts enumValues; this closes the edit-after gap. ApplyEnumValues
    // replaces the whole enum set, so callers pass the full desired list.
    public class DomainWriteService
    {
        private readonly ObjectService _objectService;

        public DomainWriteService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        // payload = { enumValues:[{name,value,description?}], dataType?, length?, decimals?, signed? }
        public string SetDomainProperties(string domainName, string payload)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(domainName)) return Models.McpResponse.Err(
                    code: "InvalidDomain", message: "Domain name (name) is required.", target: domainName);
                if (string.IsNullOrWhiteSpace(payload)) return Models.McpResponse.Err(
                    code: "InvalidPayload", message: "payload is required.",
                    hint: "e.g. { \"enumValues\": [{\"name\":\"Active\",\"value\":\"A\"}] }.",
                    target: domainName);

                // GetKB() is dynamic; type the model so EnsureSave binds statically.
                Artech.Architecture.Common.Objects.KBModel model =
                    _objectService.GetKbService().GetKB().DesignModel;
                var domain = _objectService.FindObject(domainName) as Domain;
                if (domain == null) return Models.McpResponse.Err(
                    code: "DomainNotFound",
                    message: $"Domain '{domainName}' not found.",
                    hint: "Create it first with genexus_create type=Domain.",
                    target: domainName);

                var json = JObject.Parse(payload);
                var applied = new JArray();

                using (var sdkTrans = model.KB.BeginTransaction())
                {
                    try
                    {
                        string dataType = json["dataType"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(dataType))
                        {
                            int? len = json["length"]?.ToObject<int?>();
                            int? dec = json["decimals"]?.ToObject<int?>();
                            bool? signed = json["signed"]?.ToObject<bool?>();
                            if (DomainPropertyApplier.ApplyPrimitive(domain, dataType, len, dec, signed))
                                applied.Add("dataType");
                            else { try { sdkTrans.Rollback(); } catch { } return Models.McpResponse.Err(
                                code: "DomainTypeFailed",
                                message: $"Could not apply dataType '{dataType}'.",
                                hint: "Use a canonical type: Character, VarChar, Numeric, Date, DateTime, Boolean, etc.",
                                target: domainName); }
                        }

                        var enumArr = json["enumValues"] as JArray;
                        if (enumArr != null)
                        {
                            // ISSUE-55 ground truth (2026-07-31, GeneXus 18.0.10): enum values
                            // are stored RAW in the version XML for every family (the template's
                            // own HttpMethod char enum stores <Value>GET</Value>). Quoted values
                            // are silently dropped by the bag write, so values pass verbatim.
                            var specs = DomainEnumValues.FromJson(enumArr);
                            int n = DomainPropertyApplier.ApplyEnumValues(domain, specs);
                            if (n < 0) { try { sdkTrans.Rollback(); } catch { } return Models.McpResponse.Err(
                                code: "EnumWriteFailed",
                                message: "Could not write EnumValues — SDK helper not resolvable.",
                                target: domainName); }
                            applied.Add("enumValues");
                        }

                        if (applied.Count == 0) { try { sdkTrans.Rollback(); } catch { } return Models.McpResponse.Err(
                            code: "NoPropertiesToApply",
                            message: "payload contained no recognized domain properties.",
                            hint: "Recognized: enumValues, dataType (+length/decimals/signed).",
                            target: domainName); }

                        domain.EnsureSave();
                        sdkTrans.Commit();
                        return Models.McpResponse.Ok(
                            target: domainName,
                            code: "DomainUpdated",
                            result: new JObject { ["domain"] = domain.Name, ["applied"] = applied });
                    }
                    catch (Exception ex)
                    {
                        try { sdkTrans.Rollback(); } catch { }
                        return Models.McpResponse.Err(
                            code: "DomainUpdateFailed",
                            message: ex.Message,
                            hint: "Check the worker log for the SDK stack trace.",
                            target: domainName,
                            extra: new JObject { ["stackTrace"] = ex.StackTrace });
                    }
                }
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "DomainUpdateFailed",
                    message: ex.Message,
                    hint: "Ensure the domain exists and payload is valid JSON.",
                    target: domainName);
            }
        }
    }
}
