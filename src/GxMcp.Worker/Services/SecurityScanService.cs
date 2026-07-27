using System;
using System.Collections.Generic;
using System.IO;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
#if HAS_SECURITY_SCANNER
using Scanner = GeneXus.SecurityScanner.Common;
using ScannerServices = GeneXus.SecurityScanner.Common.Services;
#endif

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_security action=scan_native — the native GeneXus Security Scanner (P0 #2).
    /// Read-only. Distinct from action=audit_gam (env-prop scan) and action=scan_secrets
    /// (regex over Source): this drives the SDK's own <c>ISecurityScannerService.Scan</c>,
    /// the same engine the IDE's Security Scanner runs.
    /// </summary>
    public class SecurityScanService
    {
        private readonly KbService _kb;

        public SecurityScanService(KbService kb)
        {
            _kb = kb;
        }

        public string Run(JObject args)
        {
#if HAS_SECURITY_SCANNER
            if (!KbModelGuard.TryGetDesignModel(_kb, out var model, out var kbErr))
                return kbErr;

            // ISecurityScannerService is registered only by the IDE's Security Scanner
            // package (not loaded headless), so the service registry is empty here. But the
            // concrete SecurityScannerService is a public class with a public ctor — construct
            // it directly (same idiom as GamService's concrete resolve) and load its command
            // plugins from <gxInstall>\Security\Commands. Fall back to the registry if present.
            ScannerServices.ISecurityScannerService scanner = null;
            try
            {
                var concrete = new ScannerServices.SecurityScannerService();
                string commandsFolder = ResolveCommandsFolder();
                if (commandsFolder != null)
                {
                    try { concrete.Initialize(commandsFolder); } catch { /* commands optional; model-level checks still run */ }
                }
                scanner = concrete;
            }
            catch { scanner = SdkServiceLocator.TryResolve<ScannerServices.ISecurityScannerService>(); }

            if (scanner == null)
                return McpResponse.Err(
                    code: "SecurityScannerServiceUnavailable",
                    message: "Could not construct or resolve the GeneXus SDK's SecurityScannerService in this worker session.",
                    hint: "Confirm GeneXus.SecurityScanner.Common is present in the install. Restart the worker (genexus_worker_reload mode=hard) and retry.");

            try
            {
                var plan = ScannerServices.SecurityScanPlan.GetForModel(model);
                if (plan == null)
                    return McpResponse.Err(
                        code: "SecurityScanPlanUnavailable",
                        message: "No SecurityScanPlan is defined for this KB model.",
                        hint: "Configure the Security Scanner in the GeneXus IDE first (it seeds the default plan).");

                var query = new ScannerServices.KBObjectQuery
                {
                    Model = model,
                    WhiteList = plan.GetWhiteList(),
                    AuthorizationProcedure = plan.GetAuthorizationProcedure()
                };

                var output = new Collector();
                scanner.Scan(query, plan, output);

                return McpResponse.Ok(
                    code: "SecurityScanCompleted",
                    result: new JObject
                    {
                        ["planName"] = SafeStr(() => plan.Name),
                        ["errorCount"] = output.Errors.Count,
                        ["warningCount"] = output.Warnings.Count,
                        ["findings"] = output.ToJson(),
                        ["source"] = "sdk:ISecurityScannerService"
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "SecurityScanFailed", message: ex.Message, hint: "Check the worker log for the full stack trace.");
            }
#else
            return McpResponse.Err(
                code: "SecurityScannerServiceUnavailable",
                message: "GeneXus.SecurityScanner.Common.dll was not found in the GeneXus installation.",
                hint: "Install the Security Scanner extension for GeneXus 18 to enable native security scans.");
#endif
        }

#if HAS_SECURITY_SCANNER
        private static string SafeStr(Func<string> f) { try { return f(); } catch { return null; } }

        // <gxInstall>\Security\Commands holds the scanner's command plugins. Try GX_PATH env,
        // the default install path, then the process cwd (InitializeSdk sets it to gxPath).
        private static string ResolveCommandsFolder()
        {
            foreach (var root in new[]
            {
                Environment.GetEnvironmentVariable("GX_PATH"),
                @"C:\Program Files (x86)\GeneXus\GeneXus18",
                Directory.GetCurrentDirectory()
            })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                var folder = System.IO.Path.Combine(root, "Security", "Commands");
                if (Directory.Exists(folder)) return folder;
            }
            return null;
        }

        /// <summary>
        /// Worker-side <c>IScannerOuput</c> that accumulates scan findings instead of driving
        /// the IDE's report window. Every method is defensive — the scanner drives the whole
        /// interface and a throw here would abort the scan.
        /// </summary>
        private sealed class Collector : Scanner.IScannerOuput
        {
            public readonly List<JObject> Errors = new List<JObject>();
            public readonly List<JObject> Warnings = new List<JObject>();
            private readonly List<string> _infos = new List<string>();

            public void SetError(Scanner.ISecurityCommand command, string description, string name, string message, KBObject m_Object)
                => Errors.Add(Row("error", message ?? description, m_Object, null, name));

            public void SetError(string message, KBObject m_Object, string link)
                => Errors.Add(Row("error", message, m_Object, link, null));

            public void SetWarning(string message, KBObject m_Object, string link)
                => Warnings.Add(Row("warning", message, m_Object, link, null));

            public void SetModelError(Scanner.ISecurityCommand command, string description, string message)
                => Errors.Add(Row("modelError", message ?? description, null, null, null));

            public void SetInfo(string message) { if (!string.IsNullOrEmpty(message)) _infos.Add(message); }
            public void SectionStart() { }
            public void SectionEnd(bool success) { }
            public void Show() { }
            public void Clean() { Errors.Clear(); Warnings.Clear(); _infos.Clear(); }

            public JArray ToJson()
            {
                var arr = new JArray();
                foreach (var e in Errors) arr.Add(e);
                foreach (var w in Warnings) arr.Add(w);
                return arr;
            }

            private static JObject Row(string severity, string message, KBObject obj, string link, string name)
            {
                string objName = null;
                try { objName = obj?.Name; } catch { }
                return new JObject
                {
                    ["severity"] = severity,
                    ["message"] = message,
                    ["object"] = objName ?? name,
                    ["link"] = link
                };
            }
        }
#endif
    }
}
