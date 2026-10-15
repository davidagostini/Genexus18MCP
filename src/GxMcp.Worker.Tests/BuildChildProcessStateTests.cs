using System.Diagnostics;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    [Trait("Category", "ProcessSmoke")]
    public class BuildChildProcessStateTests
    {
        [Fact]
        public void KillTreeRecordsRootRequestOnlyWhenRootWasStillAlive()
        {
            using (var process = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c exit 0")
                { UseShellExecute = false, CreateNoWindow = true }))
            {
                Assert.True(process.WaitForExit(5000));
                BuildService.KillProcessTree(process, out bool requested);
                Assert.False(requested);
                Assert.Equal("exited", BuildService.ResolveChildProcessState(process, requested));
            }
        }

        [Fact]
        public void KillTreeConfirmsExitSeparatelyFromAcceptedRootKillRequest()
        {
            using (var process = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c pause")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true }))
            {
                try
                {
                    Assert.False(process.HasExited);
                    BuildService.KillProcessTree(process, out bool requested);
                    Assert.True(requested);
                    Assert.True(process.WaitForExit(5000));
                    Assert.Equal("terminated", BuildService.ResolveChildProcessState(process, requested));
                }
                finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LiveRootIsRunningEvenAfterTerminationRequest(bool requested)
        {
            using (var process = Process.GetCurrentProcess())
            {
                Assert.Equal("running", BuildService.ResolveChildProcessState(process, requested));
                var status = new BuildService.BuildTaskStatus { Status = "Running", Process = process };
                Assert.True(BuildService.TrySetWatchdogFailure(status, phase => "watchdog", terminationRequested: requested));
                Assert.Equal("running", status.ChildProcessState);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void UnobservableRootIsUnknownEvenAfterTerminationRequest(bool requested)
        {
            Assert.Equal("unknown", BuildService.ResolveChildProcessState(null, requested));
            using (var process = new Process())
                Assert.Equal("unknown", BuildService.ResolveChildProcessState(process, requested));
        }

        [Theory]
        [InlineData(false, "exited")]
        [InlineData(true, "terminated")]
        public void ObservedExitDistinguishesRequestFromNaturalExit(bool requested, string expected)
        {
            using (var process = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c exit 0")
                { UseShellExecute = false, CreateNoWindow = true }))
            {
                Assert.True(process.WaitForExit(5000));
                Assert.Equal(expected, BuildService.ResolveChildProcessState(process, requested));
                var status = new BuildService.BuildTaskStatus { Status = "Running", Process = process };
                Assert.True(BuildService.TrySetWatchdogFailure(status, phase => "watchdog", terminationRequested: requested));
                Assert.Equal(expected, status.ChildProcessState);
            }
        }
    }
}
