using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        internal static bool IsCompleteMutationRecoveryRead(JToken? result)
            => result is JObject payload
                && payload["error"] == null
                && !string.IsNullOrWhiteSpace(payload["versionToken"]?.ToString())
                && payload["truncated"]?.Value<bool>() != true
                && payload["isTruncatedByWorker"]?.Value<bool>() != true
                && payload["truncatedByGateway"]?.Value<bool>() != true
                && payload["_meta"]?["truncated"] == null
                && (payload["offset"]?.Value<int>() ?? 0) == 0;

        internal static JObject? HandleMutationJournalAction(MutationRecoveryRegistry registry, JObject? args)
        {
            string action = args?["action"]?.ToString() ?? "recover";
            if (action == "recover") return null;
            if ((action == "journal_status" || action == "journal_repair")
                && args?["force"]?.Value<bool>() != true)
            {
                return action == "journal_status"
                    ? registry.GetJournalStatus()
                    : registry.RepairJournal(args?["dryRun"]?.Value<bool>() ?? true);
            }
            return new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject
                {
                    ["code"] = "InvalidJournalRecoveryRequest",
                    ["message"] = "Use journal_status or journal_repair without force. Journal repair never restarts Workers or clears pending read requirements."
                }
            };
        }
    }
}
