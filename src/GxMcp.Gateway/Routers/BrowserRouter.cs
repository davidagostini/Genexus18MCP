using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Routers
{
    /// <summary>Typed domain routes extracted from the legacy operations router.</summary>
    public sealed class BrowserRouter : IMcpModuleRouter
    {
        public string ModuleName => "Operations";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            switch (toolName)
            {
                case "genexus_browser": return ConvertBrowserUmbrella(args);
                default: return null;
            }
        }

        private object? ConvertBrowserUmbrella(JObject? args)
        {
            string? action = args?["action"]?.ToString()?.ToLowerInvariant();
            string? target = args?["target"]?.ToString() ?? args?["name"]?.ToString();

            switch (action)
            {
                case "smoke":
                    return new { module = "smoke_test", action = "Run", target, name = target };

                case "a11y":
                    return new { module = "a11y_audit", action = "Audit", target, name = target };

                case "wcag":
                    return new { module = "WcagCheck", action = "Check", target };

                case "capture":
                    return new
                    {
                        module = "browser_capture",
                        action = "Capture",
                        target,
                        name = target,
                        capture = args?["capture"]
                    };

                case "cross":
                    return new
                    {
                        module = "CrossBrowser",
                        action = "Run",
                        target,
                        browsers = args?["browsers"],
                        capture = args?["capture"]
                    };

                case "preview":
                {
                    var mode = args?["mode"]?.ToString();
                    var previewAction = string.Equals(mode, "run", StringComparison.OrdinalIgnoreCase) ? "Run" : "Render";
                    return new
                    {
                        module = "Preview",
                        action = previewAction,
                        target,
                        name = target,
                        parms = args?["parms"],
                        launcher = args?["launcher"]?.ToString() ?? "auto",
                        buildFirst = args?["buildFirst"]?.ToObject<bool?>() ?? false,
                        waitMs = args?["waitMs"]?.ToObject<int?>() ?? 3000,
                        capture = args?["capture"],
                        diffBaseline = args?["diffBaseline"]?.ToObject<bool?>() ?? false,
                        updateBaseline = args?["updateBaseline"]?.ToObject<bool?>() ?? false,
                        fill = args?["fill"],
                        click = args?["click"]?.ToString(),
                        auth = args?["auth"],
                        emulate = args?["emulate"]?.ToString(),
                        network = args?["network"]?.ToString()
                    };
                }

                default:
                    return new
                    {
                        module = "Error",
                        action = "InvalidAction",
                        error = $"genexus_browser: unknown action '{action}'. Valid: smoke|a11y|wcag|capture|cross|preview."
                    };
            }
        }
    }
}
