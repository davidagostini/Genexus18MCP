using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Tests
{
    public class WorkerSupervisorTests
    {
        [Fact]
        public async Task WorkerSupervisor_AcquireAsync_AcquiresNewWorker_ViaSpawnSeam()
        {
            var config = new Configuration();
            var supervisor = new WorkerSupervisor(config);

            var kb = new KbHandle("test-kb", @"C:\Models\TestKb");
            var mockWorker = new WorkerProcess(config, kb);

            supervisor.SpawnFactory = handle => mockWorker;

            var acquired = await supervisor.AcquireAsync(kb, CancellationToken.None);

            Assert.NotNull(acquired);
            Assert.Equal(mockWorker, acquired);
            Assert.True(supervisor.ListKnown().Count >= 1);
            Assert.Equal("test-kb", supervisor.ListKnown()[0].Alias);
        }

        [Fact]
        public async Task WorkerSupervisor_CapacityEviction_EvictsOldestWorker_WhenMaxReached()
        {
            var config = new Configuration();
            config.Server = new ServerConfig { MaxOpenKbs = 2 };

            var supervisor = new WorkerSupervisor(config);

            var kb1 = new KbHandle("kb1", @"C:\Models\Kb1");
            var kb2 = new KbHandle("kb2", @"C:\Models\Kb2");
            var kb3 = new KbHandle("kb3", @"C:\Models\Kb3");

            var w1 = new WorkerProcess(config, kb1);
            var w2 = new WorkerProcess(config, kb2);
            var w3 = new WorkerProcess(config, kb3);

            supervisor.SpawnFactory = h =>
            {
                if (h.Alias == "kb1") return w1;
                if (h.Alias == "kb2") return w2;
                return w3;
            };

            await supervisor.AcquireAsync(kb1, CancellationToken.None);
            await Task.Delay(20);
            await supervisor.AcquireAsync(kb2, CancellationToken.None);

            Assert.True(supervisor.IsAtCapacity());

            // Acquiring 3rd when MaxOpenKbs is 2 should evict oldest (kb1)
            await Task.Delay(20);
            await supervisor.AcquireAsync(kb3, CancellationToken.None);

            var open = supervisor.ListOpen();
            Assert.Equal(2, open.Count);
            Assert.Contains(open, h => h.Alias == "kb2");
            Assert.Contains(open, h => h.Alias == "kb3");
            Assert.DoesNotContain(open, h => h.Alias == "kb1");
        }

        [Fact]
        public void WorkerSupervisor_TracksKnownKbs_DurablyAcrossExits()
        {
            var config = new Configuration();
            var supervisor = new WorkerSupervisor(config);

            var kb = new KbHandle("durable-kb", @"C:\Models\DurableKb");
            var worker = new WorkerProcess(config, kb);

            supervisor.SpawnFactory = h => worker;

            // Register known
            supervisor.RegisterKnown(kb);

            Assert.Single(supervisor.ListKnown());
            Assert.Equal("durable-kb", supervisor.ListKnown()[0].Alias);

            // Live worker is not yet present
            Assert.Null(supervisor.TryGet("durable-kb"));
        }
    }
}
