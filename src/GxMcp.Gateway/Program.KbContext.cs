using System;

namespace GxMcp.Gateway
{
    partial class Program
    {
        private static readonly object _sessionKbLeaseGate = new object();
        private static readonly TimeSpan SessionKbLeaseTtl = TimeSpan.FromMinutes(10);
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

        internal static string GetSessionLeaseState(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)
                || !_sessionKbContexts.TryGetSnapshot(sessionId, out var snapshot)
                || snapshot?.Lease == null)
                return "none";

            var lease = _kbLeases.Get(snapshot.Lease.Token);
            if (lease == null)
                return "invalid";
            if (!string.Equals(lease.OwnerScopeId, snapshot.OwnerScopeId, StringComparison.Ordinal))
                return "other-owner";
            if (lease.State == KbUseLeaseState.Expired)
                return "expired";
            if (lease.State != KbUseLeaseState.Active)
            {
                return lease.State switch
                {
                    KbUseLeaseState.Revoked => "revoked",
                    KbUseLeaseState.Released => "released",
                    _ => "invalid"
                };
            }
            if (!string.Equals(lease.KbId, snapshot.KbId, StringComparison.Ordinal)
                || lease.ContextGeneration != snapshot.ContextGeneration
                || !string.Equals(lease.Identity, snapshot.Lease.Identity, StringComparison.Ordinal))
                return "invalid";

            return "active";
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
            var lease = _kbLeases.Open(sessionId, canonicalAlias, generation, identity, "session-" + generation, SessionKbLeaseTtl);
            _sessionKbContexts.Set(sessionId, alias, canonicalAlias, lease);
        }

        /// <summary>
        /// Keeps an explicit per-request KB argument from allowing the session
        /// lease to go stale. A matching explicit target renews the existing
        /// lease without changing the selected context generation. A stateful
        /// operation targeting a different or missing context adopts that
        /// explicit target so the normal ownership fence can validate it.
        /// </summary>
        internal static void RefreshSessionLeaseForExplicitKb(
            string sessionId,
            KbHandle resolvedKb,
            bool requiresSessionLease)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || resolvedKb == null)
                return;

            string identity = NormalizeKbIdentity(resolvedKb.Path, resolvedKb.Alias);
            string canonicalAlias = resolvedKb.NormalizedAlias;

            lock (_sessionKbLeaseGate)
            {
                _sessionKbContexts.TryGetSnapshot(sessionId, out var snapshot);
                if (HasMatchingActiveLease(snapshot, canonicalAlias, identity))
                {
                    var renewal = _kbLeases.Renew(
                        snapshot!.Lease!.Token,
                        snapshot.OwnerScopeId,
                        SessionKbLeaseTtl);
                    if (renewal.Status == KbUseLeaseOperationStatus.Success)
                        return;
                }

                if (requiresSessionLease)
                    SetSessionSelectedKb(sessionId, resolvedKb.Alias, identity);
            }
        }

        private static bool HasMatchingActiveLease(
            SessionKbContextStore.Snapshot? snapshot,
            string canonicalAlias,
            string identity)
        {
            if (snapshot?.Lease == null)
                return false;

            var lease = _kbLeases.Get(snapshot.Lease.Token);
            return lease != null
                && lease.State == KbUseLeaseState.Active
                && string.Equals(lease.OwnerScopeId, snapshot.OwnerScopeId, StringComparison.Ordinal)
                && string.Equals(lease.KbId, canonicalAlias, StringComparison.Ordinal)
                && lease.ContextGeneration == snapshot.ContextGeneration
                && string.Equals(lease.Identity, identity, StringComparison.Ordinal);
        }

        private static string NormalizeKbIdentity(string? path, string fallbackAlias)
        {
            string value = string.IsNullOrWhiteSpace(path) ? fallbackAlias : path;
            return value.Trim().TrimEnd('\\', '/').ToLowerInvariant();
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
