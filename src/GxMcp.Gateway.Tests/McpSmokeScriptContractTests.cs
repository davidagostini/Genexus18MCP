using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Contract test for scripts/mcp_smoke.ps1 — the diagnostic script users run via
    /// `genexus-mcp doctor --mcp-smoke` when their MCP connection is broken.
    ///
    /// Regression class this pins: first-party scripts that speak the gateway's HTTP
    /// protocol drifting out of sync with the gateway's header validation. That exact
    /// bug shipped in v2.38.0-v2.45.1: the smoke script omitted the required
    /// `Accept: application/json, text/event-stream` header, so the gateway's own
    /// ValidatePostHeaders rejected it with 406 and the doctor reported "smoke failed"
    /// on every healthy server — masking real connection problems.
    ///
    /// The unit tests on McpHttpProtocol cover the server side of that contract; this
    /// test covers the client side (our script) against a live in-process gateway.
    /// Skipped on non-Windows (the script is PowerShell) — same platform gate as the
    /// Worker test suite.
    /// </summary>
    public class McpSmokeScriptContractTests
    {
        private static bool IsWindows =>
            OperatingSystem.IsWindows();

        private static string FindRepoRoot()
        {
            // Walk up from the test assembly location until we find .git or CHANGELOG.md.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "CHANGELOG.md"))
                    && Directory.Exists(Path.Combine(dir.FullName, ".git")))
                    return dir.FullName;
                dir = dir.Parent!;
            }
            return string.Empty;
        }

        [Fact]
        public void McpSmokeScript_Succeeds_AgainstLiveGateway()
        {
            if (!IsWindows)
            {
                // mcp_smoke.ps1 requires PowerShell (Windows). Skip on other platforms.
                return;
            }
            string repoRoot = FindRepoRoot();
            if (string.IsNullOrEmpty(repoRoot))
            {
                return; // Repo root not found from test assembly.
            }

            string script = Path.Combine(repoRoot, "scripts", "mcp_smoke.ps1");
            Assert.True(File.Exists(script), $"mcp_smoke.ps1 not found at {script}");

            // Launch the just-built gateway with an ephemeral port and a scratch config
            // so the test never touches the user's running instance or KB. The config
            // mirrors what doctor's smoke targets: HTTP-only loopback, no stdio.
            int port = GetFreePort();
            string workDir = Path.Combine(Path.GetTempPath(), "gxmcp-smoketest-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(workDir);
            try
            {
                string configPath = Path.Combine(workDir, "gateway.smoke.json");
                File.WriteAllText(configPath, $@"
{{
  ""Server"": {{
    ""HttpPort"": {port},
    ""McpStdio"": false,
    ""WorkerIdleTimeoutMinutes"": 1
  }},
  ""Environment"": {{}}
}}
");

                string gatewayExe = Path.Combine(repoRoot, "src", "GxMcp.Gateway", "bin", "Debug", "net8.0-windows", "GxMcp.Gateway.exe");
                Assert.True(File.Exists(gatewayExe), $"Gateway exe not built at {gatewayExe} — run dotnet build first.");

                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = gatewayExe,
                        Arguments = string.Empty,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = Path.GetDirectoryName(gatewayExe)!,
                    }
                };
                proc.StartInfo.EnvironmentVariables["GX_CONFIG_PATH"] = configPath;
                // Drain output so a chatty gateway can't block on a full pipe.
                proc.OutputDataReceived += (_, _) => { };
                proc.ErrorDataReceived += (_, _) => { };
                Assert.True(proc.Start());
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                try
                {
                    // Wait until the HTTP listener answers (gateway startup + lease check).
                    bool listening = WaitForHttpAsync(port, timeoutSeconds: 30).GetAwaiter().GetResult();
                    Assert.True(listening, $"Gateway did not start listening on port {port} within 30s.");

                    // THE CONTRACT: run the actual smoke script unmodified. If anyone
                    // tightens gateway header validation without updating the script —
                    // or loosens the script below the gateway's requirements — this fails.
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -BaseUrl \"http://127.0.0.1:{port}/mcp\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = repoRoot,
                    };
                    using var smoke = Process.Start(psi)!;
                    string stdout = smoke.StandardOutput.ReadToEnd();
                    string stderr = smoke.StandardError.ReadToEnd();
                    smoke.WaitForExit(120_000);

                    Assert.True(smoke.ExitCode == 0,
                        "mcp_smoke.ps1 FAILED against a healthy gateway — first-party diagnostic " +
                        "script has drifted from the gateway's protocol contract.\n" +
                        "--- stdout ---\n" + stdout + "\n--- stderr ---\n" + stderr);
                    Assert.Contains("[SMOKE] PASS", stdout);
                }
                finally
                {
                    if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                }
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        private static int GetFreePort()
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static async System.Threading.Tasks.Task<bool> WaitForHttpAsync(int port, int timeoutSeconds)
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var resp = await client.GetAsync($"http://127.0.0.1:{port}/mcp");
                    // Any HTTP answer means the listener is up (405/406/400 are fine —
                    // GET /mcp isn't a valid call but proves the socket is serving).
                    return true;
                }
                catch { await System.Threading.Tasks.Task.Delay(500); }
            }
            return false;
        }
    }
}
