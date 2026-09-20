using System;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // v2.8.0 (#37) — when a duplicate clientRequestId arrives WHILE the
    // first call is still executing, the duplicate must block on the
    // in-flight signal and return the first call's response when it lands.
    // No double-apply.
    public class IdempotencyInflightTests
    {
        public IdempotencyInflightTests() => IdempotencyCache.Clear();

        [Fact]
        public async Task TryServe_WhileInflight_WaitsAndReplays()
        {
            const string id = "race-1";
            string firstResponse = "{\"status\":\"ok\",\"code\":\"WriteApplied\",\"target\":\"X\"}";

            IdempotencyCache.BeginInflight(id);

            // Start the duplicate on a background thread; it should block
            // until the first call's Store() releases the in-flight signal.
            string sawByDuplicate = null;
            var dupReady = new ManualResetEventSlim(false);
            var t = Task.Run(() =>
            {
                dupReady.Set();
                sawByDuplicate = IdempotencyCache.TryServe(id);
            });

            // Wait until the duplicate is parked on the signal.
            dupReady.Wait();
            await Task.Delay(80); // give the dup time to actually call TryServe

            // The duplicate has NOT received anything yet — store hasn't happened.
            Assert.Null(sawByDuplicate);

            // Now simulate the first call finishing.
            IdempotencyCache.Store(id, firstResponse);

            // Duplicate must wake up and pick up the replay within a sane budget.
            var completed = await Task.WhenAny(t, Task.Delay(2000));
            Assert.Same(t, completed);
            Assert.NotNull(sawByDuplicate);
            var obj = JObject.Parse(sawByDuplicate);
            Assert.Equal("WriteApplied", (string)obj["code"]);
            Assert.True((bool)obj["_meta"]["replayed"]);
        }

        [Fact]
        public async Task Abort_ReleasesInflightWithoutCaching()
        {
            const string id = "abort-1";
            IdempotencyCache.BeginInflight(id);

            string seen = null;
            var t = Task.Run(() => { seen = IdempotencyCache.TryServe(id); });
            await Task.Delay(50);

            // Abort instead of Store — the duplicate should wake but find no
            // cache entry, so it returns null and the caller will execute
            // fresh.
            IdempotencyCache.AbortInflight(id);
            var completed = await Task.WhenAny(t, Task.Delay(2000));
            Assert.Same(t, completed);
            Assert.Null(seen);
            Assert.Null(IdempotencyCache.TryServe(id));
        }

        [Fact]
        public void Store_AfterInflightBegun_ServesReplay()
        {
            const string id = "seq-1";
            IdempotencyCache.BeginInflight(id);
            IdempotencyCache.Store(id, "{\"status\":\"ok\",\"code\":\"X\"}");
            var replay = JObject.Parse(IdempotencyCache.TryServe(id));
            Assert.Equal("X", (string)replay["code"]);
            Assert.True((bool)replay["_meta"]["replayed"]);
        }

        [Fact]
        public async Task Multiple_Waiters_AllReceiveSameReplay()
        {
            const string id = "many-1";
            IdempotencyCache.BeginInflight(id);

            string[] seen = new string[3];
            var ready = new CountdownEvent(3);
            var tasks = new[]
            {
                Task.Run(() => { ready.Signal(); seen[0] = IdempotencyCache.TryServe(id); }),
                Task.Run(() => { ready.Signal(); seen[1] = IdempotencyCache.TryServe(id); }),
                Task.Run(() => { ready.Signal(); seen[2] = IdempotencyCache.TryServe(id); }),
            };
            ready.Wait();
            await Task.Delay(80);
            IdempotencyCache.Store(id, "{\"status\":\"ok\",\"code\":\"OnceOnly\"}");

            var allTasks = Task.WhenAll(tasks);
            var completed = await Task.WhenAny(allTasks, Task.Delay(3000));
            Assert.Same(allTasks, completed);
            foreach (var s in seen)
            {
                Assert.NotNull(s);
                var j = JObject.Parse(s);
                Assert.Equal("OnceOnly", (string)j["code"]);
                Assert.True((bool)j["_meta"]["replayed"]);
            }
        }
    }
}
