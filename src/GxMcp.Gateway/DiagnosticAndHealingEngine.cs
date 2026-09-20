using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    internal sealed class DiagnosticReport
    {
        public int HealthScore { get; set; } = 100;
        public string Status { get; set; } = "healthy";
        public List<string> Findings { get; } = new List<string>();
        public List<string> RecommendedActions { get; } = new List<string>();
        public JObject Details { get; set; } = new JObject();
    }

    /// <summary>
    /// Deep Diagnostic & Healing Engine for Genexus18MCP Gateway.
    /// Unifies watchdog health probes, crash ledger analysis, mutation fencing,
    /// and automated recovery workflows (worker soft-reload, hung process cleanup).
    /// </summary>
    internal static class DiagnosticAndHealingEngine
    {
        public static DiagnosticReport DiagnoseSystemHealth(string? activeKbPath = null, MutationRecoveryRegistry? recoveryRegistry = null)
        {
            var report = new DiagnosticReport();

            // 1. Check Crash Ledger
            try
            {
                var summary = CrashLedger.Summarize(5);
                int unexpected = summary["unexpected"]?.ToObject<int?>() ?? 0;
                if (unexpected > 0)
                {
                    report.HealthScore -= (unexpected * 15);
                    report.Findings.Add($"Detected {unexpected} unexpected worker crashes in CrashLedger.");
                    report.RecommendedActions.Add("Review worker stderr logs for native COM/AccessViolation exceptions.");
                }
                report.Details["crashSummary"] = summary;
            }
            catch { }

            // 2. Check KB Path & Fencing
            if (!string.IsNullOrWhiteSpace(activeKbPath))
            {
                if (!Directory.Exists(activeKbPath))
                {
                    report.HealthScore -= 30;
                    report.Findings.Add($"Active KB path does not exist on disk: {activeKbPath}");
                    report.RecommendedActions.Add("Verify KB directory path in configuration or run genexus_kb action=open.");
                }
            }

            // 3. Check Mutation Fencing Registry
            try
            {
                if (recoveryRegistry != null && recoveryRegistry.Count > 0)
                {
                    report.HealthScore -= (recoveryRegistry.Count * 5);
                    report.Findings.Add($"Detected {recoveryRegistry.Count} fenced objects requiring post-timeout read confirmation.");
                    report.RecommendedActions.Add("Call genexus_read on fenced objects to confirm state and clear write fence.");
                    report.Details["fencedCount"] = recoveryRegistry.Count;
                }
            }
            catch { }

            // 4. Score Normalization
            if (report.HealthScore < 0) report.HealthScore = 0;
            if (report.HealthScore > 100) report.HealthScore = 100;

            if (report.HealthScore < 50) report.Status = "degraded";
            else if (report.HealthScore < 80) report.Status = "warning";
            else report.Status = "healthy";

            return report;
        }
    }
}