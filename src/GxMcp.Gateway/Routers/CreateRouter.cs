using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Routers
{
    /// <summary>Typed domain routes extracted from the legacy operations router.</summary>
    public sealed class CreateRouter : IMcpModuleRouter
    {
        public string ModuleName => "Operations";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            switch (toolName)
            {
                case "genexus_create": return ConvertCreateUmbrella(args);
                default: return null;
            }
        }

        private object? ConvertCreateUmbrella(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            string? name = args?["name"]?.ToString();
            string? type = args?["type"]?.ToString();

            if (string.IsNullOrWhiteSpace(action))
            {
                if (args?["source"] != null || args?["variables"] != null || args?["rules"] != null || args?["parms"] != null)
                {
                    action = "object_atomic";
                }
                else if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(type))
                {
                    action = "object";
                }
            }

            switch (action)
            {
                case "object":
                    return new
                    {
                        module = "Object",
                        action = "Create",
                        target = name,
                        type,
                        dataType = args?["dataType"]?.ToString(),
                        length = args?["length"]?.ToObject<int?>(),
                        decimals = args?["decimals"]?.ToObject<int?>(),
                        signed = args?["signed"]?.ToObject<bool?>(),
                        description = args?["description"]?.ToString(),
                        basedOn = args?["basedOn"]?.ToString(),
                        enumValues = args?["enumValues"],
                        // issue #28 item 7: SDT first-item seed override.
                        firstItem = args?["firstItem"]?.ToString(),
                        firstItemType = args?["firstItemType"]?.ToString(),
                        // issue #50: forward a requested folder/module destination so the worker
                        // can reject it loudly (SDK placement is a no-op) instead of silently
                        // creating in Root Module.
                        folder = args?["folder"]?.ToString(),
                        destModule = args?["module"]?.ToString(),
                        parentPath = args?["parentPath"]?.ToString(),
                        dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                        // issue #60 — validationMode="specify" runs the inline Specify pass after
                        // creation; rollbackOnFailure deletes/reverts on spec errors (best-effort).
                        validationMode = args?["validationMode"]?.ToString(),
                        rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? false
                    };

                case "object_atomic":
                    // Issue #62 — atomic create/update: one validated call with variables[],
                    // rules[], parms[], properties{} and source. Forward the raw args; the
                    // worker's AtomicCreateService validates the whole definition BEFORE the
                    // first save, composes the SDK write primitives, and compensates on failure
                    // (delete fresh object / restore snapshots) so nothing partial is left.
                    return new
                    {
                        module = "AtomicCreate",
                        action = "Run",
                        target = name,
                        @params = args
                    };

                case "popup":
                    return new
                    {
                        module = "Popup",
                        action = "Create",
                        target = name,
                        name,
                        spec = args?["spec"],
                        dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false
                    };

                case "sd_panel_create":
                case "sd_panel_inspect":
                case "sd_panel_edit":
                {
                    var inner = action switch
                    {
                        "sd_panel_create" => "create",
                        "sd_panel_edit" => "edit",
                        _ => "inspect"
                    };
                    return new
                    {
                        module = "SdPanel",
                        action = inner,
                        target = name,
                        @params = args
                    };
                }

                case "save_as":
                    return new
                    {
                        module = "Object",
                        action = "SaveAs",
                        target = name,
                        @params = args
                    };

                case "scaffold":
                    return new
                    {
                        module = "Forge",
                        action = "Scaffold",
                        type,
                        name,
                        code = args?["content"]?.ToString(),
                        description = args?["description"]?.ToString()
                    };
                case "translate":
                    return new
                    {
                        module = "Conversion",
                        action = "TranslateTo",
                        target = name,
                        language = args?["content"]?.ToString()
                    };
                case "sample":
                    return new { module = "Pattern", action = "GetSample", target = type };

                case "template":
                    return new
                    {
                        module = "Write",
                        action = "ApplyTemplate",
                        target = name,
                        @params = args
                    };

                // P2 #9: scaffold a Procedure from a curl command over ICurlGeneratorService.
                case "curl_procedure":
                    return new
                    {
                        module = "CurlProc",
                        action = "Run",
                        @params = args
                    };

                default:
                    return new
                    {
                        module = "Error",
                        action = "InvalidAction",
                        error = $"genexus_create: unknown action '{action}'. Valid: object|object_atomic|popup|sd_panel_create|sd_panel_inspect|sd_panel_edit|curl_procedure|save_as|scaffold|translate|sample|template."
                    };
            }
        }

        // Telemetry umbrella dispatcher (worker-routed actions). executions + watch_event
        // are gateway-only and short-circuited in Program.cs before reaching here.
    }
}
