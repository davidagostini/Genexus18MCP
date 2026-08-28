using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace GxMcp.Gateway
{
    internal sealed class HttpSessionRegistry
    {
        private readonly ConcurrentDictionary<string, HttpSessionState> _sessions = new ConcurrentDictionary<string, HttpSessionState>(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan _sessionIdleTimeout;
        private readonly int _maxQueuedMessagesPerSession;

        public HttpSessionRegistry(TimeSpan sessionIdleTimeout, int maxQueuedMessagesPerSession = 128)
        {
            _sessionIdleTimeout = sessionIdleTimeout;
            _maxQueuedMessagesPerSession = maxQueuedMessagesPerSession;
        }

        public HttpSessionState Create()
        {
            CleanupExpired();

            var session = new HttpSessionState
            {
                Id = Guid.NewGuid().ToString("N"),
                CreatedUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            };

            _sessions[session.Id] = session;
            return session;
        }

        public bool TryGet(string sessionId, out HttpSessionState? session)
        {
            session = null;
            if (string.IsNullOrWhiteSpace(sessionId)) return false;

            if (!_sessions.TryGetValue(sessionId, out var found)) return false;
            if (IsExpired(found))
            {
                _sessions.TryRemove(sessionId, out _);
                return false;
            }

            found.LastSeenUtc = DateTime.UtcNow;
            session = found;
            return true;
        }

        public bool Remove(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return false;
            return _sessions.TryRemove(sessionId, out _);
        }

        public IReadOnlyCollection<HttpSessionState> ActiveSessions
        {
            get
            {
                CleanupExpired();
                return _sessions.Values.ToArray();
            }
        }

        public void Enqueue(HttpSessionState session, string payload)
        {
            lock (session.PendingMessages)
            {
                session.PendingMessages.Enqueue(payload);
                while (session.PendingMessages.Count > _maxQueuedMessagesPerSession)
                {
                    session.PendingMessages.Dequeue();
                }
            }
        }

        public int CleanupExpired()
        {
            int removed = 0;
            foreach (var pair in _sessions.ToArray())
            {
                if (IsExpired(pair.Value) && _sessions.TryRemove(pair.Key, out _))
                {
                    removed++;
                }
            }

            return removed;
        }

        private bool IsExpired(HttpSessionState session)
        {
            return (DateTime.UtcNow - session.LastSeenUtc) > _sessionIdleTimeout;
        }
    }

    internal sealed class HttpSessionState
    {
        public string Id { get; set; } = "";
        public string ProtocolVersion { get; set; } = McpRouter.SupportedProtocolVersion;
        public DateTime CreatedUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public string? ActiveKbAlias { get; set; }
        public Queue<string> PendingMessages { get; } = new Queue<string>();
    }
}
