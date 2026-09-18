using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 93 — genexus_friction_log. Local journal of agent-reported friction
    /// stored as JSON-lines under <c>&lt;kbPath&gt;/.gx/friction.jsonl</c>.
    /// action=append adds a record; action=tail returns the last N records.
    /// </summary>
    public class FrictionLogService
    {
        private readonly KbService _kbService;

        public FrictionLogService(KbService kbService)
        {
            _kbService = kbService;
        }

        public string Append(string tool, string message, string severity, string kbPathOverride = null)
        {
            string kbPath = ResolveKbPath(kbPathOverride);
            if (string.IsNullOrEmpty(kbPath))
            {
                return Error("NoKbOpen", "No KB is currently open.");
            }
            return AppendCore(kbPath, tool, message, severity);
        }

        public string Tail(int n, string kbPathOverride = null)
        {
            string kbPath = ResolveKbPath(kbPathOverride);
            if (string.IsNullOrEmpty(kbPath))
            {
                return Error("NoKbOpen", "No KB is currently open.");
            }
            return TailCore(kbPath, n);
        }

        // ---- Pure-IO cores (test-friendly) ------------------------------------

        public static string AppendCore(string kbPath, string tool, string message, string severity)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(message))
                {
                    return Error("MissingMessage", "message is required.");
                }
                string dir = Path.Combine(kbPath, ".gx");
                Directory.CreateDirectory(dir);
                string filePath = Path.Combine(dir, "friction.jsonl");

                var entry = new JObject
                {
                    ["atUtc"] = DateTime.UtcNow.ToString("o"),
                    ["tool"] = tool ?? "",
                    ["message"] = message,
                    ["severity"] = NormalizeSeverity(severity)
                };
                string line = entry.ToString(Newtonsoft.Json.Formatting.None);
                File.AppendAllText(filePath, line + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                return McpResponse.Ok(
                    code: "FrictionLogAppended",
                    result: new JObject { ["path"] = filePath, ["entry"] = entry });
            }
            catch (Exception ex)
            {
                return Error("AppendFailed", ex.Message);
            }
        }

        public static string TailCore(string kbPath, int n)
        {
            try
            {
                if (n <= 0) n = 20;
                if (n > 1000) n = 1000;
                string filePath = Path.Combine(kbPath, ".gx", "friction.jsonl");
                if (!File.Exists(filePath))
                {
                    return McpResponse.Ok(
                        code: "FrictionLogTailed",
                        result: new JObject { ["path"] = filePath, ["entries"] = new JArray(), ["total"] = 0 });
                }

                var lines = File.ReadAllLines(filePath);
                // Keep LINQ's sequence reversal explicit; System.Memory also exposes
                // a void Span<T>.Reverse() once the Npgsql dependency is referenced.
                var tail = Enumerable.Reverse(lines)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Take(n)
                    .Reverse()
                    .ToList();
                var arr = new JArray();
                foreach (var line in tail)
                {
                    try { arr.Add(JObject.Parse(line)); }
                    catch
                    {
                        // Preserve malformed line as a raw string so a corrupted record
                        // doesn't poison the whole tail.
                        arr.Add(new JObject { ["raw"] = line, ["parseError"] = true });
                    }
                }
                return McpResponse.Ok(
                    code: "FrictionLogTailed",
                    result: new JObject { ["path"] = filePath, ["entries"] = arr, ["total"] = lines.Length });
            }
            catch (Exception ex)
            {
                return Error("TailFailed", ex.Message);
            }
        }

        private string ResolveKbPath(string kbPathOverride)
        {
            if (!string.IsNullOrEmpty(kbPathOverride)) return kbPathOverride;
            try { return _kbService?.GetKbPath(); } catch { return null; }
        }

        private static string NormalizeSeverity(string sev)
        {
            if (string.IsNullOrWhiteSpace(sev)) return "info";
            string s = sev.Trim().ToLowerInvariant();
            switch (s)
            {
                case "info":
                case "warn":
                case "warning":
                case "error":
                case "critical":
                    return s == "warning" ? "warn" : s;
                default:
                    return "info";
            }
        }

        private static string Error(string code, string message) =>
            McpResponse.Err(code: code, message: message);
    }
}
