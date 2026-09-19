using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class MtaCommandExecutorTests
    {
        [Fact]
        public async Task BurstNeverExceedsConfiguredActiveCeiling()
        {
            using (var executor = new MtaCommandExecutor(3))
            {
                int active = 0;
                int maximum = 0;
                var responses = new ConcurrentDictionary<int, int>();
                var finished = new CountdownEvent(24);
                var accepted = new List<bool>();
                for (int i = 0; i < 24; i++)
                {
                    int responseId = i;
                    accepted.Add(executor.TrySubmit(() =>
                    {
                        int now = Interlocked.Increment(ref active);
                        UpdateMaximum(ref maximum, now);
                        Thread.Sleep(15);
                        Interlocked.Decrement(ref active);
                        Assert.True(responses.TryAdd(responseId, 1));
                        finished.Signal();
                    }));
                }

                Assert.Equal(24, accepted.Count(x => x));
                Assert.True(finished.Wait(TimeSpan.FromSeconds(2)));
                Assert.Equal(3, maximum);
                Assert.Equal(24, responses.Count);
                Assert.All(responses.Values, value => Assert.Equal(1, value));
                Assert.Equal(0, executor.ActiveCount);
            }
        }

        [Fact]
        public void HighPriorityHealthRunsBeforeQueuedNormalWork()
        {
            using (var executor = new MtaCommandExecutor(1))
            {
                var order = new ConcurrentQueue<string>();
                var firstStarted = new ManualResetEventSlim(false);
                var releaseFirst = new ManualResetEventSlim(false);
                var done = new CountdownEvent(3);

                Assert.True(executor.TrySubmit(() =>
                {
                    firstStarted.Set();
                    releaseFirst.Wait();
                    order.Enqueue("first");
                    done.Signal();
                }));
                Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(1)));
                Assert.True(executor.TrySubmit(() => { order.Enqueue("normal"); done.Signal(); }, highPriority: false));
                Assert.True(executor.TrySubmit(() => { order.Enqueue("health"); done.Signal(); }, highPriority: true));

                releaseFirst.Set();
                Assert.True(done.Wait(TimeSpan.FromSeconds(2)));
                Assert.Equal(new[] { "first", "health", "normal" }, order.ToArray());
            }
        }

        [Fact]
        public void CancellationBeforeStartDoesNotRunAndReleasesAdmission()
        {
            using (var executor = new MtaCommandExecutor(1))
            using (var cts = new CancellationTokenSource())
            {
                var release = new ManualResetEventSlim(false);
                Assert.True(executor.TrySubmit(() => release.Wait(), cancellationToken: default(CancellationToken)));
                cts.Cancel();
                int calls = 0;
                Assert.True(executor.TrySubmit(() => Interlocked.Increment(ref calls), cancellationToken: cts.Token));
                release.Set();
                Assert.True(SpinWait.SpinUntil(() => executor.ActiveCount == 0, 2000));
                Assert.Equal(0, calls);
                Assert.True(executor.TrySubmit(() => { }));
            }
        }

        [Fact]
        public void DisposeWaitsForAcceptedWorkAndRejectsNewWork()
        {
            var executor = new MtaCommandExecutor(2);
            int calls = 0;
            Assert.True(executor.TrySubmit(() => { Thread.Sleep(20); Interlocked.Increment(ref calls); }));
            executor.Dispose();
            Assert.Equal(1, calls);
            Assert.False(executor.TrySubmit(() => { }));
        }

        private static void UpdateMaximum(ref int target, int value)
        {
            while (true)
            {
                int current = Volatile.Read(ref target);
                if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current) return;
            }
        }
    }
}
