using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Gateway-side auto-injection of the <c>type</c> argument when the LLM omits it
    /// but <c>name</c> resolves to a unique object in the cached index.
    ///
    /// Mutates <paramref name="arguments"/> in-place (adds <c>arguments["type"]</c>) and
    /// returns <c>true</c> when injection succeeded.  Returns <c>false</c> — no mutation —
    /// when the name is ambiguous, unknown, or the tool doesn't accept a <c>type</c> field.
    /// </summary>
    internal static class AutoTypeInjector
    {
        // ── Tools that are exempt from injection ─────────────────────────────
        // Non-object-targeted tools that happen to have a 'name' param but
        // where 'type' means something different or doesn't exist at all.
        private static readonly HashSet<string> _skipTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "genexus_kb",
            "genexus_whoami",
            "genexus_lifecycle",
            "genexus_orient",
            "genexus_doctor",
            "genexus_worker_reload",
            "genexus_worker_pool",
            "genexus_recipe",
            "genexus_telemetry",
            "genexus_security",
            "genexus_format",
            "genexus_github",
            "genexus_ai_complete",
        };

        // ── Types that must never be auto-injected ───────────────────────────
        // Physical shadows / model artifacts of source-bearing objects — types
        // whose index entries exist in the design model but expose no
        // Source/Rules/Events part (Injecting them makes object tools
        // (genexus_edit/read/analyze/...) resolve to a part-less object —
        // PatchReadFailed: "The object does not expose the requested part.").
        //
        //  - "Table": every Transaction generates a Table model object; the
        //    design model indexes BOTH under the same name. The full-map
        //    collapse ({Transaction,Table} → Transaction) handles the paired
        //    case; the guard catches Table-only names.
        //  - "Attribute": a Transaction's attributes are indexed as physical
        //    "Attribute" model objects under their own (usually unique) names
        //    — confirmed live: a type-less genexus_edit on "GpBaseId"
        //    auto-injected type=Attribute and resolved to the part-less
        //    artifact. IndexCacheService treats Attribute/Table identically as
        //    physical targets (reference scan → AddTableCow).
        //
        // Skipping leaves the call type-less; the worker's global name
        // resolution then picks the source-bearing object. Callers that
        // genuinely target a Table/Attribute pass the type explicitly (an
        // explicit type is never overridden). Deliberately NOT here: "Domain"
        // is a real source-bearing object type — its one observed collision
        // (Geolocation: Domain + ExternalObject) is genuine ambiguity, already
        // handled by the map's ambiguous→null rule.
        private static readonly HashSet<string> _shadowTypesNoInject =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Table",
                "Attribute",
            };

        // ── Name → type lookup cache, populated from index snapshots ─────────
        // Plan 038: keyed by normalized KB alias (outer) so two KBs open in the same
        // gateway can't leak each other's name→type resolutions (e.g. "Customer" =
        // Transaction in KB-A, Business Component in KB-B). Inner map:
        // Key: object name (lower-invariant).
        // Value: the single type string when the name is unique, OR null when
        //        multiple objects share the same name (ambiguous).
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string?>> _nameLookupByKb =
            new ConcurrentDictionary<string, ConcurrentDictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);

        // Tool → inputSchema cache: does this tool declare a "type" property?
        // NOT KB-specific (a tool's schema is the same regardless of which KB is
        // open) — stays global, unlike _nameLookupByKb above.
        private static readonly ConcurrentDictionary<string, bool> _toolHasTypeCache =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Normalizes a KB alias the same way KbHandle.NormalizedAlias / _databaseInfoByKb
        // do, so callers can pass whatever casing they have on hand.
        private static string NormalizeAlias(string? kbAlias) =>
            string.IsNullOrWhiteSpace(kbAlias) ? string.Empty : kbAlias.Trim().ToLowerInvariant();

        private static ConcurrentDictionary<string, string?> GetOrCreateKbMap(string? kbAlias) =>
            _nameLookupByKb.GetOrAdd(
                NormalizeAlias(kbAlias),
                _ => new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Attempt to auto-inject <c>arguments["type"]</c> for <paramref name="toolName"/>,
        /// resolving the name against <paramref name="kbAlias"/>'s own name→type map.
        /// Returns <c>true</c> and sets <paramref name="injectedType"/> when injection occurs.
        /// </summary>
        public static bool TryInject(string kbAlias, string toolName, JObject? arguments, out string injectedType)
        {
            injectedType = null!;

            if (arguments == null) return false;

            // Already has a type? Don't override caller.
            var suppliedType = arguments["type"];
            if (suppliedType != null && suppliedType.Type != JTokenType.Null)
                return false;

            // Is this tool exempt?
            if (_skipTools.Contains(toolName))
                return false;

            // Does the tool's schema even declare a 'type' field?
            if (!ToolAcceptsTypeArg(toolName))
                return false;

            // Does the call have a 'name'?
            string? name = arguments["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
                return false;

            // Unique lookup, scoped to this KB only.
            var nameLookup = GetOrCreateKbMap(kbAlias);
            if (!nameLookup.TryGetValue(name, out string? resolvedType))
                return false;           // name not in this KB's index cache

            if (resolvedType == null)
                return false;           // ambiguous (multiple objects with this name)

            // Never inject a physical shadow type (see _shadowTypesNoInject) —
            // it would resolve object tools to a part-less shadow object.
            if (_shadowTypesNoInject.Contains(resolvedType))
                return false;

            // Inject!
            arguments["type"] = resolvedType;
            injectedType = resolvedType;
            return true;
        }

        /// <summary>
        /// Refresh <paramref name="kbAlias"/>'s name→type lookup from a <c>RecentlyChanged</c>
        /// JArray coming from <see cref="IndexStateSnapshot.RecentlyChanged"/>.
        /// Each element is expected to have at least <c>Name</c> and <c>Type</c> (or
        /// lower-case equivalents) fields.
        /// Call this whenever a fresh index snapshot arrives for that KB.
        /// </summary>
        public static void RefreshFromRecentlyChanged(string kbAlias, JArray? recentlyChanged)
        {
            if (recentlyChanged == null) return;

            var nameLookup = GetOrCreateKbMap(kbAlias);
            foreach (var token in recentlyChanged)
            {
                if (token is not JObject obj) continue;
                string? name = obj["Name"]?.ToString() ?? obj["name"]?.ToString();
                string? type = obj["Type"]?.ToString() ?? obj["type"]?.ToString();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type))
                    continue;

                // The recent-window feed must never poison the full-map resolution: a
                // Table shadow surfacing here for a name that ApplyFullNameTypeMap
                // collapsed to "Transaction" would flip it to ambiguous (null), silently
                // undoing the root-cause fix. Shadow types are resolved by the full map
                // and refused by the inject guard anyway, so never feed them from the
                // window.
                if (_shadowTypesNoInject.Contains(type))
                    continue;

                nameLookup.AddOrUpdate(
                    name,
                    addValue: type,
                    updateValueFactory: (_, existing) =>
                        // If existing type differs, mark as ambiguous (null)
                        existing != null && !string.Equals(existing, type, StringComparison.OrdinalIgnoreCase)
                            ? null
                            : type);
            }
        }

        /// <summary>
        /// Root-cause fix: replace <paramref name="kbAlias"/>'s entire name→type map
        /// with the FULL index name→[types] map (as produced by the worker's
        /// <c>kb/GetNameTypeMap</c> action). Unlike <see cref="RefreshFromRecentlyChanged"/>
        /// — which only ever sees the top-5 RecentlyChanged window and therefore cannot
        /// establish real uniqueness — this sees every object of every type, so a name
        /// whose types are exactly { Transaction, Table } is the Transaction (the Table
        /// is its physical shadow) and resolves to "Transaction"; a name with 2+ distinct
        /// source-bearing types is marked ambiguous (null) and won't be injected.
        /// The map is rebuilt wholesale: names that were only present in the recent
        /// window and are not in the full index disappear.
        /// </summary>
        public static void ApplyFullNameTypeMap(string kbAlias, JObject? nameTypeMap)
        {
            var nameLookup = GetOrCreateKbMap(kbAlias);
            // Null map = no-op, NOT a wipe: clearing here would discard the
            // recentlyChanged-primed fallback on a failed fetch.
            if (nameTypeMap == null) return;
            nameLookup.Clear();

            foreach (var prop in nameTypeMap.Properties())
            {
                string name = prop.Name;
                if (prop.Value is not JArray types || types.Count == 0) continue;

                // Distinct types under this name (case-insensitive).
                var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in types)
                {
                    string? ts = t?.ToString();
                    if (!string.IsNullOrWhiteSpace(ts)) distinct.Add(ts!);
                }
                if (distinct.Count == 0) continue;

                // Shadow resolution: a Transaction's physical Table is not a second
                // meaning — collapse { Transaction, Table } to Transaction. A name that
                // is ONLY a Table stays "Table" in the map and is then refused by the
                // _shadowTypesNoInject guard in TryInject (left type-less, so the
                // worker's global resolution picks the object).
                if (distinct.Contains("Transaction"))
                    distinct.Remove("Table");

                nameLookup[name] = distinct.Count == 1
                    ? distinct.First()
                    : null; // genuinely ambiguous (2+ source-bearing types)
            }
        }

        /// <summary>
        /// v2.8.0 (S1) — used by MCP `completion/complete` to autocomplete
        /// object names from a partial prefix, scoped to <paramref name="kbAlias"/>.
        /// Returns up to <paramref name="cap"/> matches that start with the prefix
        /// (case-insensitive). Empty when the index hasn't warmed yet.
        /// </summary>
        public static IEnumerable<string> CompleteName(string kbAlias, string prefix, int cap = 25)
        {
            if (cap <= 0) yield break;
            prefix = prefix ?? string.Empty;
            int yielded = 0;
            foreach (var kv in GetOrCreateKbMap(kbAlias))
            {
                if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    yield return kv.Key;
                    if (++yielded >= cap) yield break;
                }
            }
        }

        // ── Test helpers ──────────────────────────────────────────────────────

        /// <summary>Test-only: prime <paramref name="kbAlias"/>'s name→type map directly.</summary>
        internal static void PrimeIndex(string kbAlias, IEnumerable<(string name, string? type)> entries)
        {
            var nameLookup = GetOrCreateKbMap(kbAlias);
            nameLookup.Clear();
            foreach (var (name, type) in entries)
                nameLookup[name] = type;
        }

        /// <summary>
        /// Clear internal state. With <paramref name="kbAlias"/> omitted, wipes every
        /// KB's name→type map plus the (global, non-KB-specific) tool-schema cache.
        /// With an alias, clears only that KB's inner map and leaves other KBs untouched.
        /// </summary>
        internal static void ClearAll(string? kbAlias = null)
        {
            if (kbAlias == null)
            {
                _nameLookupByKb.Clear();
                _toolHasTypeCache.Clear();
            }
            else
            {
                _nameLookupByKb.TryRemove(NormalizeAlias(kbAlias), out _);
            }
        }

        /// <summary>Test-only: tell the injector whether a tool accepts a 'type' arg.</summary>
        internal static void PrimeToolAcceptsType(string toolName, bool accepts)
        {
            _toolHasTypeCache[toolName] = accepts;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static bool ToolAcceptsTypeArg(string toolName)
        {
            if (_toolHasTypeCache.TryGetValue(toolName, out bool cached))
                return cached;

            // Fall back to reading the tool_definitions.json schema via GatewayArgsValidator's
            // internal mechanism.  We call GatewayArgsValidator.Validate with a dummy args that
            // only has 'type' present so we can detect whether the schema declares the property.
            // A lighter approach: just check if "type" is in the schema properties.
            bool result = SchemaDeclaresTypeProperty(toolName);
            _toolHasTypeCache[toolName] = result;
            return result;
        }

        private static bool SchemaDeclaresTypeProperty(string toolName)
        {
            // We reuse GatewayArgsValidator's internal schema loader via the public Validate
            // path: if a JObject with only { "type": "dummy" } passes validation without a
            // violation for "type", then the property is known.  But that's fragile.
            //
            // Simpler: call Validate with an empty args and check that "type" is NOT in
            // violations as "none (additionalProperties: false)".  Tools with
            // additionalProperties:false that DON'T declare 'type' will reject it.
            // Tools that DO declare 'type' or have no additionalProperties constraint will accept it.
            //
            // Hardcode the set of tools that are known to accept 'type' based on
            // tool_definitions.json scan — this list covers all object-targeted tools.
            return _toolsWithTypeArg.Contains(toolName);
        }

        // All tools in tool_definitions.json that declare a top-level "type" property.
        // Keeping this as a static set avoids a runtime JSON parse per call.
        private static readonly HashSet<string> _toolsWithTypeArg = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "genexus_read",
            "genexus_edit",
            "genexus_inspect",
            "genexus_delete_object",
            "genexus_refactor",
            "genexus_edit_and_build",
            "genexus_properties",
            "genexus_analyze",
            "genexus_apply_pattern",
            "genexus_variable",
            "genexus_io",
            "genexus_versioning",
            "genexus_db",
            "genexus_rename_across_kb",
            "genexus_multi_agent_lock",
            "genexus_navigation",
            "genexus_layout",
            "genexus_structure",
            "genexus_create",
        };
    }
}
