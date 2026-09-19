using System;
using System.IO;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SdkSurfaceProbeTests
    {
        [Fact]
        public void Run_WhenGxPathDoesNotExist_EmitsWarningInsteadOfSilentReturn()
        {
            string oldGxPath = Environment.GetEnvironmentVariable("GX_PATH");
            string oldGxProgDir = Environment.GetEnvironmentVariable("GX_PROGRAM_DIR");
            string tempOut = Path.Combine(Path.GetTempPath(), "gxmcp_test_probe_" + Guid.NewGuid().ToString("N"));

            try
            {
                string nonExistentPath = Path.Combine(Path.GetTempPath(), "non_existent_gx_" + Guid.NewGuid().ToString("N"));
                Environment.SetEnvironmentVariable("GX_PROGRAM_DIR", nonExistentPath);
                Environment.SetEnvironmentVariable("GX_PATH", nonExistentPath);

                var result = SdkSurfaceProbe.Run(tempOut);

                Assert.NotNull(result);
                Assert.Contains(result.Warnings, w => w.Contains("SDK preloading skipped: GeneXus path not found") && w.Contains(nonExistentPath));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GX_PROGRAM_DIR", oldGxProgDir);
                Environment.SetEnvironmentVariable("GX_PATH", oldGxPath);
                try { if (Directory.Exists(tempOut)) Directory.Delete(tempOut, true); } catch { }
            }
        }
    }
}
