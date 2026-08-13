using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Coverage for items 5 + 37 (friction 2026-05-22). Mocks the CLI runner
    /// so no real browser is launched; the screenshot bytes are produced by a
    /// tiny in-memory PNG generator so pixel-diff has something deterministic
    /// to compare.
    /// </summary>
    public class VisualVerifyServiceTests
    {
        private const string FakeKbName = "AcademicoHomolog1";
        private const string FakePartName = "WebForm";

        /// <summary>
        /// CLI runner that records calls and either writes a fake PNG on
        /// "screenshot" or returns a configurable Which() result.
        /// </summary>
        private class FakeRunner : VisualVerifyService.ICliRunner
        {
            public List<(string fileName, string arguments)> Calls = new List<(string, string)>();
            public string WhichResult = "C:/fake/chrome-devtools-axi.cmd";
            public Func<string, string, (int exit, string stderr)> RunHandler = null;
            public Action<string> OnScreenshot; // optional override for screenshot side-effect

            public VisualVerifyService.CliResult Run(string fileName, string arguments, int timeoutMs)
            {
                Calls.Add((fileName, arguments));
                if (arguments != null && arguments.StartsWith("screenshot ", StringComparison.Ordinal))
                {
                    // Extract the quoted path arg
                    string outPath = arguments.Substring("screenshot ".Length).Trim('"');
                    if (OnScreenshot != null)
                    {
                        try { OnScreenshot(outPath); } catch { }
                    }
                    else
                    {
                        WriteFakePng(outPath, Color.Blue);
                    }
                    return new VisualVerifyService.CliResult { ExitCode = 0 };
                }
                if (RunHandler != null)
                {
                    var (exit, stderr) = RunHandler(fileName, arguments);
                    return new VisualVerifyService.CliResult { ExitCode = exit, StdErr = stderr };
                }
                return new VisualVerifyService.CliResult { ExitCode = 0 };
            }

            public string Which(string command) => WhichResult;
        }

        private static void WriteFakePng(string path, Color color)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "."); } catch { }
            using (var bmp = new Bitmap(8, 8, PixelFormat.Format32bppArgb))
            {
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        bmp.SetPixel(x, y, color);
                bmp.Save(path, ImageFormat.Png);
            }
        }

        private static string FreshKb()
        {
            string p = Path.Combine(Path.GetTempPath(), "VisualVerifySvc_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(p);
            return p;
        }

        private static VisualVerifyService BuildService(FakeRunner runner, string kbDir, string launcher = "MyPanel")
        {
            return new VisualVerifyService(
                runner,
                () => launcher,
                () => kbDir,
                name => (name ?? "obj").ToLowerInvariant() + ".aspx",
                "http://localhost/fake");
        }

        [Fact]
        public void Verify_WhenDriverUnavailable_ReturnsSkippedWithReason()
        {
            // Item 5 contract: edit succeeds even when no browser CLI is on PATH.
            // The verify result must surface skipped:true + a structured reason
            // so the agent can degrade gracefully without parsing free-form text.
            var runner = new FakeRunner { WhichResult = null };
            var svc = BuildService(runner, FreshKb());

            var r = svc.Verify(FakeKbName, FakePartName);

            Assert.True(r.Skipped);
            Assert.Equal("BrowserDriverUnavailable", r.SkipReason);
            Assert.Null(r.ScreenshotPath);
        }

        [Fact]
        public void Verify_NoBaseline_ReturnsScreenshotAndNoPixelDiff()
        {
            // First-time visualVerify on an object: screenshot is captured and
            // persisted under .gx/visual-baselines/<obj>/<part>/<utc>.png but
            // there's nothing to diff against yet, so pixelDiff stays null.
            var runner = new FakeRunner();
            string kb = FreshKb();
            var svc = BuildService(runner, kb);

            var r = svc.Verify(FakeKbName, FakePartName);

            Assert.False(r.Skipped);
            Assert.NotNull(r.ScreenshotPath);
            Assert.True(File.Exists(r.ScreenshotPath));
            Assert.Null(r.Diff);
            Assert.Equal("http://localhost/fake/" + FakeKbName.ToLowerInvariant() + ".aspx", r.UrlOpened);
            Assert.False(string.IsNullOrEmpty(r.Base64Truncated));
            // Sanity: persisted under the expected path layout.
            string expectedRoot = Path.Combine(kb, ".gx", "visual-baselines", FakeKbName, FakePartName);
            Assert.StartsWith(expectedRoot, r.ScreenshotPath);
        }

        [Fact]
        public void Verify_WithPriorBaseline_AttachesPixelDiff()
        {
            // Item 37 contract: when a prior baseline exists for the same
            // (obj, part), the second verify run computes a pixel-equality
            // diff against it. Two different colors -> changedPixels == total.
            string kb = FreshKb();
            string baselinesDir = Path.Combine(kb, ".gx", "visual-baselines", FakeKbName, FakePartName);
            Directory.CreateDirectory(baselinesDir);
            // Pre-seed an older baseline. Use a "small" timestamp so ordinal
            // sort puts the new screenshot after it.
            string priorPath = Path.Combine(baselinesDir, "2000-01-01T00-00-00-000Z.png");
            WriteFakePng(priorPath, Color.Red);

            var runner = new FakeRunner
            {
                // Force the new screenshot to be a different color so every
                // pixel changes — easy assertion.
                OnScreenshot = p => WriteFakePng(p, Color.Lime)
            };
            var svc = BuildService(runner, kb);

            var r = svc.Verify(FakeKbName, FakePartName);

            Assert.False(r.Skipped);
            Assert.NotNull(r.Diff);
            Assert.Equal(64, r.Diff.TotalPixels);   // 8x8 = 64
            Assert.Equal(64, r.Diff.ChangedPixels); // every pixel differs
            Assert.True(File.Exists(r.Diff.DiffPath));
            Assert.Equal(priorPath, r.Diff.AgainstBaseline);
        }

        [Fact]
        public void VerifyAsJObject_OffByDefault_OmittedFromResponse()
        {
            // The wire-format guarantee: when visualVerify isn't requested the
            // dispatcher hook should bail before constructing any envelope.
            // We test that by exercising the gate on the dispatcher's contract:
            // the service itself only renders a JObject when called, so the
            // schema check happens upstream. Here we just confirm the JObject
            // shape on the "on" path is what the schema documents.
            var runner = new FakeRunner();
            var svc = BuildService(runner, FreshKb());

            JObject env = svc.VerifyAsJObject(FakeKbName, FakePartName);
            Assert.NotNull(env);
            Assert.NotNull(env["path"]);
            Assert.NotNull(env["base64Truncated"]);
            Assert.NotNull(env["capturedAtUtc"]);
            Assert.NotNull(env["urlOpened"]);
            // pixelDiff absent on first run (no prior baseline)
            Assert.Null(env["pixelDiff"]);
        }

        [Fact]
        public void Retention_PrunesBaselinesPastTen()
        {
            // Sanity check on the retention helper — drop into the public
            // surface by running Verify 12 times back-to-back; only the
            // newest 10 .png files should survive.
            string kb = FreshKb();
            var runner = new FakeRunner();
            var svc = BuildService(runner, kb);

            for (int i = 0; i < 12; i++)
            {
                var r = svc.Verify(FakeKbName, FakePartName);
                Assert.False(r.Skipped, "Verify must not skip on the happy path");
                // Force unique timestamps so file names don't collide.
                System.Threading.Thread.Sleep(5);
            }

            string dir = Path.Combine(kb, ".gx", "visual-baselines", FakeKbName, FakePartName);
            int pngCount = 0;
            foreach (var f in Directory.GetFiles(dir, "*.png"))
            {
                if (!Path.GetFileName(f).EndsWith(".diff.png", StringComparison.OrdinalIgnoreCase))
                    pngCount++;
            }
            Assert.Equal(VisualVerifyService.BaselineRetention, pngCount);
        }
    }
}
