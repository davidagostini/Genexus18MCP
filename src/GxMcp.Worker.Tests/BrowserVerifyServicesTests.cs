using System;
using System.Collections.Generic;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Wave-3 browser-verify pipeline tests. All three services share the
    /// <see cref="IBrowserDriverInvoker"/> seam — these tests stub it so no real
    /// chrome-devtools-axi shell-out happens.
    /// </summary>
    public class BrowserVerifyServicesTests
    {
        /// <summary>Records every CLI invocation and returns scripted responses by verb.</summary>
        private class FakeInvoker : IBrowserDriverInvoker
        {
            public List<string> Calls = new List<string>();
            public Dictionary<string, DriverResult> ByVerb = new Dictionary<string, DriverResult>();
            public string DriverPath = "C:/fake/chrome-devtools-axi.cmd";
            public DriverResult Default = new DriverResult { ExitCode = 0, StdOut = "[]", StdErr = "" };

            public string ResolveDriverPath() => DriverPath;
            public DriverResult Invoke(string arguments, int timeoutMs)
            {
                Calls.Add(arguments);
                var verb = (arguments ?? "").Split(' ')[0];
                return ByVerb.TryGetValue(verb, out var r) ? r : Default;
            }
        }

        // ---- BrowserCaptureService ----

        [Fact]
        public void BrowserCapture_HappyPath_ReturnsConsoleNetworkExceptions()
        {
            var inv = new FakeInvoker();
            // eval returns JSON arrays we encode here as full stdout.
            inv.Default = new DriverResult { ExitCode = 0, StdOut = "[{\"msg\":\"hi\"}]" };
            var svc = new BrowserCaptureService(null, inv);

            var res = svc.Capture("MyPanel", null);

            Assert.True((bool)res["ok"]);
            Assert.Equal("MyPanel", res["target"]?.ToString());
            Assert.Equal("C:/fake/chrome-devtools-axi.cmd", res["driverUsed"]?.ToString());
            Assert.NotNull(res["console"]);
            Assert.NotNull(res["network"]);
            Assert.NotNull(res["exceptions"]);
            Assert.IsType<JArray>(res["console"]);
            // capturedAtUtc must be ISO-8601-ish.
            Assert.Contains("T", res["capturedAtUtc"]?.ToString() ?? "");
        }

        [Fact]
        public void BrowserCapture_DriverUnavailable_ReturnsSkippedEnvelope()
        {
            var inv = new FakeInvoker { DriverPath = null };
            var svc = new BrowserCaptureService(null, inv);

            var res = svc.Capture("MyPanel", null);

            Assert.True((bool)res["skipped"]);
            Assert.Equal("BrowserDriverUnavailable", res["code"]?.ToString());
            Assert.False(res.ContainsKey("ok") && (bool?)res["ok"] == true);
            Assert.NotNull(res["hint"]);
        }

        [Fact]
        public void BrowserCapture_EmptyTarget_InvalidRequest()
        {
            var svc = new BrowserCaptureService(null, new FakeInvoker());
            var res = svc.Capture("", null);
            Assert.False((bool)res["ok"]);
            Assert.Equal("invalid_request", res["code"]?.ToString());
        }

        // ---- SmokeTestService ----

        private class StubHttp : SmokeTestService.IHttpProbe
        {
            public int Status = 200;
            public string Body = "<html>ok</html>";
            public string Err = null;
            public SmokeTestService.ProbeResult Fetch(string url)
                => new SmokeTestService.ProbeResult { StatusCode = Status, Body = Body, Error = Err };
        }

        [Fact]
        public void SmokeTest_AllPass_ReturnsOk()
        {
            var inv = new FakeInvoker { Default = new DriverResult { ExitCode = 0, StdOut = "[]" } };
            var capture = new BrowserCaptureService(null, inv);
            var svc = new SmokeTestService(capture, new StubHttp());

            var res = svc.Run("MyPanel");

            Assert.True((bool)res["ok"]);
            var steps = (JArray)res["steps"];
            Assert.Equal(4, steps.Count);
            foreach (var s in steps) Assert.True((bool)s["ok"]);
        }

        [Fact]
        public void SmokeTest_DriverMissing_SkippedWithFailedAt()
        {
            var inv = new FakeInvoker { DriverPath = null };
            var capture = new BrowserCaptureService(null, inv);
            var svc = new SmokeTestService(capture, new StubHttp());

            var res = svc.Run("MyPanel");

            Assert.False((bool)res["ok"]);
            Assert.True((bool)res["skipped"]);
            Assert.Equal("BrowserDriverUnavailable", res["code"]?.ToString());
            Assert.Equal("driver", res["failedAt"]?.ToString());
        }

        [Fact]
        public void SmokeTest_ScriptErrorInBody_FailsNoScriptErrorStep()
        {
            var inv = new FakeInvoker();
            var capture = new BrowserCaptureService(null, inv);
            var svc = new SmokeTestService(capture, new StubHttp { Body = "<html><scriptError>boom</scriptError></html>" });

            var res = svc.Run("MyPanel");

            Assert.False((bool)res["ok"]);
            Assert.Equal("no_script_error", res["failedAt"]?.ToString());
        }

        // ---- A11yAuditService ----

        [Fact]
        public void A11yAudit_HappyPath_NormalisesAxeViolations()
        {
            var inv = new FakeInvoker();
            inv.ByVerb["a11y"] = new DriverResult
            {
                ExitCode = 0,
                StdOut = "{\"violations\":[{\"id\":\"color-contrast\",\"impact\":\"serious\",\"helpUrl\":\"http://x\",\"nodes\":[{},{}]}]}"
            };
            var svc = new A11yAuditService(inv);

            var res = svc.Audit("MyPanel");

            Assert.True((bool)res["ok"]);
            Assert.Equal("chrome-devtools-axi", res["driverUsed"]?.ToString());
            var v = (JArray)res["violations"];
            Assert.Single(v);
            Assert.Equal("color-contrast", v[0]["rule"]?.ToString());
            Assert.Equal("serious", v[0]["impact"]?.ToString());
            Assert.Equal(2, (int)v[0]["nodes"]);
            // 1.0 - 0.1 * 1 violation = 0.9
            Assert.Equal(0.9, (double)res["score"], 3);
        }

        [Fact]
        public void A11yAudit_DriverUnavailable_ReturnsSkippedEnvelope()
        {
            var inv = new FakeInvoker { DriverPath = null };
            // No playwright fallback wired → must skip.
            var svc = new A11yAuditService(inv);

            var res = svc.Audit("MyPanel");

            Assert.True((bool)res["skipped"]);
            Assert.Equal("A11yDriverUnavailable", res["code"]?.ToString());
            Assert.NotNull(res["hint"]);
        }

        [Fact]
        public void A11yAudit_NormaliseAxeOutput_AcceptsArrayShape()
        {
            var (violations, score) = A11yAuditService.NormaliseAxeOutput("[{\"rule\":\"r1\",\"impact\":\"minor\"}]");
            Assert.Single(violations);
            Assert.Equal("r1", violations[0]["rule"]?.ToString());
            Assert.Equal(0.9, score, 3);
        }

        [Fact]
        public void A11yAudit_NormaliseAxeOutput_GarbageReturnsEmpty()
        {
            var (violations, score) = A11yAuditService.NormaliseAxeOutput("not json");
            Assert.Empty(violations);
            Assert.Equal(1.0, score, 3);
        }
    }
}
