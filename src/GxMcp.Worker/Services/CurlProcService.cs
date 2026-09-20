using System;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using GenexusServices = Artech.Genexus.Common.Services;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_create action=curl_procedure (P2 #9). Scaffolds a REST-consumer Procedure from
    /// a curl command — the IDE "Import from cURL" flow — over
    /// <c>ICurlGeneratorService.Generate(model, procName, description, parent, curlCommand)</c>.
    /// Creates a KB object (write). Constructs the public concrete <c>CurlGeneratorService</c>
    /// (not in the headless registry; ConstructOrResolve).
    /// </summary>
    public class CurlProcService
    {
        private readonly KbService _kb;
        private readonly ObjectService _objects;

        public CurlProcService(KbService kb, ObjectService objects)
        {
            _kb = kb;
            _objects = objects;
        }

        public string Run(JObject args)
        {
            string procName = args?["name"]?.ToString();
            string curl = args?["curl"]?.ToString() ?? args?["curlCommand"]?.ToString() ?? args?["content"]?.ToString();
            string description = args?["description"]?.ToString() ?? procName;

            if (string.IsNullOrWhiteSpace(procName))
                return McpResponse.Err("BadArgs", "curl_procedure requires name (the new Procedure's name).", "Pass name=<ProcName>.");
            if (string.IsNullOrWhiteSpace(curl))
                return McpResponse.Err("BadArgs", "curl_procedure requires curl (the curl command).", "Pass curl=\"curl -X POST https://...\".");

            if (!KbModelGuard.TryGetDesignModel(_kb, out var model, out var kbErr))
                return kbErr;

            var curlSvcType = Type.GetType("Artech.Genexus.Common.Services.ICurlGeneratorService, Artech.Genexus.Common")
                ?? typeof(Artech.Genexus.Common.Objects.Procedure).Assembly.GetType("Artech.Genexus.Common.Services.ICurlGeneratorService");
            if (curlSvcType == null)
                return McpResponse.Err("CurlGeneratorServiceUnavailable", "ICurlGeneratorService is not available in this GeneXus version.", "Importing procedures from cURL requires GeneXus 17 or later.");

            object svc = null;
            try
            {
                var concreteType = Type.GetType("Artech.Packages.Genexus.BL.Services.CurlGeneratorService, Artech.Packages.Genexus.BL");
                if (concreteType != null)
                    svc = Activator.CreateInstance(concreteType);
            }
            catch { }

            if (svc == null)
            {
                svc = SdkServiceLocator.TryResolve(curlSvcType.GUID);
            }

            if (svc == null)
                return McpResponse.Err("CurlGeneratorServiceUnavailable", "Could not construct the SDK's CurlGeneratorService.", "Restart the worker (genexus_worker_reload mode=hard) and retry.");

            try
            {
                // parent = null → created at the KB root (folder/module placement is IDE-only).
                ((dynamic)svc).Generate(model, procName, description, null, curl);

                KBObject created = null;
                try { created = _objects?.FindObject(procName, "Procedure"); } catch { }

                return McpResponse.Ok(
                    code: created != null ? "CurlProcedureCreated" : "CurlProcedureGenerated",
                    result: new JObject
                    {
                        ["name"] = procName,
                        ["created"] = created != null,
                        ["hint"] = created == null ? "Generate returned without error; the Procedure may need a genexus_lifecycle action=index to appear." : null,
                        ["source"] = "sdk:ICurlGeneratorService.Generate"
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err("CurlGenerateFailed", ex.Message, "Check the curl syntax and the worker log for the full stack trace.");
            }
        }
    }
}
