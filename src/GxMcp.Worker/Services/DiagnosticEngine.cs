using System;
using System.Diagnostics;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public interface IDiagnosticEngine
    {
        JObject GetSystemDiagnostics();
        void RecordIncident(string category, string message, string target = null);
    }

    /// <summary>
    /// Deep diagnostic and telemetry engine monitoring worker memory, process state,
    /// and runtime health.
    /// </summary>
    public class DiagnosticEngine : IDiagnosticEngine
    {
        private readonly KbService _kbService;

        public DiagnosticEngine(KbService kbService)
        {
            _kbService = kbService;
        }

        public JObject GetSystemDiagnostics()
        {
            var proc = Process.GetCurrentProcess();
            long memoryMb = proc.WorkingSet64 / (1024 * 1024);
            long privateMb = proc.PrivateMemorySize64 / (1024 * 1024);

            string kbName = null;
            try { kbName = _kbService?.GetKB()?.Name; } catch { }

            return new JObject
            {
                ["status"] = "Healthy",
                ["processId"] = proc.Id,
                ["workingSetMb"] = memoryMb,
                ["privateMemoryMb"] = privateMb,
                ["activeKb"] = kbName ?? "None",
                ["timestampUtc"] = DateTime.UtcNow.ToString("o")
            };
        }

        public void RecordIncident(string category, string message, string target = null)
        {
            Logger.Warn($"[INCIDENT][{category}] {target}: {message}");
        }
    }
}
