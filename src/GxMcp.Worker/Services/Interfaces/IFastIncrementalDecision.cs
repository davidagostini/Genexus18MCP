using System.Collections.Generic;

namespace GxMcp.Worker.Services
{
    // Item 28 (mcp-improvements-2026-05-22, Tier-S) — EXPERIMENTAL.
    //
    // Decides what the in-process build runner can SKIP when the agent opts into
    // fastIncremental=true. BuildService applies the decision to the in-process
    // runner: it can skip Specify on already-clean targets and skip the
    // IdeWebBuildAndDeploy module-copy step entirely when the dirty set is
    // limited to "incremental-safe" kinds (Procedure / WebPanel / Transaction).
    //
    // The interface exists so unit tests can inject a deterministic decision
    // without standing up an EditDirtyTracker / IndexCacheService. BuildService
    // passes the decision into the real runner: safe decisions select the
    // targeted path, while ForceFullBuild explicitly selects the legacy full
    // path. The default implementation reads EditDirtyTracker and asks
    // IndexCacheService for the target type.
    public interface IFastIncrementalDecision
    {
        FastIncrementalDecision Decide(string kbPath, IReadOnlyList<string> targets);
    }

    public sealed class FastIncrementalDecision
    {
        /// <summary>
        /// True when EditDirtyTracker has no dirty entries for any requested
        /// target AND every target has been built at least once this session.
        /// BuildService returns a short-circuit response without invoking the
        /// SDK at all.
        /// </summary>
        public bool NothingDirty { get; set; }

        /// <summary>
        /// True when the dirty set contains a "risky" object kind
        /// (Theme / MasterPage / Domain / SDT) that requires a full
        /// IdeWebBuildAndDeploy module-copy pass to be safe. The runner
        /// falls back to the legacy full build and surfaces this reason.
        /// </summary>
        public bool ForceFullBuild { get; set; }

        /// <summary>
        /// Reason string surfaced under <c>fallbackReason</c> when
        /// <see cref="ForceFullBuild"/> is true (and only then). Examples:
        ///   "non-incremental-target-kind"
        ///   "target-kind-unknown"
        /// </summary>
        public string FallbackReason { get; set; }

        /// <summary>
        /// True when every dirty target is an incremental-safe kind
        /// (Procedure / WebPanel / Transaction). The runner can skip the
        /// IdeWebBuildAndDeploy module-copy phase — the compiled .dll lands
        /// in place but the IIS / module redeploy is suppressed.
        /// </summary>
        public bool CanSkipDeploy { get; set; }

        /// <summary>
        /// Subset of requested targets where EditDirtyTracker.IsDirty
        /// returned false — i.e. clean and already built. Specify can be
        /// skipped for these. Other (dirty) targets still flow through the
        /// normal Specify + Generate + Compile path.
        /// </summary>
        public IReadOnlyList<string> CanSkipSpecify { get; set; } =
            new List<string>();
    }
}
