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
            => Resolve(kbArg, openKbs, null, null, out _);

        // issue #26 P3: `knownKbs` (optional) is the durable set of aliases the user has
        // opened this session — it survives worker recycles, unlike `openKbs`.
        public KbHandle Resolve(string? kbArg, IReadOnlyCollection<KbHandle> openKbs, IReadOnlyCollection<KbHandle>? knownKbs)
            => Resolve(kbArg, openKbs, knownKbs, null, out _);

        public KbHandle Resolve(
            string? kbArg,
            IReadOnlyCollection<KbHandle> openKbs,
            IReadOnlyCollection<KbHandle>? knownKbs,
            string? sessionDefaultAlias)
            => Resolve(kbArg, openKbs, knownKbs, sessionDefaultAlias, out _);

        public KbHandle Resolve(
            string? kbArg,
            IReadOnlyCollection<KbHandle> openKbs,
            IReadOnlyCollection<KbHandle>? knownKbs,
            string? sessionDefaultAlias,
            bool sessionContextInitialized)
            => Resolve(kbArg, openKbs, knownKbs, sessionDefaultAlias, out _);

        public KbHandle Resolve(
            string? kbArg,
            IReadOnlyCollection<KbHandle> openKbs,
            IReadOnlyCollection<KbHandle>? knownKbs,
            string? sessionDefaultAlias,
            out string selectionSource)
        {
            if (!string.IsNullOrWhiteSpace(kbArg))
            {
                selectionSource = "explicit-arg";
                return ResolveExplicit(kbArg!, openKbs, knownKbs, fromSession: false);
            }

            if (!string.IsNullOrWhiteSpace(sessionDefaultAlias))
            {
                var handle = ResolveExplicit(sessionDefaultAlias!, openKbs, knownKbs, fromSession: true);
                selectionSource = "session-select";
                return handle;
            }

            bool isStrict = !string.Equals(_config.Environment?.ResolutionPolicy, "legacy", StringComparison.OrdinalIgnoreCase);

            if (isStrict)
            {
                // Strict mode (Issue #146):
                // 1. DefaultKb / ActiveKb in config do NOT auto-seed sessions.
                // 2. Exactly 1 KB open without conflicting configured default -> resolve as single-open.
                // 3. Exactly 1 KB open (B) but configured default is A (A != B) -> KB_CONTEXT_REQUIRED (DefaultConflict).
                // 4. >1 KBs open without selection -> KB_AMBIGUOUS.
                // 5. 0 KBs open -> never auto-open declared KB; KB_CONTEXT_REQUIRED (<=1 declared) or KB_AMBIGUOUS (>1 declared).
                if (openKbs.Count == 1)
                {
                    var sole = openKbs.First();
                    string? configuredDefault = _config.Environment?.RawDefaultKb ?? _config.Environment?.DefaultKb;
                    if (!string.IsNullOrWhiteSpace(configuredDefault) && !string.Equals(configuredDefault, sole.Alias, StringComparison.OrdinalIgnoreCase))
                    {
                        selectionSource = "none";
                        throw new KbResolutionException("KB_CONTEXT_REQUIRED",
                            $"DefaultConflict: single open KB '{sole.Alias}' conflicts with configured default '{configuredDefault}'. Explicit KB context is required for this session.");
                    }
                    selectionSource = "single-open";
                    return sole;
                }

                if (openKbs.Count > 1)
                {
                    selectionSource = "none";
                    throw new KbResolutionException("KB_AMBIGUOUS",
                        $"Multiple KBs open ({string.Join(",", openKbs.Select(k => k.Alias))}); 'kb' parameter is required.");
                }

                // openKbs.Count == 0
                selectionSource = "none";
                var declared = _config.Environment?.KBs ?? new List<KbEntry>();
                if (declared.Count <= 1)
                {
                    throw new KbResolutionException("KB_CONTEXT_REQUIRED",
                        "No Knowledge Base is open. Open a KB with 'genexus_kb action=open' or pass 'kb'.");
                }
                else
                {
                    var aliases = string.Join(", ", declared.Select(k => k.Alias));
                    throw new KbResolutionException("KB_AMBIGUOUS",
                        $"Multiple Knowledge Bases are declared ({aliases}); no KB is currently open. Pass 'kb' or open a KB with 'genexus_kb action=open'.");
                }
            }

            // Legacy mode (ResolutionPolicy == "legacy"):
            string? legacyDefault = _config.Environment?.DefaultKb;
            if (string.IsNullOrWhiteSpace(legacyDefault))
                legacyDefault = _config.Environment?.ActiveKb;

            if (!string.IsNullOrWhiteSpace(legacyDefault))
            {
                var openDefault = openKbs.FirstOrDefault(
                    k => string.Equals(k.Alias, legacyDefault, StringComparison.OrdinalIgnoreCase));
                if (openDefault != null)
                {
                    selectionSource = "config-default";
                    return openDefault;
                }

                if (openKbs.Count == 1)
                {
                    selectionSource = "single-open";
                    return openKbs.First();
                }

                var declaredDefault = _config.Environment?.KBs?.FirstOrDefault(
                    k => string.Equals(k.Alias, legacyDefault, StringComparison.OrdinalIgnoreCase));
                if (declaredDefault != null)
                {
                    selectionSource = "config-default";
                    return KbHandle.FromEntry(declaredDefault);
                }

                var knownDefault = knownKbs?.FirstOrDefault(
                    k => string.Equals(k.Alias, legacyDefault, StringComparison.OrdinalIgnoreCase));
                if (knownDefault != null)
                {
                    selectionSource = "config-default";
                    return knownDefault;
                }

                selectionSource = "none";
                throw new KbResolutionException("KB_NOT_FOUND",
                    $"Configured default KB '{legacyDefault}' is not declared, open, or known in this session.");
            }

            if (openKbs.Count == 1)
            {
                selectionSource = "single-open";
                return openKbs.First();
            }

            if (openKbs.Count == 0)
            {
                var first = _config.Environment?.KBs?.FirstOrDefault();
                if (first != null)
                {
                    selectionSource = "declared-first";
                    return KbHandle.FromEntry(first);
                }

                selectionSource = "none";
                throw new KbResolutionException("KB_AMBIGUOUS",
                    "No 'kb' parameter, no DefaultKb configured, and no KB currently open.");
            }

            selectionSource = "none";
            throw new KbResolutionException("KB_AMBIGUOUS",
                $"Multiple KBs open ({string.Join(",", openKbs.Select(k => k.Alias))}); 'kb' parameter is required.");
        }

        private KbHandle ResolveExplicit(
            string kbArg,
            IReadOnlyCollection<KbHandle> openKbs,
            IReadOnlyCollection<KbHandle>? knownKbs,
            bool fromSession)
        {
            var declared = _config.Environment?.KBs?.FirstOrDefault(
                k => string.Equals(k.Alias, kbArg, StringComparison.OrdinalIgnoreCase));
            if (declared != null) return KbHandle.FromEntry(declared);

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

            if (fromSession)
            {
                throw new KbResolutionException("KB_SELECTION_INVALID",
                    $"Session-selected KB '{kbArg}' is not declared, open, or known. Declare an alias in config.Environment.KBs[] or pass an absolute path to an existing directory.");
            }

            throw new KbResolutionException("KB_NOT_FOUND",
                $"Unknown KB '{kbArg}' is not declared, open, or known. Declare an alias in config.Environment.KBs[] or pass an absolute path to an existing directory.");
        }
    }
}
