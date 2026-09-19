using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Helpers
{
    public static class HealingService
    {
        public class HealingResult
        {
            public bool Healed { get; set; }
            public string NewCode { get; set; }
            public string ActionTaken { get; set; }
        }

        // v2.8.0 — universal lookup-error formatter. Emits the canonical
        // envelope and BRANCHES on the index probe:
        //   - 2+ exact-name matches (no type filter)  → AmbiguousName + candidates
        //   - 1 match but the SDK lookup still missed → ObjectNotFound + 3 closest
        //   - 0 matches                                → ObjectNotFound + actionable hint
        //   - index cold (null)                        → ObjectNotFoundIndexWarming + retry
        // Callers don't have to differentiate — they call this when FindObject
        // returns null and they get the right canonical shape for free.
        public static string FormatNotFoundError(string target, SearchIndex index)
        {
            // Index cold / unavailable. An empty (Count==0) index is the cold/warming
            // case too — a loaded index always carries the KB catalogue. Issue #27 item 3:
            // without this, a read while Cold fell through to "No similar names found in
            // the index", which falsely implies the index was consulted authoritatively.
            // The object lookup already tried the SDK directly (FindObject slow path), so
            // the honest message is "index still warming / SDK also missed — retry".
            if (index == null || index.Objects == null || index.Objects.Count == 0)
            {
                return McpResponse.Err(
                    code: "ObjectNotFoundIndexWarming",
                    message: "Object not found. The KB name index is still warming (a direct SDK lookup also missed).",
                    hint: "Retry in 2-3 seconds once the index is Ready for 'did you mean' suggestions, or list objects to find the exact name.",
                    nextSteps: new JArray(
                        McpResponse.NextStep(
                            tool: "genexus_list_objects",
                            args: new JObject { ["name_contains"] = target ?? string.Empty },
                            why: "Lists objects whose names contain the target string.")),
                    retryAfterMs: 2500,
                    target: target);
            }

            // Exact-name matches across the index (case-insensitive). If 2+
            // come back with different types and no type filter was applied,
            // that's ambiguity — surface candidates so the LLM picks one.
            var exactMatches = index.FindByName(target);

            if (exactMatches.Count >= 2)
            {
                var candidates = new JArray();
                var steps = new JArray();
                foreach (var m in exactMatches.Take(5))
                {
                    candidates.Add(new JObject
                    {
                        ["name"] = m.Name,
                        ["type"] = m.Type,
                        ["parent"] = m.Parent,
                        ["module"] = m.Module
                    });
                    // Pre-mount a retry-with-type nextStep per candidate so the
                    // LLM literally copies one of these calls.
                    steps.Add(McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = m.Name, ["type"] = m.Type },
                        why: $"Retry pinned to type='{m.Type}'."));
                }

                return McpResponse.Err(
                    code: "AmbiguousName",
                    message: $"'{target}' matches {exactMatches.Count} objects of different types.",
                    hint: "Re-call with 'type' set to one of the candidate types to disambiguate.",
                    nextSteps: steps,
                    target: target,
                    errorExtra: new JObject { ["candidates"] = candidates });
            }

            // Not ambiguous — fall back to similar-name suggestions.
            var suggestions = index.Objects.Values
                .Where(e => target != null
                    && (e.Name.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0
                        || target.IndexOf(e.Name, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(e => Math.Abs(e.Name.Length - (target?.Length ?? 0)))
                .Take(3)
                .ToList();

            var notFoundSteps = new JArray();
            if (suggestions.Count > 0)
            {
                foreach (var s in suggestions)
                {
                    notFoundSteps.Add(McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = s.Name, ["type"] = s.Type },
                        why: $"Closest name match in the index ({s.Type})."));
                }
            }
            else
            {
                notFoundSteps.Add(McpResponse.NextStep(
                    tool: "genexus_list_objects",
                    args: new JObject { ["name_contains"] = target ?? string.Empty },
                    why: "Broad list — finds objects whose names contain the target string."));
            }

            string suggestionHint = suggestions.Count > 0
                ? "Did you mean one of these? " + string.Join(", ", suggestions.Select(e => $"{e.Type}:{e.Name}"))
                : "No similar names found in the index.";

            return McpResponse.Err(
                code: "ObjectNotFound",
                message: $"Object not found: {target}",
                hint: suggestionHint,
                nextSteps: notFoundSteps,
                target: target);
        }

        public static HealingResult AttemptHealing(string code, JArray messages, SearchIndex index)
        {
            // Placeholder for real healing logic
            return new HealingResult { Healed = false };
        }
    }
}
