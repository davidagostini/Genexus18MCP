using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Bounded, TTL-aware store backing the gateway's semantic cache.
    /// The gateway process is long-lived (stdio EOF keeps it alive via Task.Delay(-1)),
    /// so an unbounded ConcurrentDictionary grows forever across read-only sessions.
    /// Entries expire after <see cref="TtlMinutes"/> without access (lazy sweep on Set)
    /// and the store evicts least-recently-accessed entries beyond <see cref="MaxEntries"/>.
    /// </summary>
    internal sealed class SemanticCacheStore
    {
        // Idle time after which an entry is considered stale. Swept lazily on every Set,
        // so no background timer is needed — a hot entry never expires mid-session.
        internal const int TtlMinutes = 30;
        private const int DefaultMaxEntries = 256;
        private const string MaxEntriesEnvVar = "GXMCP_SEMANTIC_CACHE_MAX";

        private readonly ConcurrentDictionary<string, JObject> _entries = new ConcurrentDictionary<string, JObject>();
        // Last-access timestamp per key, driven by NextStamp(). Kept separate so
        // the cached envelope itself stays a plain JObject.
        private readonly ConcurrentDictionary<string, long> _lastAccess = new ConcurrentDictionary<string, long>();
        private readonly int _maxEntries;
        private readonly TimeSpan _ttl;
        // Logical clock: TickCount64 alone has 1ms resolution, so several accesses
        // can land on the same tick and leave LRU order ambiguous. Stamps only
        // move forward — a bumped stamp is still fresh enough for TTL checks.
        private long _lastStamp;

        public SemanticCacheStore()
            : this(ResolveMaxEntriesFromEnv(), TimeSpan.FromMinutes(TtlMinutes))
        {
        }

        // Test seam: lets unit tests drive eviction/TTL deterministically.
        internal SemanticCacheStore(int maxEntries, TimeSpan ttl)
        {
            _maxEntries = maxEntries > 0 ? maxEntries : DefaultMaxEntries;
            _ttl = ttl;
        }

        public int MaxEntries => _maxEntries;

        public bool TryGet(string key, out JObject value)
        {
            value = null!;
            if (!_entries.TryGetValue(key, out var found)) return false;

            if (IsExpired(key))
            {
                RemoveEntry(key);
                return false;
            }

            // Touch on hit: LRU-style recency drives both expiry and cap eviction.
            _lastAccess[key] = NextStamp();
            value = found;
            return true;
        }

        public void Set(string key, JObject value)
        {
            // Opportunistic maintenance: expire stale entries first so they don't
            // consume capacity that would otherwise force live entries out.
            SweepExpired();

            _entries[key] = value;
            _lastAccess[key] = NextStamp();

            EvictBeyondCap();
        }

        public void Clear()
        {
            _entries.Clear();
            _lastAccess.Clear();
        }

        private bool IsExpired(string key)
        {
            if (!_lastAccess.TryGetValue(key, out var lastSeen)) return true;
            return TimeSpan.FromMilliseconds(Environment.TickCount64 - lastSeen) > _ttl;
        }

        /// <summary>
        /// Monotonic access stamp: real TickCount64 when it advanced, otherwise the
        /// previous stamp + 1, so every access gets a strictly greater value even
        /// within the same millisecond (LRU eviction needs a total order).
        /// </summary>
        private long NextStamp()
        {
            long ticks = Environment.TickCount64;
            long prev = Interlocked.Read(ref _lastStamp);
            while (true)
            {
                long next = ticks > prev ? ticks : prev + 1;
                long seen = Interlocked.CompareExchange(ref _lastStamp, next, prev);
                if (seen == prev) return next;
                prev = seen;
            }
        }

        private void SweepExpired()
        {
            foreach (var key in _lastAccess.Keys.ToArray())
            {
                if (_entries.ContainsKey(key) && IsExpired(key))
                {
                    RemoveEntry(key);
                }
            }
        }

        private void EvictBeyondCap()
        {
            while (_entries.Count > _maxEntries)
            {
                // Least-recently-accessed victim. ToArray snapshot: concurrent writers
                // may race, but the loop re-checks Count so we never over-evict.
                string? oldestKey = _lastAccess.ToArray()
                    .OrderBy(pair => pair.Value)
                    .Select(pair => pair.Key)
                    .FirstOrDefault(candidate => _entries.ContainsKey(candidate));

                if (oldestKey == null || !RemoveEntry(oldestKey))
                {
                    break;
                }
            }
        }

        private bool RemoveEntry(string key)
        {
            _lastAccess.TryRemove(key, out _);
            return _entries.TryRemove(key, out _);
        }

        private static int ResolveMaxEntriesFromEnv()
        {
            var raw = Environment.GetEnvironmentVariable(MaxEntriesEnvVar);
            if (!int.TryParse(raw, out int parsed) || parsed <= 0) return DefaultMaxEntries;
            return parsed;
        }
    }
}
