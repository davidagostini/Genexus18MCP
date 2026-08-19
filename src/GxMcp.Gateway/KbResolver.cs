using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GxMcp.Gateway
{
    public sealed class KbResolutionException : Exception
    {
        public string Code { get; }
        public KbResolutionException(string code, string message) : base(message) { Code = code; }
    }

    public sealed class KbResolver
    {
        private readonly Configuration _config;

        public KbResolver(Configuration config) { _config = config; }

        public KbHandle Resolve(string? kbArg, IReadOnlyCollection<KbHandle> openKbs)
            => Resolve(kbArg, openKbs, null);

        // issue #26 P3: `knownKbs` (optional) is the durable set of aliases the user has
        // opened this session — it survives worker recycles, unlike `openKbs`. An explicit
        // alias is matched against declared → open → known → path, so a KB whose worker is
        // momentarily down stays resolvable instead of failing with "Unknown KB". The
        // Empty-arg resolution uses the configured default first, then the live worker
        // set for the single-open fallback; known workers only fill an explicitly
        // selected default whose process is currently down.
        public KbHandle Resolve(string? kbArg, IReadOnlyCollection<KbHandle> openKbs, IReadOnlyCollection<KbHandle>? knownKbs)
            => Resolve(kbArg, openKbs, knownKbs, null);

        public KbHandle Resolve(
            string? kbArg,
            IReadOnlyCollection<KbHandle> openKbs,
            IReadOnlyCollection<KbHandle>? knownKbs,
            string? sessionDefaultAlias)
            => Resolve(kbArg, openKbs, knownKbs, sessionDefaultAlias, sessionContextInitialized: false);

        public KbHandle Resolve(
            string? kbArg,
            IReadOnlyCollection<KbHandle> openKbs,
            IReadOnlyCollection<KbHandle>? knownKbs,
            string? sessionDefaultAlias,
            bool sessionContextInitialized)
        {
            if (!string.IsNullOrWhiteSpace(kbArg))
            {
                return ResolveExplicit(kbArg!, openKbs, knownKbs, fromSession: false);
            }

            if (!string.IsNullOrWhiteSpace(sessionDefaultAlias))
                return ResolveExplicit(sessionDefaultAlias!, openKbs, knownKbs, fromSession: true);

            if (sessionContextInitialized)
                return ResolveWithoutConfiguredDefault(openKbs);

            // An explicit default/active selection is the safe implicit target
            // when several workers are alive. Without this branch, set_default
            // only changed metadata while every omitted kb argument still failed
            // with KB_AMBIGUOUS.
            string? configuredDefault = _config.Environment?.DefaultKb;
            if (string.IsNullOrWhiteSpace(configuredDefault))
                configuredDefault = _config.Environment?.ActiveKb;

            if (!string.IsNullOrWhiteSpace(configuredDefault))
            {
                var openDefault = openKbs.FirstOrDefault(
                    k => string.Equals(k.Alias, configuredDefault, StringComparison.OrdinalIgnoreCase));
                if (openDefault != null) return openDefault;

                // Preserve the original single-open-KB behavior: an ad-hoc
                // `open` remains usable when the persisted default points at a
                // different, not-yet-open worker.
                if (openKbs.Count == 1) return openKbs.First();

                var declaredDefault = _config.Environment?.KBs?.FirstOrDefault(
                    k => string.Equals(k.Alias, configuredDefault, StringComparison.OrdinalIgnoreCase));
                if (declaredDefault != null)
                    return new KbHandle(declaredDefault.Alias, declaredDefault.Path);

                var knownDefault = knownKbs?.FirstOrDefault(
                    k => string.Equals(k.Alias, configuredDefault, StringComparison.OrdinalIgnoreCase));
                if (knownDefault != null) return knownDefault;

                throw new KbResolutionException("KB_NOT_FOUND",
                    $"Configured default KB '{configuredDefault}' is not declared, open, or known in this session.");
            }

            if (openKbs.Count == 1) return openKbs.First();
            if (openKbs.Count == 0)
            {
                // No default and no open KBs: fall back to first declared KB if any.
                var first = _config.Environment?.KBs?.FirstOrDefault();
                if (first != null) return new KbHandle(first.Alias, first.Path);

                throw new KbResolutionException("KB_AMBIGUOUS",
                    "No 'kb' parameter, no DefaultKb configured, and no KB currently open.");
            }

            throw new KbResolutionException("KB_AMBIGUOUS",
                $"Multiple KBs open ({string.Join(",", openKbs.Select(k => k.Alias))}); 'kb' parameter is required.");
        }

        private KbHandle ResolveWithoutConfiguredDefault(IReadOnlyCollection<KbHandle> openKbs)
        {
            if (openKbs.Count == 1) return openKbs.First();
            if (openKbs.Count == 0)
            {
                throw new KbResolutionException("KB_AMBIGUOUS",
                    "No KB is selected in this MCP session and no KB is currently open. Set a session default or pass 'kb'.");
            }

            throw new KbResolutionException("KB_AMBIGUOUS",
                $"Multiple KBs open ({string.Join(",", openKbs.Select(k => k.Alias))}); select a session default or pass 'kb'.");
        }

        private KbHandle ResolveExplicit(
            string kbArg,
            IReadOnlyCollection<KbHandle> openKbs,
            IReadOnlyCollection<KbHandle>? knownKbs,
            bool fromSession)
        {
            var declared = _config.Environment?.KBs?.FirstOrDefault(
                k => string.Equals(k.Alias, kbArg, StringComparison.OrdinalIgnoreCase));
            if (declared != null) return new KbHandle(declared.Alias, declared.Path);

            var openMatch = openKbs.FirstOrDefault(
                k => string.Equals(k.Alias, kbArg, StringComparison.OrdinalIgnoreCase));
            if (openMatch != null) return openMatch;

            // issue #26 P3: fall back to the durable known set (survives worker recycle).
            if (knownKbs != null)
            {
                var knownMatch = knownKbs.FirstOrDefault(
                    k => string.Equals(k.Alias, kbArg, StringComparison.OrdinalIgnoreCase));
                if (knownMatch != null) return knownMatch;
            }

            if (Path.IsPathRooted(kbArg) && Directory.Exists(kbArg))
            {
                string alias = Path.GetFileName(kbArg.TrimEnd('\\', '/')).ToLowerInvariant();
                if (string.IsNullOrEmpty(alias)) alias = "adhoc";
                return new KbHandle(alias, kbArg);
            }

            string source = fromSession ? "Session-selected KB" : "Unknown KB";
            throw new KbResolutionException("KB_NOT_FOUND",
                $"{source} '{kbArg}' is not declared, open, or known. Declare an alias in config.Environment.KBs[] or pass an absolute path to an existing directory.");
        }
    }
}
