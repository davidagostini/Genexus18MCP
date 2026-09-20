using System;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class BrowserDriverInvokerTests
    {
        [Fact]
        public void BuildArguments_NativeExecutableUsesQuotedLogicalArguments()
        {
            var args = BrowserDriverProcess.BuildArguments(new[] { "open", "https://example.test/a?x=1&y=2", "C:\\tmp\\a b.png" });
            Assert.Equal("\"open\" \"https://example.test/a?x=1&y=2\" \"C:\\tmp\\a b.png\"", args);
        }

        [Fact]
        public void BuildShimArguments_EscapesCommandMetacharacters()
        {
            var args = BrowserDriverProcess.BuildShimArguments("C:\\tools\\driver.cmd", new[] { "eval", "a & b | c > d" });
            Assert.DoesNotContain(" & ", args);
            Assert.Contains("^&", args);
            Assert.Contains("^|", args);
            Assert.Contains("^>", args);
        }

        [Theory]
        [InlineData("C:\\tools\\driver.cmd", "eval", "bad\r\nvalue")]
        [InlineData("C:\\tools\\driver.cmd", "eval", "bad%PATH%value")]
        public void BuildShimArguments_RejectsControlAndExpansionCharacters(string path, string verb, string value)
        {
            Assert.Throws<ArgumentException>(() => BrowserDriverProcess.BuildShimArguments(path, new[] { verb, value }));
        }

        [Fact]
        public void BuildShimArguments_RejectsUnsupportedDriverExtension()
        {
            Assert.Throws<ArgumentException>(() => BrowserDriverProcess.BuildShimArguments("driver.ps1", new[] { "open" }));
        }

        [Fact]
        public void Invoke_ReturnsExitErrorFromShim()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gx-driver-" + Guid.NewGuid().ToString("N") + ".cmd");
            System.IO.File.WriteAllText(path, "@echo off\r\nexit /b 7\r\n");
            try
            {
                var result = new DefaultBrowserDriverInvoker(path).Invoke("open", 5000);
                Assert.Equal(7, result.ExitCode);
                Assert.False(result.TimedOut);
            }
            finally { try { System.IO.File.Delete(path); } catch { } }
        }

        [Fact]
        public void Invoke_ReturnsTimeoutForHungShim()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gx-driver-" + Guid.NewGuid().ToString("N") + ".cmd");
            System.IO.File.WriteAllText(path, "@echo off\r\nping 127.0.0.1 -n 20 >nul\r\n");
            try
            {
                var result = new DefaultBrowserDriverInvoker(path).Invoke("open", 50);
                Assert.True(result.TimedOut);
                Assert.Equal(-1, result.ExitCode);
            }
            finally { try { System.IO.File.Delete(path); } catch { } }
        }
    }
}
