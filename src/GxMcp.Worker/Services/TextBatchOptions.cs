using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    internal sealed class TextBatchOptions
    {
        public bool DryRun { get; private set; }
        public bool Confirm { get; private set; }
        public bool Overwrite { get; private set; }
        public bool ListOnly { get; private set; }
        public bool StopOnError { get; private set; }
        public bool IncludeChildren { get; private set; }
        public bool IncludeVisualParts { get; private set; }
        public bool ForceSave { get; private set; }
        public bool RollbackOnFailure { get; private set; }
        public int Skip { get; private set; }
        public int Limit { get; private set; }

        public static bool TryParse(
            JObject args,
            bool defaultDryRun,
            bool defaultListOnly,
            bool defaultStopOnError,
            bool defaultIncludeChildren,
            bool defaultIncludeVisualParts,
            bool defaultForceSave,
            bool defaultRollbackOnFailure,
            out TextBatchOptions options,
            out string error)
        {
            args = args ?? new JObject();
            options = new TextBatchOptions
            {
                DryRun = args["dryRun"]?.ToObject<bool?>() ?? defaultDryRun,
                Confirm = args["confirm"]?.ToObject<bool?>() ?? false,
                Overwrite = args["overwrite"]?.ToObject<bool?>() ?? false,
                ListOnly = args["listOnly"]?.ToObject<bool?>() ?? defaultListOnly,
                StopOnError = args["stopOnError"]?.ToObject<bool?>() ?? defaultStopOnError,
                IncludeChildren = args["includeChildren"]?.ToObject<bool?>() ?? defaultIncludeChildren,
                IncludeVisualParts = args["includeVisualParts"]?.ToObject<bool?>() ?? defaultIncludeVisualParts,
                ForceSave = args["forceSave"]?.ToObject<bool?>() ?? defaultForceSave,
                RollbackOnFailure = args["rollbackOnFailure"]?.ToObject<bool?>() ?? defaultRollbackOnFailure
            };
            error = null;

            if (!TryReadNonNegativeInt(args, "skip", out int skip, out error)) return false;
            if (!TryReadNonNegativeInt(args, "limit", out int limit, out error)) return false;
            options.Skip = skip;
            options.Limit = limit;
            return true;
        }

        private static bool TryReadNonNegativeInt(JObject args, string name, out int value, out string error)
        {
            value = 0;
            error = null;
            JToken token = args[name];
            if (token == null || token.Type == JTokenType.Null) return true;
            try { value = token.ToObject<int>(); }
            catch
            {
                error = name + " must be an integer greater than or equal to zero.";
                return false;
            }
            if (value < 0)
            {
                error = name + " must be greater than or equal to zero.";
                return false;
            }
            return true;
        }
    }
}
