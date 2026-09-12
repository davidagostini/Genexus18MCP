using System;

namespace GxMcp.Gateway
{
    partial class Program
    {
        private static readonly SessionKbContextStore _sessionKbContexts =
            new SessionKbContextStore(TimeSpan.FromMinutes(10));

        internal static string? GetConfiguredDefaultKb()
        {
            string? alias = _activeConfig?.Environment?.DefaultKb;
            if (string.IsNullOrWhiteSpace(alias))
                alias = _activeConfig?.Environment?.ActiveKb;
            return string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        }

        internal static bool TryGetSessionSelectedKb(string sessionId, out string? alias)
        {
            return _sessionKbContexts.TryGet(sessionId, out alias);
        }

        internal static string? GetSessionSelectedKb(string sessionId)
        {
            return _sessionKbContexts.Get(sessionId);
        }

        private static void InitializeSessionKbContext(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;
            _sessionKbContexts.Initialize(sessionId, null);
        }

        internal static void SetSessionSelectedKb(string sessionId, string alias)
        {
            _sessionKbContexts.Set(sessionId, alias);
        }

        internal static void SetSessionSelectedKb(string sessionId, string alias, string kbId)
        {
            if (!_sessionKbContexts.TryGetSnapshot(sessionId, out var prior))
                _sessionKbContexts.Initialize(sessionId, null);
            long generation = (prior?.ContextGeneration ?? 0) + 1;
            string identity = (kbId ?? string.Empty).Trim().TrimEnd('\\', '/').ToLowerInvariant();
            string canonicalAlias = CanonicalizeKbAlias(alias);
            var lease = _kbLeases.Open(sessionId, canonicalAlias, generation, identity, "session-" + generation, TimeSpan.FromMinutes(10));
            _sessionKbContexts.Set(sessionId, alias, canonicalAlias, lease);
        }

        internal static string CanonicalizeKbAlias(string alias)
        {
            if (string.IsNullOrWhiteSpace(alias))
                throw new ArgumentException("KB alias is required.", nameof(alias));
            return alias.Trim().ToLowerInvariant();
        }

        internal static void ClearSessionSelectedKb(string sessionId)
        {
            _sessionKbContexts.Clear(sessionId);
        }

        // Test-only seam for validating the lease payload without starting a Worker.
        internal static bool TryGetSessionSnapshotForTest(string sessionId, out SessionKbContextStore.Snapshot? snapshot)
        {
            return _sessionKbContexts.TryGetSnapshot(sessionId, out snapshot);
        }

        internal static IDisposable ConfigureRouteStateForTest(Configuration config, string configPath)
        {
            var previousConfig = _activeConfig;
            var previousResolver = _kbResolver;
            var previousPool = _workerPool;
            var previousConfigPath = Configuration.CurrentConfigPath;
            var previousResolvedFrom = Configuration.ResolvedFrom;

            _activeConfig = config;
            _kbResolver = new KbResolver(config);
            _workerPool = new WorkerPool(config);
            Configuration.SetCurrentConfigPathForTest(configPath);

            return new RouteStateRestore(
                previousConfig, previousResolver, previousPool, previousConfigPath, previousResolvedFrom);
        }

        private sealed class RouteStateRestore : IDisposable
        {
            private readonly Configuration? _config;
            private readonly KbResolver? _resolver;
            private readonly WorkerPool? _pool;
            private readonly string? _configPath;
            private readonly string _resolvedFrom;
            private bool _restored;

            internal RouteStateRestore(
                Configuration? config,
                KbResolver? resolver,
                WorkerPool? pool,
                string? configPath,
                string resolvedFrom)
            {
                _config = config;
                _resolver = resolver;
                _pool = pool;
                _configPath = configPath;
                _resolvedFrom = resolvedFrom;
            }

            public void Dispose()
            {
                if (_restored) return;
                _restored = true;
                _activeConfig = _config;
                _kbResolver = _resolver;
                _workerPool = _pool;
                Configuration.SetCurrentConfigPathForTest(_configPath);
                Configuration.ResolvedFrom = _resolvedFrom;
            }
        }
    }
}
