using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Routers
{
    /// <summary>Typed domain routes extracted from the legacy operations router.</summary>
    public sealed class TelemetryRouter : IMcpModuleRouter
    {
        public string ModuleName => "Operations";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            switch (toolName)
            {
                case "genexus_telemetry": return ConvertTelemetryUmbrella(args);
                default: return null;
            }
        }

        private object? ConvertTelemetryUmbrella(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            switch (action)
            {
                case "logs":
                    return new
                    {
                        module = "Object",
                        action = "ReadLogs",
                        target = "_self",
                        lines = args?["tail"]?.ToObject<int?>() ?? args?["lines"]?.ToObject<int?>() ?? 100,
                        filterCorrelation = args?["filterCorrelation"]?.ToString(),
                        grep = args?["grep"]?.ToString(),
                        since = args?["since"]?.ToString(),
                        objectFilter = args?["target"]?.ToString()
                    };

                case "friction_append":
                    return new
                    {
                        module = "FrictionLog",
                        action = "Append",
                        tool = args?["tool"]?.ToString(),
                        message = args?["message"]?.ToString(),
                        severity = args?["severity"]?.ToString()
                    };

                case "friction_tail":
                    return new
                    {
                        module = "FrictionLog",
                        action = "Tail",
                        n = args?["n"]?.ToObject<int?>() ?? 20
                    };

                case "learning_report":
                    return new
                    {
                        module = "Learning",
                        action = "Report",
                        since = args?["since"]?.ToString(),
                        until = args?["until"]?.ToString()
                    };

                case "profile_analyze":
                case "profile_hotspots":
                case "profile_correlate":
                {
                    var inner = action switch
                    {
                        "profile_hotspots" => "hotspots",
                        "profile_correlate" => "correlate",
                        _ => "analyze"
                    };
                    return new
                    {
                        module = "Profile",
                        action = inner,
                        target = args?["target"]?.ToString(),
                        @params = args
                    };
                }

                default:
                    return new
                    {
                        module = "Error",
                        action = "InvalidAction",
                        error = $"genexus_telemetry: unknown or gateway-only action '{action}'. Worker-routed: logs|friction_append|friction_tail|learning_report|profile_analyze|profile_hotspots|profile_correlate."
                    };
            }
        }

        // IO umbrella dispatcher. Replaces _asset/_export_object/_import_object/_export_unified/_screenshot_publish/_ocr_screenshot.
    }
}
