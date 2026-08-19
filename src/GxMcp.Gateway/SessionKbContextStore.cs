using System;
using System.Collections.Concurrent;

namespace GxMcp.Gateway
{
    internal sealed class SessionKbContextStore
    {
        private sealed class Entry
        {
            public string? Alias { get; set; }
            public DateTime LastSeenUtc { get; set; }
        }

        private readonly ConcurrentDictionary<string, Entry> _entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan _idleTimeout;

        public SessionKbContextStore(TimeSpan idleTimeout)
        {
            if (idleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
            _idleTimeout = idleTimeout;
        }

        public string? Get(string sessionId)
        {
            return TryGet(sessionId, out var alias) ? alias : null;
        }

        public bool TryGet(string sessionId, out string? alias)
        {
            alias = null;
            if (string.IsNullOrWhiteSpace(sessionId)) return false;
            CleanupExpired();
            if (!_entries.TryGetValue(sessionId, out var entry)) return false;

            entry.LastSeenUtc = DateTime.UtcNow;
            alias = entry.Alias;
            return true;
        }

        public bool Initialize(string sessionId, string? alias)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session id is required.", nameof(sessionId));
            CleanupExpired();
            return _entries.TryAdd(sessionId, new Entry
            {
                Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim(),
                LastSeenUtc = DateTime.UtcNow
            });
        }

        public void Set(string sessionId, string alias)
        {
            Validate(sessionId, alias);
            _entries[sessionId] = new Entry
            {
                Alias = alias.Trim(),
                LastSeenUtc = DateTime.UtcNow
            };
        }

        public void Clear(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;
            _entries.TryRemove(sessionId, out _);
        }

        private void CleanupExpired()
        {
            DateTime cutoff = DateTime.UtcNow - _idleTimeout;
            foreach (var pair in _entries)
            {
                // stdio is a process-scoped session and has no HTTP idle timeout.
                if (string.Equals(pair.Key, "stdio", StringComparison.OrdinalIgnoreCase)) continue;
                if (pair.Value.LastSeenUtc < cutoff)
                    _entries.TryRemove(pair.Key, out _);
            }
        }

        private static void Validate(string sessionId, string alias)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("Session id is required.", nameof(sessionId));
            if (string.IsNullOrWhiteSpace(alias)) throw new ArgumentException("KB alias is required.", nameof(alias));
        }
    }
}
