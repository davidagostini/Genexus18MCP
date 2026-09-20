using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class SharedWorkerRegistryTests
    {
        [Fact]
        public void Identity_NormalizesPathsAndDriverMetadata()
        {
            var first = SharedWorkerIdentity.Create(
                @"C:\Worker\GxMcp.Worker.exe\",
                @"C:\KBs\Shared\",
                @"C:\GeneXus18\",
                "Native-SDK",
                "18");
            var second = SharedWorkerIdentity.Create(
                @"c:\worker\GxMcp.Worker.exe",
                @"c:\kbs\shared",
                @"c:\genexus18",
                "native-sdk",
                "18");

            Assert.Equal(first.Key, second.Key);
            Assert.NotEqual(first.Key, SharedWorkerIdentity.Create(
                @"c:\worker\GxMcp.Worker.exe",
                @"c:\kbs\shared",
                @"c:\genexus17",
                "native-sdk",
                "18").Key);
            Assert.NotEqual(first.Key, SharedWorkerIdentity.Create(
                @"c:\worker\GxMcp.Worker.exe",
                @"c:\kbs\shared",
                @"c:\genexus18",
                "native-sdk",
                "18",
                @"c:\profiles\other.json").Key);
        }

        [Fact]
        public void Record_IsLive_RequiresPidAndStartTimeMatch()
        {
            using (var process = Process.GetCurrentProcess())
            {
                var identity = SharedWorkerIdentity.Create(
                    process.MainModule!.FileName!,
                    @"C:\KBs\Shared",
                    @"C:\GeneXus18",
                    "native-sdk",
                    "18");
                var record = new SharedWorkerRecord
                {
                    Identity = identity,
                    HostPid = process.Id,
                    HostStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                    UpdatedUtc = DateTime.UtcNow
                };

                Assert.True(SharedWorkerRegistry.IsSameProcess(process, record.HostPid, record.HostStartTimeUtcTicks));
                Assert.False(SharedWorkerRegistry.IsSameProcess(process, record.HostPid, record.HostStartTimeUtcTicks + 1));
            }
        }

        [Fact]
        public void Record_RoundTripsAtomicallyWithoutChangingIdentity()
        {
            string dir = Path.Combine(Path.GetTempPath(), "gxmcp-shared-registry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "worker.json");
            try
            {
                var record = new SharedWorkerRecord
                {
                    Identity = SharedWorkerIdentity.Create(
                        @"C:\Worker\GxMcp.Worker.exe",
                        @"C:\KBs\Shared",
                        @"C:\GeneXus18",
                        "native-sdk",
                        "18"),
                    HostPid = 123,
                    HostStartTimeUtcTicks = 456,
                    WorkerPid = 789,
                    WorkerStartTimeUtcTicks = 987,
                    PipeName = "GxMcpShared_test",
                    Generation = 3,
                    UpdatedUtc = DateTime.UtcNow
                };

                SharedWorkerRegistry.WriteRecord(path, record);
                var loaded = SharedWorkerRegistry.ReadRecord(path);

                Assert.NotNull(loaded);
                Assert.Equal(record.Identity.Key, loaded!.Identity.Key);
                Assert.Equal(record.PipeName, loaded.PipeName);
                Assert.Equal(record.Generation, loaded.Generation);
                Assert.False(File.Exists(path + ".tmp"));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
        [Fact]
        public void Reservation_NamedMutexAllowsOnlyOneConcurrentElector()
        {
            var identity = SharedWorkerIdentity.Create(
                @"C:\Worker\GxMcp.Worker.exe",
                Path.Combine(Path.GetTempPath(), "gxmcp-reservation-" + Guid.NewGuid().ToString("N")),
                @"C:\GeneXus18",
                "native-sdk",
                "18");
            using (var ready = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            {
                Exception? firstException = null;
                var firstThread = new Thread(() =>
                {
                    try
                    {
                        using (var first = SharedWorkerRegistry.TryReserve(identity, TimeSpan.FromSeconds(2), out _))
                        {
                            if (first == null) throw new InvalidOperationException("first elector did not acquire the reservation");
                            ready.Set();
                            release.Wait();
                        }
                    }
                    catch (Exception ex)
                    {
                        firstException = ex;
                        ready.Set();
                    }
                });
                firstThread.IsBackground = true;
                firstThread.Start();

                try
                {
                    Assert.True(ready.Wait(TimeSpan.FromSeconds(2)));
                    if (firstException != null) throw firstException;
                    SharedWorkerRegistryReservation? second = null;
                    Exception? secondException = null;
                    using (var secondReady = new ManualResetEventSlim(false))
                    {
                        var secondThread = new Thread(() =>
                        {
                            try { second = SharedWorkerRegistry.TryReserve(identity, TimeSpan.FromMilliseconds(100), out _); }
                            catch (Exception ex) { secondException = ex; }
                            finally { secondReady.Set(); }
                        });
                        secondThread.IsBackground = true;
                        secondThread.Start();
                        Assert.True(secondReady.Wait(TimeSpan.FromSeconds(2)));
                        secondThread.Join();
                    }
                    if (secondException != null) throw secondException;
                    Assert.Null(second);
                }
                finally
                {
                    release.Set();
                    firstThread.Join(TimeSpan.FromSeconds(2));
                }
            }
        }
    }
}
