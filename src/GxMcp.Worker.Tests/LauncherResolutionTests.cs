using System.IO;
using System.Threading.Tasks;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// v2.6.6 Stream H (FR#25) — F5 launcher parity. The PreviewService.RunAsync
    /// path resolves a launcher object when target is omitted, mirroring the
    /// IDE's F5/Run behaviour. When no candidate exists the call surfaces a
    /// <c>NoLauncher</c> envelope instead of guessing.
    /// </summary>
    public class LauncherResolutionTests
    {
        private class FakeRunner : PreviewService.ICliRunner
        {
            public string WhichResult = null; // force cli_missing — we only care about resolution
            public PreviewService.CliResult Run(string fileName, string arguments, int timeoutMs)
                => new PreviewService.CliResult { ExitCode = 0 };
            public string Which(string command) => WhichResult;
        }

        private static string TempDir()
        {
            string p = Path.Combine(Path.GetTempPath(), "LauncherResTest_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(p);
            return p;
        }

        [Fact]
        public async Task Run_NoTarget_ResolvesViaLauncherResolver()
        {
            var dir = TempDir();
            var svc = new PreviewService(null, null, new FakeRunner(), Path.Combine(dir, "preview.config.json"), dir);
            svc.SetLauncherResolverForTest(() => "MainWebPanel");

            var r = await svc.RunAsync(null);
            Assert.Equal("MainWebPanel", r["resolvedLauncher"]?.ToString());
            // PreviewSync proceeds past resolution; cli_missing is fine — the
            // assertion is that resolution worked.
            Assert.Equal("cli_missing", r["status"]?.ToString());
            Assert.Equal("MainWebPanel", r["name"]?.ToString());
        }

        [Fact]
        public async Task Run_NoTarget_NoCandidate_ReturnsNoLauncherHint()
        {
            var dir = TempDir();
            var svc = new PreviewService(null, null, new FakeRunner(), Path.Combine(dir, "preview.config.json"), dir);
            svc.SetLauncherResolverForTest(() => null);

            var r = await svc.RunAsync(null);
            Assert.Equal("error", r["status"]?.ToString());
            Assert.Equal("NoLauncher", r["error"]?["code"]?.ToString());
            Assert.Contains("Pass explicit target", r["error"]?["hint"]?.ToString() ?? "");
        }

        [Fact]
        public async Task Run_ExplicitTarget_SkipsResolver()
        {
            var dir = TempDir();
            var svc = new PreviewService(null, null, new FakeRunner(), Path.Combine(dir, "preview.config.json"), dir);
            int resolverCalls = 0;
            svc.SetLauncherResolverForTest(() => { resolverCalls++; return "ResolverChoice"; });

            var r = await svc.RunAsync("ExplicitPanel");
            Assert.Equal(0, resolverCalls);
            Assert.Equal("ExplicitPanel", r["name"]?.ToString());
            Assert.Equal("ExplicitPanel", r["resolvedLauncher"]?.ToString());
        }

        [Fact]
        public async Task Run_EmptyStringTarget_TriggersResolution()
        {
            var dir = TempDir();
            var svc = new PreviewService(null, null, new FakeRunner(), Path.Combine(dir, "preview.config.json"), dir);
            svc.SetLauncherResolverForTest(() => "ResolvedPanel");

            var r = await svc.RunAsync("");
            Assert.Equal("ResolvedPanel", r["resolvedLauncher"]?.ToString());
        }
    }
}
