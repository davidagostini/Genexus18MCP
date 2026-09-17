using System;
using System.Collections;
using System.Reflection;
using GxMcp.Gateway;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    [Collection("Gateway route state")]
    public sealed class Issue225ExplicitKbLeaseRefreshTests
    {
        [Fact]
        public void MatchingExplicitKb_RenewsLeaseWithoutChangingContext()
        {
            string sessionId = NewSessionId();
            try
            {
                Program.SetSessionSelectedKb(sessionId, "Orders", @"C:\KB\Orders");
                Assert.True(Program.TryGetSessionSnapshotForTest(sessionId, out var before));
                var beforeLease = GetLease(before!.Lease!.Token);

                Program.RefreshSessionLeaseForExplicitKb(
                    sessionId,
                    new KbHandle("orders", @"c:\KB\Orders\"),
                    requiresSessionLease: false);

                Assert.True(Program.TryGetSessionSnapshotForTest(sessionId, out var after));
                var afterLease = GetLease(after!.Lease!.Token);
                Assert.Equal(before.Lease.Token, after.Lease.Token);
                Assert.Equal(before.ContextGeneration, after.ContextGeneration);
                Assert.True(afterLease!.ExpiresAt > beforeLease!.ExpiresAt);
                Assert.Equal("active", Program.GetSessionLeaseState(sessionId));
            }
            finally
            {
                Program.ClearSessionSelectedKb(sessionId);
            }
        }

        [Fact]
        public void ExplicitStatefulKb_AdoptsDifferentTarget()
        {
            string sessionId = NewSessionId();
            try
            {
                Program.SetSessionSelectedKb(sessionId, "orders", @"C:\KB\Orders");
                Assert.True(Program.TryGetSessionSnapshotForTest(sessionId, out var before));

                Program.RefreshSessionLeaseForExplicitKb(
                    sessionId,
                    new KbHandle("Customer", @"C:\KB\Customer\"),
                    requiresSessionLease: true);

                Assert.True(Program.TryGetSessionSnapshotForTest(sessionId, out var after));
                Assert.NotEqual(before!.Lease!.Token, after!.Lease!.Token);
                Assert.True(after.ContextGeneration > before.ContextGeneration);
                Assert.Equal("customer", after.Lease!.KbId);
                Assert.Equal(@"c:\kb\customer", after.Lease.Identity);
                Assert.Equal("active", Program.GetSessionLeaseState(sessionId));
            }
            finally
            {
                Program.ClearSessionSelectedKb(sessionId);
            }
        }

        [Fact]
        public void ExpiredExplicitStatefulKb_RecoversWithFreshLease()
        {
            string sessionId = NewSessionId();
            try
            {
                Program.SetSessionSelectedKb(sessionId, "orders", @"C:\KB\Orders");
                Assert.True(Program.TryGetSessionSnapshotForTest(sessionId, out var before));
                ExpireLease(before!.Lease!.Token);

                Program.RefreshSessionLeaseForExplicitKb(
                    sessionId,
                    new KbHandle("orders", @"C:\KB\Orders\"),
                    requiresSessionLease: true);

                Assert.True(Program.TryGetSessionSnapshotForTest(sessionId, out var after));
                Assert.NotEqual(before.Lease.Token, after!.Lease!.Token);
                Assert.True(after.ContextGeneration > before.ContextGeneration);
                Assert.Equal("active", Program.GetSessionLeaseState(sessionId));
            }
            finally
            {
                Program.ClearSessionSelectedKb(sessionId);
            }
        }

        [Fact]
        public void ExplicitStatelessDifferentKb_DoesNotChangeSessionSelection()
        {
            string sessionId = NewSessionId();
            try
            {
                Program.SetSessionSelectedKb(sessionId, "orders", @"C:\KB\Orders");
                Assert.True(Program.TryGetSessionSnapshotForTest(sessionId, out var before));

                Program.RefreshSessionLeaseForExplicitKb(
                    sessionId,
                    new KbHandle("customer", @"C:\KB\Customer\"),
                    requiresSessionLease: false);

                Assert.True(Program.TryGetSessionSnapshotForTest(sessionId, out var after));
                Assert.Equal(before!.Lease!.Token, after!.Lease!.Token);
                Assert.Equal("orders", after.Lease!.KbId);
                Assert.Equal(before.ContextGeneration, after.ContextGeneration);
            }
            finally
            {
                Program.ClearSessionSelectedKb(sessionId);
            }
        }

        private static string NewSessionId() =>
            "issue225-" + Guid.NewGuid().ToString("N");

        private static KbUseLease GetLease(string token)
        {
            var field = typeof(Program).GetField("_kbLeases", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var registry = (KbUseLeaseRegistry)field!.GetValue(null)!;
            return registry.Get(token)!;
        }

        private static void ExpireLease(string token)
        {
            var field = typeof(Program).GetField("_kbLeases", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var registry = (KbUseLeaseRegistry)field!.GetValue(null)!;
            var tokenMapField = typeof(KbUseLeaseRegistry).GetField("_byToken", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(tokenMapField);
            var tokenMap = (IDictionary)tokenMapField!.GetValue(registry)!;
            var entry = tokenMap[token];
            Assert.NotNull(entry);
            var expiresAtField = entry!.GetType().GetField("ExpiresAt", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(expiresAtField);
            expiresAtField!.SetValue(entry, TimeSpan.Zero);
        }
    }
}
