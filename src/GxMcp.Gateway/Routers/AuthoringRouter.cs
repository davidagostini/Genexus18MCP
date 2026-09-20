using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Routers
{
    /// <summary>Typed domain routes extracted from the legacy operations router.</summary>
    public sealed class AuthoringRouter : IMcpModuleRouter
    {
        public string ModuleName => "Operations";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            switch (toolName)
            {
                case "genexus_authoring": return ConvertAuthoringToolCall(args);
                default: return null;
            }
        }

        private object? ConvertAuthoringToolCall(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            string? mappedAction = action switch
            {
                "add_external_method" => "AddExternalMethod",
                "add_external_property" => "AddExternalProperty",
                "add_menu_option" => "AddMenuOption",
                "add_condition" => "AddDataSelectorCondition",
                _ => null
            };

            if (mappedAction == null) return null;

            return new
            {
                module = "Authoring",
                action = mappedAction,
                target = args?["name"]?.ToString(),
                payload = args?["payload"]?.ToString()
            };
        }
    }
}
