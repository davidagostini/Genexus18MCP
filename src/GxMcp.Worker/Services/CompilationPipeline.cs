using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public enum CompilationStage
    {
        Planning,
        Specification,
        Compilation,
        DiagnosticHarvesting,
        BindingVerification,
        Completed,
        Failed
    }

    public sealed class DiagnosticEntry
    {
        public string Severity { get; set; } = "error";
        public string Code { get; set; }
        public string Message { get; set; }
        public string File { get; set; }
        public int? Line { get; set; }
        public int? Column { get; set; }
    }

    /// <summary>
    /// Deep Compilation Pipeline for GeneXus builds.
    /// Encapsulates MSBuild process spawning, SDK in-process specification, log scraping,
    /// structured diagnostic harvesting, and binding verification behind a clean lifecycle interface.
    /// </summary>
    public sealed class CompilationPipeline
    {
        private readonly BuildService _buildService;
        private readonly KbService _kbService;

        private static readonly Regex DiagnosticPattern = new Regex(
            @"(?<file>[^\r\n\(]+)\((?<line>\d+)(?:,(?<col>\d+))?\):\s*(?<severity>error|warning)\s*(?<code>[A-Za-z0-9]+):\s*(?<msg>[^\r\n]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex GxSpecDiagnosticPattern = new Regex(
            @"(?:^|\r|\n)(?<severity>error|warning)\s*(?<code>spc\d{4}|src\d{4}):\s*(?<msg>[^\r\n]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public CompilationPipeline(BuildService buildService, KbService kbService)
        {
            _buildService = buildService ?? throw new ArgumentNullException(nameof(buildService));
            _kbService = kbService;
        }

        public string ExecuteBuild(string action, string target, JObject args)
        {
            if (args == null) args = new JObject();

            string includeCallees = args["includeCallees"]?.ToString();
            int buildPlanCap = args["buildPlanCap"]?.ToObject<int?>() ?? 100;

            return _buildService.Build(
                action: action ?? "build",
                target: target,
                includeCallees: includeCallees,
                buildPlanCap: buildPlanCap);
        }

        public List<DiagnosticEntry> HarvestDiagnostics(string rawCompilerLog)
        {
            var diagnostics = new List<DiagnosticEntry>();
            if (string.IsNullOrWhiteSpace(rawCompilerLog)) return diagnostics;

            // 1. Standard MSBuild / C# compiler diagnostic pattern
            var matches = DiagnosticPattern.Matches(rawCompilerLog);
            foreach (Match m in matches)
            {
                if (!m.Success) continue;
                var entry = new DiagnosticEntry
                {
                    File = m.Groups["file"].Value.Trim(),
                    Severity = m.Groups["severity"].Value.ToLowerInvariant(),
                    Code = m.Groups["code"].Value.Trim(),
                    Message = m.Groups["msg"].Value.Trim()
                };

                if (int.TryParse(m.Groups["line"].Value, out int line)) entry.Line = line;
                if (m.Groups["col"].Success && int.TryParse(m.Groups["col"].Value, out int col)) entry.Column = col;

                diagnostics.Add(entry);
            }

            // 2. GeneXus Specification diagnostic pattern (spc/src codes)
            var gxMatches = GxSpecDiagnosticPattern.Matches(rawCompilerLog);
            foreach (Match m in gxMatches)
            {
                if (!m.Success) continue;
                var entry = new DiagnosticEntry
                {
                    Severity = m.Groups["severity"].Value.ToLowerInvariant(),
                    Code = m.Groups["code"].Value.ToUpperInvariant(),
                    Message = m.Groups["msg"].Value.Trim()
                };
                diagnostics.Add(entry);
            }

            return diagnostics;
        }
    }
}