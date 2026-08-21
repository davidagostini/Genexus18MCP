namespace GxMcp.Gateway
{
    /// <summary>
    /// issue #113 — policy for the async-build background status poller. When the worker
    /// process dies mid-build, every Build/Status poll comes back as an error envelope;
    /// previously the poller looped until its 30-minute hard cap while a wait_until_done
    /// caller (or the MCP transport) hung with no terminal answer. A small run of
    /// consecutive failures is tolerated (transient pipe hiccups); past that the worker
    /// is treated as gone and the job is completed as failed immediately.
    /// </summary>
    internal static class BuildStatusPollPolicy
    {
        public const int MaxConsecutiveFailures = 3;

        public static bool ShouldAbort(int consecutiveFailures) => consecutiveFailures >= MaxConsecutiveFailures;
    }
}
