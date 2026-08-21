using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    public static class ToolProfileFilter
    {
        private static readonly HashSet<string> CoreTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "genexus_whoami",
            "genexus_query",
            "genexus_list_objects",
            "genexus_read",
            "genexus_edit",
            "genexus_inspect",
            "genexus_analyze",
            "genexus_lifecycle",
            "genexus_search_source",
            "genexus_kb",
            "genexus_doc"
        };

        private static readonly HashSet<string> AuthoringTools = new HashSet<string>(CoreTools, StringComparer.OrdinalIgnoreCase)
        {
            "genexus_create",
            "genexus_structure",
            "genexus_variable",
            "genexus_authoring",
            "genexus_refactor",
            "genexus_properties",
            "genexus_format",
            "genexus_delete_object",
            "genexus_api",
            "genexus_data_view",
            "genexus_recipe",
            "genexus_module",
            "genexus_generator_reference",
            "genexus_layout",
            "genexus_edit_form",
            "genexus_wwp",
            "genexus_apply_pattern",
            "genexus_edit_and_build"
        };

        private static readonly HashSet<string> DevOpsTools = new HashSet<string>(CoreTools, StringComparer.OrdinalIgnoreCase)
        {
            "genexus_test",
            "genexus_sdk_probe",
            "genexus_worker_reload",
            "genexus_doctor",
            "genexus_run_object",
            "genexus_compare",
            "genexus_merge",
            "genexus_gxserver",
            "genexus_kb_version",
            "genexus_versioning",
            "genexus_memory",
            "genexus_transfer",
            "genexus_deploy",
            "genexus_telemetry",
            "genexus_security",
            "genexus_io"
        };

        private static readonly HashSet<string> UITools = new HashSet<string>(CoreTools, StringComparer.OrdinalIgnoreCase)
        {
            "genexus_layout",
            "genexus_edit_form",
            "genexus_browser",
            "genexus_wwp",
            "genexus_apply_pattern",
            "genexus_structure",
            "genexus_properties"
        };

        private static readonly HashSet<string> DbTools = new HashSet<string>(CoreTools, StringComparer.OrdinalIgnoreCase)
        {
            "genexus_db",
            "genexus_data_view",
            "genexus_structure",
            "genexus_navigation"
        };

        public static string ResolveActiveProfile(string? configuredProfile = null)
        {
            string? envProfile = global::System.Environment.GetEnvironmentVariable("GXMCP_PROFILE");
            if (!string.IsNullOrWhiteSpace(envProfile))
            {
                return envProfile.Trim().ToLowerInvariant();
            }

            if (!string.IsNullOrWhiteSpace(configuredProfile))
            {
                return configuredProfile.Trim().ToLowerInvariant();
            }

            return "all";
        }

        public static JArray Filter(JArray tools, string? profile)
        {
            if (tools == null || tools.Count == 0) return new JArray();

            string normalizedProfile = (profile ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(normalizedProfile) || normalizedProfile == "all")
            {
                return tools;
            }

            var tokens = normalizedProfile.Split(new[] { ',', '+', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || tokens.Any(t => string.Equals(t, "all", StringComparison.OrdinalIgnoreCase)))
            {
                return tools;
            }

            var allowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool matchedAny = false;

            foreach (var token in tokens)
            {
                var set = token switch
                {
                    "core" => CoreTools,
                    "authoring" => AuthoringTools,
                    "devops" or "cicd" => DevOpsTools,
                    "ui" or "frontend" => UITools,
                    "db" or "data" => DbTools,
                    _ => null
                };

                if (set != null)
                {
                    matchedAny = true;
                    foreach (var toolName in set)
                    {
                        allowlist.Add(toolName);
                    }
                }
            }

            if (!matchedAny)
            {
                // Unrecognized profile: fail open (return all)
                return tools;
            }

            var filtered = new JArray(tools.OfType<JObject>()
                .Where(t =>
                {
                    string? name = t["name"]?.ToString();
                    return !string.IsNullOrEmpty(name) && allowlist.Contains(name);
                }));

            return filtered;
        }
    }
}
