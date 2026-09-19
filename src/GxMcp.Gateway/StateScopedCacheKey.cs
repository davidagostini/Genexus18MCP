using System;

namespace GxMcp.Gateway
{
    /// <summary>Explicit identity for process-local cached state.</summary>
    internal readonly struct StateScopedCacheKey : IEquatable<StateScopedCacheKey>
    {
        internal StateScopedCacheKey(StateScopeId stateScopeId, string kbId, long generation, string itemKey)
        {
            StateScopeId = stateScopeId;
            KbId = Normalize(kbId, nameof(kbId));
            Generation = generation;
            ItemKey = itemKey ?? throw new ArgumentNullException(nameof(itemKey));
        }

        internal StateScopeId StateScopeId { get; }
        internal string KbId { get; }
        internal long Generation { get; }
        internal string ItemKey { get; }

        internal static StateScopedCacheKey Create(StateScopeId stateScopeId, string kbId, long generation, string itemKey)
            => new StateScopedCacheKey(stateScopeId, kbId, generation, itemKey);

        // Delimiters are intentionally limited to the envelope prefix; ItemKey may contain '|'.
        public override string ToString()
            => stateScopeIdText() + "|" + KbId + "|gen=" + Generation.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + ItemKey;

        internal static bool TryParse(string value, out StateScopedCacheKey key)
        {
            key = default;
            if (string.IsNullOrWhiteSpace(value)) return false;
            int first = value.IndexOf('|');
            int second = first < 0 ? -1 : value.IndexOf('|', first + 1);
            int third = second < 0 ? -1 : value.IndexOf('|', second + 1);
            if (first <= 0 || second <= first + 1 || third <= second + 1) return false;
            if (!long.TryParse(value.Substring(second + 5, third - second - 5), out long generation)) return false;
            try
            {
                key = new StateScopedCacheKey(
                    StateScopeId.Parse(value.Substring(0, first)),
                    value.Substring(first + 1, second - first - 1),
                    generation,
                    value.Substring(third + 1));
                return true;
            }
            catch (ArgumentException) { return false; }
        }

        private string stateScopeIdText() => StateScopeId.ToString();
        private static string Normalize(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A KB id is required.", name);
            return value.Trim().ToLowerInvariant();
        }

        public bool Equals(StateScopedCacheKey other)
            => StateScopeId == other.StateScopeId && Generation == other.Generation
                && string.Equals(KbId, other.KbId, StringComparison.Ordinal)
                && string.Equals(ItemKey, other.ItemKey, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is StateScopedCacheKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(StateScopeId, KbId, Generation, ItemKey);
    }
}
