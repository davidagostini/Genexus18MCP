using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace GxMcp.Worker.Services
{
    public interface IBrowserDriverInvoker
    {
        string ResolveDriverPath();
        DriverResult Invoke(string arguments, int timeoutMs);
    }

    public class DriverResult
    {
        public int ExitCode;
        public string StdOut = string.Empty;
        public string StdErr = string.Empty;
        public bool TimedOut;
        public bool DriverMissing;
    }

    internal static class BrowserDriverProcess
    {
        internal static string BuildArguments(IEnumerable<string> arguments)
        {
            var values = (arguments ?? Enumerable.Empty<string>()).ToArray();
            if (values.Length == 0) return string.Empty;
            return string.Join(" ", values.Select(QuoteArgument));
        }

        internal static string BuildShimArguments(string driverPath, IEnumerable<string> arguments)
        {
            if (string.IsNullOrWhiteSpace(driverPath)) throw new ArgumentException("Driver path is required.", nameof(driverPath));
            var ext = Path.GetExtension(driverPath);
            if (!string.Equals(ext, ".cmd", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, ".bat", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only .cmd and .bat shims are supported.", nameof(driverPath));
            var logical = (arguments ?? Enumerable.Empty<string>()).ToArray();
            foreach (var argument in logical)
            {
                if (argument == null || argument.IndexOf('%') >= 0 || argument.Any(char.IsControl))
                    throw new ArgumentException("Driver arguments contain an unsafe character.", nameof(arguments));
            }
            var escaped = string.Join(" ", logical.Select(a => EscapeCmdMeta(QuoteArgument(a))));
            return "/d /s /c \"\"" + driverPath + "\"" + (escaped.Length == 0 ? "" : " " + escaped) + "\"";
        }

        private static string EscapeCmdMeta(string value)
        {
            var b = new StringBuilder(value.Length + 8);
            foreach (var c in value)
            {
                if (c == '&' || c == '|' || c == '<' || c == '>' || c == '^' || c == '(' || c == ')') b.Append('^');
                b.Append(c);
            }
            return b.ToString();
        }

        private static string QuoteArgument(string value)
        {
            value = value ?? string.Empty;
            var b = new StringBuilder(value.Length + 2);
            b.Append('"');
            int slashes = 0;
            foreach (var c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"')
                {
                    b.Append('\\', slashes * 2 + 1).Append('"');
                    slashes = 0;
                    continue;
                }
                b.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            b.Append('\\', slashes * 2).Append('"');
            return b.ToString();
        }
    }

    public class DefaultBrowserDriverInvoker : IBrowserDriverInvoker
    {
        private string _cachedPath;
        private bool _probed;
        private readonly object _lock = new object();
        private readonly string _configuredPath;

        public DefaultBrowserDriverInvoker() { }

        internal DefaultBrowserDriverInvoker(string configuredPath) { _configuredPath = configuredPath; }

        public string ResolveDriverPath()
        {
            if (_probed) return _cachedPath;
            lock (_lock)
            {
                if (_probed) return _cachedPath;
                _cachedPath = string.IsNullOrWhiteSpace(_configuredPath) ? FindOnPath() : ValidateDriverPath(_configuredPath);
                _probed = true;
                return _cachedPath;
            }
        }

        private static string FindOnPath()
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in path.Split(Path.PathSeparator).Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                foreach (var name in new[] { "chrome-devtools-axi.exe", "chrome-devtools-axi.com", "chrome-devtools-axi.cmd", "chrome-devtools-axi.bat" })
                {
                    try
                    {
                        var candidate = Path.GetFullPath(Path.Combine(directory.Trim(), name));
                        var validated = ValidateDriverPath(candidate);
                        if (validated != null) return validated;
                    }
                    catch { }
                }
            }
            return null;
        }

        private static string ValidateDriverPath(string path)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                var ext = Path.GetExtension(fullPath);
                bool supported = string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(ext, ".com", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(ext, ".cmd", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(ext, ".bat", StringComparison.OrdinalIgnoreCase);
                return supported && File.Exists(fullPath) ? fullPath : null;
            }
            catch { return null; }
        }

        public DriverResult Invoke(string arguments, int timeoutMs)
        {
            var cli = ResolveDriverPath();
            if (string.IsNullOrEmpty(cli))
                return new DriverResult { ExitCode = -1, DriverMissing = true, StdErr = "chrome-devtools-axi not found in PATH" };
            try
            {
                var ext = Path.GetExtension(cli);
                bool native = string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".com", StringComparison.OrdinalIgnoreCase);
                var logicalArguments = ParseLegacyArguments(arguments).ToArray();
                if (logicalArguments.Any(a => a == null || a.Any(char.IsControl)))
                    return new DriverResult { ExitCode = -1, StdErr = "Driver arguments contain an unsafe control character." };
                var psi = native
                    ? new ProcessStartInfo(cli, BrowserDriverProcess.BuildArguments(logicalArguments))
                    : new ProcessStartInfo("cmd.exe", BrowserDriverProcess.BuildShimArguments(cli, logicalArguments));
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (var p = Process.Start(psi))
                {
                    var output = new StringBuilder();
                    var error = new StringBuilder();
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) error.AppendLine(e.Data); };
                    p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        try { p.WaitForExit(1000); } catch { }
                        return new DriverResult { ExitCode = -1, StdOut = output.ToString(), StdErr = error.ToString(), TimedOut = true };
                    }
                    try { p.WaitForExit(500); } catch { }
                    return new DriverResult { ExitCode = p.ExitCode, StdOut = output.ToString(), StdErr = error.ToString() };
                }
            }
            catch (Exception ex) { return new DriverResult { ExitCode = -1, StdErr = ex.Message }; }
        }

        internal static IEnumerable<string> ParseLegacyArguments(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments)) return Enumerable.Empty<string>();
            var values = new List<string>();
            var current = new StringBuilder();
            bool quoted = false;
            foreach (var c in arguments)
            {
                if (c == '"') { quoted = !quoted; continue; }
                if (char.IsWhiteSpace(c) && !quoted) { if (current.Length > 0) { values.Add(current.ToString()); current.Clear(); } }
                else current.Append(c);
            }
            if (current.Length > 0) values.Add(current.ToString());
            return values;
        }
    }
}
