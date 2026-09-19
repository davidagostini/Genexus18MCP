using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Routers
{
    /// <summary>Typed domain routes extracted from the legacy operations router.</summary>
    public sealed class RefactorRouter : IMcpModuleRouter
    {
        public string ModuleName => "Operations";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            switch (toolName)
            {
                case "genexus_refactor": return ConvertRefactorToolCall(args);
                case "genexus_rename_across_kb":
                {
                    string? from = args?["from"]?.ToString() ?? args?["oldName"]?.ToString();
                    string? to = args?["to"]?.ToString() ?? args?["newName"]?.ToString();
                    string? type = args?["type"]?.ToString();
                    bool renameAcrossDryRun = args?["dryRun"]?.ToObject<bool?>() ?? false;
                    // RenameAttribute path is the index-driven one (writes attribute then
                    // updates every CalledBy edge). For non-Attribute types, RenameObject
                    // currently falls into the same code path (line 64 of RefactorService).
                    string refactorAction = string.Equals(type, "Attribute", System.StringComparison.OrdinalIgnoreCase)
                        ? "RenameAttribute"
                        : "RenameObject";
                    return new
                    {
                        module = "Refactor",
                        action = refactorAction,
                        target = from,
                        dryRun = renameAcrossDryRun,
                        payload = new JObject
                        {
                            ["oldName"] = from,
                            ["newName"] = to,
                            ["type"] = type
                        }.ToString()
                    };
                }

                // Variable umbrella: add|delete|modify. Replaces _add_variable, _delete_variable, _modify_variable.
                default: return null;
            }
        }

        private object? ConvertRefactorToolCall(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) return null;
            bool refactorDryRun = args?["dryRun"]?.ToObject<bool?>() ?? false;

            if (action == "ExtractProcedure")
            {
                JObject? payloadObj = null;
                if (args?["payload"] is JObject pObj) payloadObj = pObj;
                else if (args?["payload"] is JValue pVal && pVal.Value is string pStr && pStr.TrimStart().StartsWith("{"))
                {
                    try { payloadObj = JObject.Parse(pStr); } catch { }
                }

                return new
                {
                    module = "Refactor",
                    action,
                    target = args?["target"]?.ToString() ?? args?["objectName"]?.ToString() ?? payloadObj?["target"]?.ToString(),
                    dryRun = refactorDryRun,
                    payload = new JObject
                    {
                        ["code"] = args?["code"]?.ToString() ?? args?["codeToExtract"]?.ToString() ?? payloadObj?["code"]?.ToString() ?? payloadObj?["codeToExtract"]?.ToString(),
                        ["procedureName"] = args?["procedureName"]?.ToString() ?? payloadObj?["procedureName"]?.ToString() ?? payloadObj?["name"]?.ToString()
                    }.ToString()
                };
            }

            if (action == "ExtractSubroutine" || string.Equals(action, "extract_subroutine", StringComparison.OrdinalIgnoreCase))
            {
                JObject? payloadObj = null;
                if (args?["payload"] is JObject pObj) payloadObj = pObj;
                else if (args?["payload"] is JValue pVal && pVal.Value is string pStr && pStr.TrimStart().StartsWith("{"))
                {
                    try { payloadObj = JObject.Parse(pStr); } catch { }
                }

                return new
                {
                    module = "Refactor",
                    action = "ExtractSubroutine",
                    target = args?["target"]?.ToString() ?? args?["objectName"]?.ToString() ?? payloadObj?["target"]?.ToString(),
                    dryRun = refactorDryRun,
                    payload = new JObject
                    {
                        ["code"] = args?["code"]?.ToString() ?? args?["codeToExtract"]?.ToString() ?? payloadObj?["code"]?.ToString() ?? payloadObj?["codeToExtract"]?.ToString(),
                        ["subroutineName"] = args?["subroutineName"]?.ToString() ?? args?["subroutine"]?.ToString() ?? args?["name"]?.ToString() ?? payloadObj?["subroutineName"]?.ToString() ?? payloadObj?["subroutine"]?.ToString() ?? payloadObj?["name"]?.ToString()
                    }.ToString()
                };
            }

            if (action == "WWPSetCondition")
            {
                return new
                {
                    module = "Refactor",
                    action,
                    target = args?["target"]?.ToString() ?? args?["objectName"]?.ToString(),
                    dryRun = refactorDryRun,
                    payload = new JObject
                    {
                        ["controlAttribute"] = args?["controlAttribute"]?.ToString(),
                        ["value"] = args?["value"]?.ToString(),
                        ["typeFilter"] = args?["type"]?.ToString()
                    }.ToString()
                };
            }

            string? target = args?["target"]?.ToString();
            if (action == "RenameVariable")
            {
                target = args?["objectName"]?.ToString();
            }

            return new
            {
                module = "Refactor",
                action,
                target,
                dryRun = refactorDryRun,
                payload = new JObject
                {
                    ["oldName"] = args?["target"]?.ToString(),
                    ["newName"] = args?["newName"]?.ToString(),
                    ["type"] = args?["type"]?.ToString()
                }.ToString()
            };
        }
    }
}
