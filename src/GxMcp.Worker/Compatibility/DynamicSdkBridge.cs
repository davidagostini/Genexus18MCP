using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Compatibility
{
    public static class DynamicSdkBridge
    {
        public static string CurrentDriver { get; set; } = "native-sdk";
        public static string CurrentMajor { get; set; } = "18";

        public static bool IsLegacyDriver =>
            !string.Equals(CurrentDriver, "native-sdk", StringComparison.OrdinalIgnoreCase);

        public static bool IsComDriver =>
            string.Equals(CurrentDriver, "com-gxpublic", StringComparison.OrdinalIgnoreCase);

        public static bool IsDotNetReflectionDriver =>
            string.Equals(CurrentDriver, "dotnet-reflection", StringComparison.OrdinalIgnoreCase);

        public static void Initialize(string driver, string major)
        {
            CurrentDriver = !string.IsNullOrWhiteSpace(driver) ? driver : "native-sdk";
            CurrentMajor = !string.IsNullOrWhiteSpace(major) ? major : "18";
        }

        public static IDisposable Scoped(string driver, string major)
        {
            string prevDriver = CurrentDriver;
            string prevMajor = CurrentMajor;
            Initialize(driver, major);
            return new ScopeReset(() => Initialize(prevDriver, prevMajor));
        }

        private sealed class ScopeReset : IDisposable
        {
            private readonly Action _reset;
            public ScopeReset(Action reset) => _reset = reset;
            public void Dispose() => _reset?.Invoke();
        }

        public static double GetMajorNumber(string major)
        {
            if (string.IsNullOrWhiteSpace(major)) return 18.0;
            if (double.TryParse(major, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d))
                return d;
            return 18.0;
        }

        public static bool CheckCapability(string method, string toolName, string target, out string errorResponse)
        {
            errorResponse = null;

            string normalizedTool = !string.IsNullOrWhiteSpace(toolName)
                ? toolName.Trim().ToLowerInvariant()
                : NormalizeMethodToTool(method);

            if (string.IsNullOrEmpty(normalizedTool)) return true;

            // COM driver check (GeneXus 8.0 & 9.0)
            if (IsComDriver)
            {
                if (IsModernToolUnsupportedOnCom(normalizedTool))
                {
                    errorResponse = Models.McpResponse.Err(
                        code: "UNSUPPORTED_IN_GENEXUS_VERSION",
                        message: $"Tool '{normalizedTool}' is not supported under the COM (GXPublic) driver for GeneXus {CurrentMajor}. GXPublic only supports classic core object models.",
                        hint: GetComAlternativeGuidance(normalizedTool),
                        target: target,
                        errorExtra: new JObject
                        {
                            ["tool"] = normalizedTool,
                            ["currentMajor"] = CurrentMajor,
                            ["driver"] = CurrentDriver,
                            ["minSupportedMajor"] = "10.1"
                        });
                    return false;
                }
            }

            // Version capability matrix
            if (MinVersionMap.TryGetValue(normalizedTool, out var minMajor))
            {
                double currentNum = GetMajorNumber(CurrentMajor);
                double minNum = GetMajorNumber(minMajor);
                if (currentNum < minNum)
                {
                    errorResponse = Models.McpResponse.Err(
                        code: "UNSUPPORTED_IN_GENEXUS_VERSION",
                        message: $"Tool '{normalizedTool}' is not supported in GeneXus {CurrentMajor} ({CurrentDriver}). Feature was introduced in GeneXus {minMajor}.",
                        hint: GetAlternativeGuidance(normalizedTool, CurrentMajor, minMajor),
                        target: target,
                        errorExtra: new JObject
                        {
                            ["tool"] = normalizedTool,
                            ["currentMajor"] = CurrentMajor,
                            ["minSupportedMajor"] = minMajor,
                            ["driver"] = CurrentDriver
                        });
                    return false;
                }
            }

            return true;
        }

        public static string GetAlternativeGuidance(string tool, string currentMajor, string minMajor)
        {
            switch (tool?.Trim().ToLowerInvariant())
            {
                case "genexus_api":
                    return $"In GeneXus {currentMajor} (<17), OpenAPI REST objects do not exist. To expose endpoints, configure a Procedure with 'Expose as Web Service = True' (SOAP/REST) or use direct &HttpResponse manipulation.";
                case "genexus_module":
                    return $"In GeneXus {currentMajor} (<10.3), Modules do not exist; all objects reside in the global namespace. Use Folders to organize objects and reference them by simple name without module prefix (e.g. 'Customer' instead of 'Sales.Customer').";
                case "genexus_design_system":
                    return $"In GeneXus {currentMajor} (<17), Design System Objects (.dso) do not exist. Use Theme objects ('Theme') with genexus_properties or genexus_edit to manage visual classes and styles.";
                case "genexus_gam":
                    return $"In GeneXus {currentMajor} (<10.2), GAM does not exist (introduced in Ev2). Manage authentication and authorization via application procedures, custom database tables, and &WebSession.";
                case "genexus_sdpanel":
                    return $"In GeneXus {currentMajor} (<10.2), Smart Device Panels do not exist (introduced in Ev2). Use standard WebPanels or WorkWith Web.";
                case "genexus_generator_reference":
                    return $"In GeneXus {currentMajor} (<17), Typed .NET generator references are not supported. Use standard External Objects or native compiler flags.";
                default:
                    return $"GeneXus {minMajor} or higher is required to use {tool}.";
            }
        }

        public static string GetComAlternativeGuidance(string tool)
        {
            return $"Under GeneXus 8.0/9.0 (GXPublic COM driver), tool '{tool}' is not available. Supported tools include genexus_read, genexus_query, genexus_edit, genexus_transfer (xpz export/import), and object lifecycle.";
        }

        public static System.Text.Encoding GetEffectiveEncoding()
        {
            string envEnc = Environment.GetEnvironmentVariable("GXMCP_SOURCE_ENCODING");
            if (!string.IsNullOrWhiteSpace(envEnc))
            {
                try { return System.Text.Encoding.GetEncoding(envEnc); } catch { }
            }

            // Classic GeneXus 8.0/9.0 and Evolution 1/2 historically use Windows-1252 (ANSI/Latin1)
            if (IsComDriver || (IsLegacyDriver && GetMajorNumber(CurrentMajor) < 15.0))
            {
                try { return System.Text.Encoding.GetEncoding(1252); } catch { }
            }

            return System.Text.Encoding.UTF8;
        }

        private static string NormalizeMethodToTool(string method)
        {
            if (string.IsNullOrWhiteSpace(method)) return null;
            string m = method.Trim().ToLowerInvariant();
            switch (m)
            {
                case "api": return "genexus_api";
                case "gam": return "genexus_gam";
                case "designsystem": return "genexus_design_system";
                case "generatorreference": return "genexus_generator_reference";
                case "module": return "genexus_module";
                case "sdpanel": return "genexus_sdpanel";
                case "pattern": return "genexus_apply_pattern";
                case "wwpaction": return "genexus_wwp";
                default: return null;
            }
        }

        private static readonly Dictionary<string, string> MinVersionMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["genexus_api"] = "17",
                ["genexus_gam"] = "10.2",
                ["genexus_design_system"] = "17",
                ["genexus_generator_reference"] = "17",
                ["genexus_module"] = "10.3",
                ["genexus_sdpanel"] = "10.2",
            };

        private static bool IsModernToolUnsupportedOnCom(string tool)
        {
            return tool.StartsWith("genexus_gam", StringComparison.OrdinalIgnoreCase)
                || tool.StartsWith("genexus_api", StringComparison.OrdinalIgnoreCase)
                || tool.StartsWith("genexus_design_system", StringComparison.OrdinalIgnoreCase)
                || tool.StartsWith("genexus_module", StringComparison.OrdinalIgnoreCase)
                || tool.StartsWith("genexus_apply_pattern", StringComparison.OrdinalIgnoreCase)
                || tool.StartsWith("genexus_wwp", StringComparison.OrdinalIgnoreCase)
                || tool.StartsWith("genexus_sdpanel", StringComparison.OrdinalIgnoreCase)
                || tool.StartsWith("genexus_generator_reference", StringComparison.OrdinalIgnoreCase);
        }
    }
}
