using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Microsoft.CSharp;
using Xunit;

namespace GxMcp.Worker.Tests
{
    [Trait("Category", "ProcessSmoke")]
    public class SourceStoreProcessTests
    {
        [Fact]
        public void RemovalQueuedBeforeDisposeCannotDeleteTheNextOwnersRecord()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-remove-owner-" + Guid.NewGuid().ToString("N"));
            var old = new SourceStoreService(root, true);
            var flush = typeof(SourceStoreService).GetField("_flushGate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(old);
            SourceStoreService next = null;
            Thread remover = null;
            bool removed = true;
            try
            {
                Assert.True(old.Put("same-object", "Source", "old", null, "v1"));
                lock (flush)
                {
                    using (var started = new ManualResetEventSlim())
                    {
                        remover = new Thread(() => { started.Set(); removed = old.RemoveObject("same-object"); }) { IsBackground = true };
                        remover.Start();
                        Assert.True(started.Wait(10000));
                        Assert.False(remover.Join(200));
                        old.Dispose();
                        next = new SourceStoreService(root, true);
                        Assert.True(next.Put("same-object", "Source", "new", null, "v2"));
                        next.FlushCatalog();
                    }
                }
                Assert.True(remover.Join(10000));
                Assert.False(removed);
                Assert.True(next.TryGet("same-object", "Source", out string source));
                Assert.Equal("new", source);
            }
            finally { old.Dispose(); next?.Dispose(); Directory.Delete(root, true); }
        }

        [Fact]
        public void InFlightPutRetainsOwnershipUntilRecordAndCatalogAreUpdated()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-put-owner-" + Guid.NewGuid().ToString("N"));
            var store = new SourceStoreService(root, true);
            var flush = typeof(SourceStoreService).GetField("_flushGate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(store);
            var io = typeof(SourceStoreService).GetField("_ioGate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(store);
            bool written = false;
            bool flushHeldByPut = false;
            Thread writer = null, disposer = null;
            try
            {
                lock (io)
                {
                    using (var started = new ManualResetEventSlim())
                    {
                        writer = new Thread(() => { started.Set(); written = store.Put("in-flight", "Source", "complete", null, "v1"); }) { IsBackground = true };
                        writer.Start();
                        Assert.True(started.Wait(10000));
                        Assert.False(writer.Join(200));
                        bool acquired = Monitor.TryEnter(flush, 1000);
                        if (acquired) Monitor.Exit(flush);
                        flushHeldByPut = !acquired;
                        disposer = new Thread(store.Dispose) { IsBackground = true };
                        disposer.Start();
                    }
                }
                Assert.True(writer.Join(10000));
                Assert.True(disposer.Join(10000));
                Assert.True(flushHeldByPut, "An in-flight record write must retain the ownership gate until the catalog is updated.");
                Assert.True(written);
                using (var next = new SourceStoreService(root, true))
                {
                    Assert.True(next.IsAvailable);
                    Assert.True(next.TryGet("in-flight", "Source", out string source));
                    Assert.Equal("complete", source);
                }
            }
            finally { store.Dispose(); Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RemoveAndDisposeWaitForFlushWithoutHoldingIoLock(bool dispose)
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-flush-order-" + Guid.NewGuid().ToString("N"));
            var store = new SourceStoreService(root, true);
            var flush = typeof(SourceStoreService).GetField("_flushGate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(store);
            var io = typeof(SourceStoreService).GetField("_ioGate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(store);
            Thread work = null;
            Exception failure = null;
            bool acquired = false;
            try
            {
                lock (flush)
                {
                    using (var started = new ManualResetEventSlim())
                    {
                        work = new Thread(() => {
                            started.Set();
                            try { if (dispose) store.Dispose(); else store.RemoveObject(Guid.NewGuid().ToString()); }
                            catch (Exception ex) { failure = ex; }
                        }) { IsBackground = true };
                        work.Start();
                        Assert.True(started.Wait(10000));
                        Assert.False(work.Join(200), "Flush is held, so removal/disposal must wait.");
                        acquired = Monitor.TryEnter(io, 1000);
                        if (acquired) Monitor.Exit(io);
                    }
                }
                Assert.True(work.Join(10000), "Removal/disposal did not finish after releasing flush.");
                Assert.Null(failure);
                Assert.True(acquired, "Lock inversion: removal/disposal held IO while waiting for flush.");
            }
            finally
            {
                store.Dispose();
                Directory.Delete(root, true);
            }
        }

        [Theory]
        [InlineData("current", true)]
        [InlineData("stale", false)]
        [InlineData("refreshing", false)]
        public void DeltaDeletionAfterStartOnlyAdvancesWhenCurrentCatalogConfirmsAbsence(string freshness, bool removed)
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-delta-deleted-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var store = new SourceStoreService(root, true))
                {
                    var first = new SearchIndex.IndexEntry { Guid = Guid.NewGuid().ToString(), Name = "First", Type = "Procedure", LastUpdate = DateTime.UtcNow };
                    var gone = new SearchIndex.IndexEntry { Guid = Guid.NewGuid().ToString(), Name = "Gone", Type = "Procedure", LastUpdate = DateTime.UtcNow.AddMinutes(-1) };
                    var current = new SearchIndex();
                    current.Objects["First"] = first; current.Objects["Gone"] = gone;
                    Assert.True(store.Put(gone.Guid, "Source", "retain-until-confirmed", gone.LastUpdate, "v1"));
                    Assert.True(store.Put(gone.Guid, "Rules", "retain-rules", gone.LastUpdate, "v1"));
                    var fill = new SourceStoreBackfillService(store);
                    Assert.True(fill.Start(() => new[] { first, gone }, e => e.Guid == gone.Guid ? null
                        : new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "first", LastUpdate = e.LastUpdate } },
                        schedule: false, confirmedAbsent: e => ObjectService.IsConfirmedAbsentFromCurrentIndex(e, current, freshness)));
                    Assert.True(fill.RunOneSliceForTest(1, 1000));
                    Assert.Equal(1, fill.GetState().CursorIndex);
                    current.Objects.TryRemove("Gone", out _);
                    Assert.Equal(removed, fill.RunOneSliceForTest(1, 1000));
                    Assert.Equal(removed ? 2 : 1, fill.GetState().CursorIndex);
                    Assert.Equal(removed ? 1 : 0, fill.GetState().RemovedObjects);
                    Assert.Equal(!removed, store.TryGet(gone.Guid, "Source", out _));
                    Assert.Equal(!removed, store.TryGet(gone.Guid, "Rules", out _));
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Fact]
        public void IndependentProcessesCannotShareOwnershipAndAbruptExitPreservesWarmState()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-owner-process-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string storeRoot = Path.Combine(root, "store");
            string guid = Guid.NewGuid().ToString();
            Process first = null, second = null, restarted = null;
            try
            {
                using (var seed = new SourceStoreService(storeRoot, true))
                {
                    var entry = new SearchIndex.IndexEntry { Guid = guid, Name = "Synthetic", Type = "Procedure", LastUpdate = DateTime.UtcNow };
                    var fill = new SourceStoreBackfillService(seed);
                    Assert.True(fill.Start(() => new[] { entry }, e => new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "preserved", LastUpdate = e.LastUpdate } }, schedule: false));
                    Assert.True(fill.RunOneSliceForTest(1, 1000));
                }
                string exe = CompileOwner(root);
                first = StartOwner(exe, storeRoot, guid);
                Assert.Equal("True|preserved|1", ReadLine(first));
                second = StartOwner(exe, storeRoot, guid);
                Assert.Equal("False", ReadLine(second));
                // The OS must release FileShare.None even when Dispose never runs.
                first.Kill();
                Assert.True(first.WaitForExit(10000));
                restarted = StartOwner(exe, storeRoot, guid);
                Assert.Equal("True|preserved|1", ReadLine(restarted));
            }
            finally
            {
                Stop(first); Stop(second); Stop(restarted);
                Directory.Delete(root, true);
            }
        }

        private static string CompileOwner(string root)
        {
            string exe = Path.Combine(root, "owner.exe");
            const string code = @"
using System;
using System.Reflection;
class Program {
 static void Main(string[] args) {
  var assembly = Assembly.LoadFrom(args[0]);
  var type = assembly.GetType(""GxMcp.Worker.Services.SourceStoreService"", true);
  var store = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
    new[] { typeof(string), typeof(bool) }, null).Invoke(new object[] { args[1], true });
  bool available = (bool)type.GetProperty(""IsAvailable"").GetValue(store, null);
  if (available) {
   var parameters = new object[] { args[2], ""Source"", null };
   type.GetMethod(""TryGet"", new[] { typeof(string), typeof(string), typeof(string).MakeByRefType() }).Invoke(store, parameters);
   var fillType = assembly.GetType(""GxMcp.Worker.Services.SourceStoreBackfillService"", true);
   var fill = Activator.CreateInstance(fillType, store);
   var state = fillType.GetMethod(""GetState"").Invoke(fill, null);
   Console.WriteLine(""True|"" + parameters[2] + ""|"" + state.GetType().GetProperty(""CursorIndex"").GetValue(state, null));
  } else Console.WriteLine(""False"");
  Console.Out.Flush(); Console.ReadLine();
  ((IDisposable)store).Dispose();
 }
}";
            using (var compiler = new CSharpCodeProvider())
            {
                var parameters = new CompilerParameters(new[] { "System.dll" }, exe) { GenerateExecutable = true, CompilerOptions = "/platform:x86" };
                var result = compiler.CompileAssemblyFromSource(parameters, code);
                Assert.False(result.Errors.HasErrors, string.Join(Environment.NewLine, result.Errors));
            }
            return exe;
        }

        private static Process StartOwner(string exe, string root, string guid)
            => Process.Start(new ProcessStartInfo(exe,
                "\"" + new Uri(typeof(SourceStoreService).Assembly.CodeBase).LocalPath + "\" \"" + root + "\" " + guid)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true });

        private static string ReadLine(Process process)
        {
            var read = process.StandardOutput.ReadLineAsync();
            Assert.True(read.Wait(10000), "Source-store child did not answer within the bounded wait.");
            Assert.True(read.Result != null, "Source-store child exited without an answer: " + (read.Result == null ? process.StandardError.ReadToEnd() : ""));
            return read.Result;
        }

        private static void Stop(Process process)
        {
            if (process == null) return;
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine();
                if (!process.WaitForExit(10000)) { process.Kill(); process.WaitForExit(10000); }
            }
            process.Dispose();
        }
    }
}
