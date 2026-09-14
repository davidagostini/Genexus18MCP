using System;
using System.Collections.Generic;
using System.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        /// <summary>
        /// Selects the worker for a graceful reload from the request-resolved KB.
        /// Never falls back to the first process in the pool: that is unsafe when
        /// more than one KB is open and the session lease points at another one.
        /// </summary>
        internal static KbHandle? ResolveWorkerReloadTarget(
            string? requestedAlias,
            KbHandle? requestKb,
            IReadOnlyCollection<KbHandle> openKbs)
        {
            if (openKbs == null || openKbs.Count == 0) return null;

            if (requestKb != null)
            {
                return openKbs.FirstOrDefault(handle =>
                    string.Equals(handle.NormalizedAlias, requestKb.NormalizedAlias, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(requestedAlias))
            {
                return openKbs.FirstOrDefault(handle =>
                    string.Equals(handle.NormalizedAlias, requestedAlias.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            return null;
        }
    }
}
