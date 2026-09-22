using System;
using System.Diagnostics;
using System.Text;

namespace GxMcp.Gateway.Tests
{
    internal static class McpSmokeDiagnostics
    {
        internal const string PowerShellExecutable = "pwsh";
        private const int MaxDiagnosticChars = 16 * 1024;

        internal static string BuildGatewayDiagnostics(Process proc, StringBuilder gatewayOutput, StringBuilder gatewayError)
        {
            bool hasExited = false;
            string exitCode = "unknown";
            try
            {
                hasExited = proc.HasExited;
                if (hasExited) exitCode = proc.ExitCode.ToString();
            }
            catch (Exception ex)
            {
                exitCode = "unavailable (" + ex.GetType().Name + ")";
            }

            string stdout;
            string stderr;
            lock (gatewayOutput) stdout = Limit(gatewayOutput.ToString());
            lock (gatewayError) stderr = Limit(gatewayError.ToString());
            return "--- gateway process ---\n" +
                   "HasExited=" + hasExited + "\n" +
                   "ExitCode=" + exitCode + "\n" +
                   "GatewayOutput:\n" + stdout +
                   "GatewayError:\n" + stderr;
        }

        private static string Limit(string value)
        {
            if (value.Length <= MaxDiagnosticChars) return value;
            return value.Substring(0, MaxDiagnosticChars) + "\n[diagnostic output truncated]";
        }

        internal static string GetPowerShellDiagnostics()
        {
            try
            {
                using var host = Process.Start(new ProcessStartInfo
                {
                    FileName = PowerShellExecutable,
                    Arguments = "-NoProfile -Command \"$PSVersionTable.PSVersion.ToString()\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (host == null) return "executable=pwsh; process-started=false";
                bool exited = host.WaitForExit(5_000);
                if (!exited)
                {
                    try { host.Kill(entireProcessTree: true); } catch { }
                    return "executable=pwsh; version=timeout";
                }

                string version = host.StandardOutput.ReadToEnd().Trim();
                string error = host.StandardError.ReadToEnd().Trim();
                return "executable=pwsh; version=" + (string.IsNullOrEmpty(version) ? "unknown" : version) +
                       (string.IsNullOrEmpty(error) ? "" : "; error=" + error);
            }
            catch (Exception ex)
            {
                return "executable=pwsh; version=unavailable (" + ex.GetType().Name + ": " + ex.Message + ")";
            }
        }
    }
}
