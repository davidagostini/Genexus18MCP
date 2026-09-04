using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Unified read/write action classifier across the gateway (Issue #131 A4/C2).
    /// Used by MacroSuggestionService (to discard pure read-only sequences) and
    /// NextLegalActionsBuilder (to skip steps that don't need next-step follow-up).
    /// </summary>
    internal static class OperationClassifier
    {
        private static readonly HashSet<string> PureReadOnlyTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "genexus_query",
            "genexus_list_objects",
            "genexus_read",
            "genexus_inspect",
            "genexus_analyze",
            "genexus_whoami",
            "genexus_doctor",
            "genexus_navigation",
            "genexus_search_source",
            "genexus_security",
            "genexus_logs" // legacy alias
        };

        private static readonly HashSet<string> StructureReadOnlyActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "get_visual", "get_indexes", "get_logic", "check_subtypes"
        };

        private static readonly HashSet<string> VersioningReadOnlyActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "history_list", "history_get", "time_travel", "blame", "diff", "diff_revisions", "diff_objects"
        };

        private static readonly HashSet<string> KbReadOnlyActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "list", "list_environments", "get_environment"
        };

        private static readonly HashSet<string> IoReadOnlyActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "asset_find", "asset_read", "ocr"
        };

        private static readonly HashSet<string> PropertiesReadOnlyActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "get", "list"
        };

        public static bool IsReadOnly(string? toolName, JObject? args)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return false;

            if (PureReadOnlyTools.Contains(toolName)) return true;

            string? action = args?["action"]?.ToString()?.ToLowerInvariant();

            if (string.Equals(toolName, "genexus_structure", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(action) && StructureReadOnlyActions.Contains(action);
            }

            if (string.Equals(toolName, "genexus_telemetry", StringComparison.OrdinalIgnoreCase))
            {
                // friction_append writes .gx/friction.jsonl; logs/metrics/status are reads
                return !string.Equals(action, "friction_append", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_versioning", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(action) && VersioningReadOnlyActions.Contains(action);
            }

            if (string.Equals(toolName, "genexus_history", StringComparison.OrdinalIgnoreCase)) // legacy alias
            {
                return string.Equals(action, "list", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "get", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_doc", StringComparison.OrdinalIgnoreCase))
            {
                // wiki writes docs/<target>.md, visualize writes html/graph.html; health is pure read
                return string.Equals(action, "health", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_recipe", StringComparison.OrdinalIgnoreCase))
            {
                // crystallize persists JSON; list/describe/suggest_macro are reads
                return !string.Equals(action, "crystallize", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_kb", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(action) && KbReadOnlyActions.Contains(action);
            }

            if (string.Equals(toolName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(action, "status", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(action, "result", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(action, "cancel", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(action, "reorg_preview", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (args?["dryRun"]?.ToObject<bool?>() == true &&
                    (string.Equals(action, "build", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(action, "rebuild", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(action, "specify", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(action, "index", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
                return false;
            }

            if (string.Equals(toolName, "genexus_db", StringComparison.OrdinalIgnoreCase))
            {
                // sample_data writes DB rows, translations_import writes object parts
                return !string.Equals(action, "sample_data", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(action, "translations_import", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_io", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(action) && IoReadOnlyActions.Contains(action);
            }

            if (string.Equals(toolName, "genexus_properties", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(action) && PropertiesReadOnlyActions.Contains(action);
            }

            return false;
        }
    }
}
