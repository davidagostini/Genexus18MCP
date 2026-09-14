using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Routers
{
    public interface IMcpModuleRouter
    {
        string ModuleName { get; }
        object? ConvertToolCall(string toolName, JObject? arguments);
    }

    // Common arg-extraction helpers shared by every router. The five routers
    // hit `args?["xxx"]?.ToString()` and friends dozens of times; centralising
    // here keeps the call sites scannable and the null-guarding consistent.
    internal static class RouterArgs
    {
        public static string? Str(JObject? args, string key) =>
            args?[key]?.ToString();

        public static string? Part(JObject? args) =>
            Str(args, "part") ?? Str(args, "partName");

        public static int? Int(JObject? args, string key) =>
            args?[key]?.ToObject<int?>();

        public static bool Bool(JObject? args, string key, bool defaultValue = false) =>
            args?[key]?.ToObject<bool?>() ?? defaultValue;
    }
}
