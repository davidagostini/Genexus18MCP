using System;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class SessionKbContextStoreTests
    {
        [Fact]
        public void Initialize_RegistersUncommittedSession()
        {
            var store = new SessionKbContextStore(TimeSpan.FromMinutes(5));
            string sessionId = "session-" + Guid.NewGuid().ToString("N");

            bool added = store.Initialize(sessionId, null);

            Assert.True(added);
            Assert.True(store.TryGet(sessionId, out var alias));
            Assert.Null(alias);
            Assert.Null(store.Get(sessionId));
        }

        [Fact]
        public void Set_UpdatesSessionSelection()
        {
            var store = new SessionKbContextStore(TimeSpan.FromMinutes(5));
            string sessionId = "session-" + Guid.NewGuid().ToString("N");

            store.Initialize(sessionId, null);
            store.Set(sessionId, "order");

            Assert.True(store.TryGet(sessionId, out var alias));
            Assert.Equal("order", alias);
            Assert.Equal("order", store.Get(sessionId));
        }

        [Fact]
        public void Clear_RemovesSession()
        {
            var store = new SessionKbContextStore(TimeSpan.FromMinutes(5));
            string sessionId = "session-" + Guid.NewGuid().ToString("N");

            store.Set(sessionId, "order");
            store.Clear(sessionId);

            Assert.False(store.TryGet(sessionId, out var alias));
            Assert.Null(alias);
        }

        [Fact]
        public void MultipleSessions_AreIsolated()
        {
            var store = new SessionKbContextStore(TimeSpan.FromMinutes(5));
            string sessionA = "session-a";
            string sessionB = "session-b";
            string sessionC = "session-c";

            store.Initialize(sessionA, null);
            store.Set(sessionA, "order");

            store.Initialize(sessionB, null);
            store.Set(sessionB, "customer");

            store.Initialize(sessionC, null);

            Assert.Equal("order", store.Get(sessionA));
            Assert.Equal("customer", store.Get(sessionB));
            Assert.Null(store.Get(sessionC));
        }

        [Fact]
        public void SessionSnapshotCarriesOwnerKbGenerationAndLeaseIndependently()
        {
            var registry = new KbUseLeaseRegistry(new TestClock());
            var store = new SessionKbContextStore(TimeSpan.FromMinutes(5));
            var leaseA = registry.Open("session-a", "orders", 1, "path-a", "open-a", TimeSpan.FromMinutes(1));
            var leaseB = registry.Open("session-b", "customer", 1, "path-b", "open-b", TimeSpan.FromMinutes(1));

            store.Set("session-a", "orders", "orders", leaseA);
            store.Set("session-b", "customer", "customer", leaseB);

            Assert.True(store.TryGetSnapshot("session-a", out var a));
            Assert.True(store.TryGetSnapshot("session-b", out var b));
            Assert.Equal("session-a", a!.OwnerScopeId);
            Assert.Equal("orders", a.KbId);
            Assert.Equal(leaseA.Token, a.Lease!.Token);
            Assert.NotEqual(a.Lease.Token, b!.Lease!.Token);
        }

        [Fact]
        public void ProgramSessionSelection_CanonicalizesLeaseAlias()
        {
            string sessionId = "canonical-alias-" + Guid.NewGuid().ToString("N");
            try
            {
                Program.SetSessionSelectedKb(sessionId, "MC30", "C:/KB/MC30");

                Assert.True(Program.TryGetSessionSnapshotForTest(sessionId, out var snapshot));
                Assert.Equal("mc30", snapshot!.Lease!.KbId);
            }
            finally
            {
                Program.ClearSessionSelectedKb(sessionId);
            }
        }

        [Fact]
        public void StdioSession_DoesNotExpireOnIdle()
        {
            var store = new SessionKbContextStore(TimeSpan.FromMilliseconds(1));
            store.Set("stdio", "invoicing");

            Thread.Sleep(20);

            Assert.True(store.TryGet("stdio", out var alias));
            Assert.Equal("invoicing", alias);
        }

        [Fact]
        public void MultiSession_KbResolver_ResolutionIsolation()
        {
            var cfg = new Configuration
            {
                Environment = new EnvironmentConfig
                {
                    DefaultKb = "customer",
                    KBs =
                    {
                        new KbEntry { Alias = "customer", Path = "C:/KB/Customer" },
                        new KbEntry { Alias = "order", Path = "C:/KB/Order" }
                    }
                }
            };
            var resolver = new KbResolver(cfg);
            var open = new List<KbHandle>
            {
                new KbHandle("customer", "C:/KB/Customer"),
                new KbHandle("order", "C:/KB/Order")
            };

            // Session A has explicitly selected "order"
            var handleA = resolver.Resolve(null, open, null, "order", sessionContextInitialized: true);
            Assert.Equal("order", handleA.Alias);

            // Session B has explicitly selected "customer"
            var handleB = resolver.Resolve(null, open, null, "customer", sessionContextInitialized: true);
            Assert.Equal("customer", handleB.Alias);

            // Session C is initialized without a selection -> fails deterministically with KB_AMBIGUOUS
            var exC = Assert.Throws<KbResolutionException>(
                () => resolver.Resolve(null, open, null, null, sessionContextInitialized: true));
            Assert.Equal("KB_AMBIGUOUS", exC.Code);
            Assert.Contains("order", exC.Message);
            Assert.Contains("customer", exC.Message);
        }
        private sealed class TestClock : IMonotonicClock
        {
            public TimeSpan Now => TimeSpan.Zero;
        }
    }
}
