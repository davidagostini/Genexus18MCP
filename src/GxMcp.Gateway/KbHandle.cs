using System;

namespace GxMcp.Gateway
{
    /// <summary>
    /// v2.6.6 Stream H (FR#26) — cached metadata handle.
    ///
    /// Historically <c>KbHandle</c> was an immutable record containing just
    /// <c>Alias</c> + <c>Path</c>. Every lifecycle call that needed the active
    /// environment burnt 50-200ms on a worker round-trip via the SDK
    /// (<c>KnowledgeBase.UserInterface</c> / <c>ActiveModel</c>). The IDE keeps
    /// this value in memory; the gateway now mirrors that behaviour with a
    /// 60s TTL + explicit <see cref="Invalidate"/> hook used by
    /// <c>KbWatcherService.OnEnvironmentChanged</c>.
    ///
    /// Correctness &gt; raw perf: a stale cached environment can ship a build
    /// against the wrong target, so any worker-side environment switch must
    /// call <see cref="Invalidate"/> (or the cached entry must expire on its
    /// own) before the next consumer reads it.
    /// </summary>
    public sealed class KbHandle : IEquatable<KbHandle>
    {
        public string Alias { get; }
        public string Path { get; }

        public KbHandle(string alias, string path)
        {
            Alias = alias;
            Path = path;
        }

        public string NormalizedAlias => Alias.Trim().ToLowerInvariant();

        // 60-second TTL — matches the documented expectation in Stream H.
        // Tests can lower this via Invalidate() instead of waiting.
        public static TimeSpan DefaultTtl { get; } = TimeSpan.FromSeconds(60);

        private readonly object _envLock = new object();
        private string? _cachedEnv;
        private string? _cachedEnvVersion;
        private DateTime _cachedAtUtc = DateTime.MinValue;
        private Func<(string env, string version)>? _envFetcher;
        private TimeSpan _ttl = DefaultTtl;

        /// <summary>
        /// Wires the worker round-trip used on cache miss. The gateway sets this
        /// once after acquiring the worker; tests can inject a synchronous fake.
        /// </summary>
        public void ConfigureEnvFetcher(Func<(string env, string version)> fetcher, TimeSpan? ttl = null)
        {
            lock (_envLock)
            {
                _envFetcher = fetcher;
                if (ttl.HasValue) _ttl = ttl.Value;
            }
        }

        /// <summary>
        /// Cache-hit on read within TTL; cache-miss pays one worker round-trip.
        /// Returns <c>null</c> when no fetcher has been wired (degraded mode).
        /// </summary>
        public string? ActiveEnvironment
        {
            get
            {
                EnsureFresh();
                return _cachedEnv;
            }
        }

        public string? ActiveEnvironmentVersion
        {
            get
            {
                EnsureFresh();
                return _cachedEnvVersion;
            }
        }

        /// <summary>Returns whether the next read would hit the cache (within TTL).</summary>
        public bool IsEnvCacheFresh
        {
            get
            {
                lock (_envLock)
                {
                    return _cachedAtUtc != DateTime.MinValue
                        && (DateTime.UtcNow - _cachedAtUtc) < _ttl;
                }
            }
        }

        /// <summary>Force re-fetch on the next read. Called from KbWatcherService.</summary>
        public void Invalidate()
        {
            lock (_envLock)
            {
                _cachedAtUtc = DateTime.MinValue;
            }
        }

        private void EnsureFresh()
        {
            Func<(string env, string version)>? fetcher;
            lock (_envLock)
            {
                if (_cachedAtUtc != DateTime.MinValue && (DateTime.UtcNow - _cachedAtUtc) < _ttl)
                    return;
                fetcher = _envFetcher;
            }

            if (fetcher == null) return;

            (string env, string version) result;
            try { result = fetcher(); }
            catch { return; }

            lock (_envLock)
            {
                _cachedEnv = result.env;
                _cachedEnvVersion = result.version;
                _cachedAtUtc = DateTime.UtcNow;
            }
        }

        // Record-style equality preserved so existing call sites comparing two
        // KbHandle values keep working. Compare on Alias only (the path may
        // differ in case/trailing slash for the same logical KB).
        public bool Equals(KbHandle? other) =>
            other != null && string.Equals(NormalizedAlias, other.NormalizedAlias, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as KbHandle);

        public override int GetHashCode() =>
            NormalizedAlias?.GetHashCode() ?? 0;

        public override string ToString() => $"KbHandle {{ Alias = {Alias}, Path = {Path} }}";
    }
}
