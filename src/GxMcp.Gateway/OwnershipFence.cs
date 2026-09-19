using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>Immutable fence carried by gateway-owned asynchronous state.</summary>
    internal sealed class OwnershipFence
    {
        internal OwnershipFence(string ownerScopeId, string kbId, long generation)
        {
            OwnerScopeId = ownerScopeId ?? string.Empty;
            KbId = kbId ?? string.Empty;
            Generation = generation;
            Epoch = generation;
        }

        internal string OwnerScopeId { get; }
        internal string KbId { get; }
        internal long Generation { get; }
        internal long Epoch { get; }

        internal bool Matches(OwnershipFence? other)
            => other != null
                && string.Equals(OwnerScopeId, other.OwnerScopeId, StringComparison.Ordinal)
                && string.Equals(KbId, other.KbId, StringComparison.Ordinal)
                && Generation == other.Generation
                && Epoch == other.Epoch;

        internal bool Matches(JObject? payload)
            => payload != null
                && string.Equals(OwnerScopeId, payload["ownerScopeId"]?.ToString(), StringComparison.Ordinal)
                && string.Equals(KbId, payload["kbId"]?.ToString(), StringComparison.Ordinal)
                && Generation == (payload["generation"]?.ToObject<long?>() ?? long.MinValue)
                && Epoch == (payload["epoch"]?.ToObject<long?>() ?? long.MinValue);

        internal JObject ToJson()
            => new JObject
            {
                ["ownerScopeId"] = OwnerScopeId,
                ["kbId"] = KbId,
                ["generation"] = Generation,
                ["epoch"] = Epoch
            };
    }
}
