using System;
using System.Linq;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public sealed class KbUseLeaseRegistryTests
    {
        [Fact]
        public void OpenIsIdempotentPerOwnerIdentityAndClientRequest()
        {
            var clock = new TestMonotonicClock();
            var registry = new KbUseLeaseRegistry(clock);

            var first = registry.Open("owner-a", "kb-1", 7, "identity-a", "request-1", TimeSpan.FromMinutes(1));
            var second = registry.Open("owner-a", "kb-1", 7, "identity-a", "request-1", TimeSpan.FromMinutes(1));

            Assert.Equal(first.Token, second.Token);
            Assert.Equal(KbUseLeaseState.Active, second.State);
            Assert.Equal("owner-a", second.OwnerScopeId);
            Assert.Equal("kb-1", second.KbId);
            Assert.Equal(7, second.ContextGeneration);
            Assert.NotEqual(first.Token, registry.Open("owner-a", "kb-1", 7, "identity-a", "request-2", TimeSpan.FromMinutes(1)).Token);
            Assert.NotEqual(first.Token, registry.Open("owner-b", "kb-1", 7, "identity-a", "request-1", TimeSpan.FromMinutes(1)).Token);
        }

        [Fact]
        public void TokenIsOpaqueRandomAndCannotBeUsedByAnotherOwner()
        {
            var registry = new KbUseLeaseRegistry(new TestMonotonicClock());
            var lease = registry.Open("owner-a", "kb-1", 1, "identity-a", "request-1", TimeSpan.FromMinutes(1));

            Assert.True(lease.Token.Length >= 40);
            Assert.DoesNotContain("owner-a", lease.Token, StringComparison.Ordinal);
            var renew = registry.Renew(lease.Token, "owner-b", TimeSpan.FromMinutes(1));
            var close = registry.Close(lease.Token, "owner-b");

            Assert.Equal(KbUseLeaseOperationStatus.WrongOwner, renew.Status);
            Assert.Equal(KbUseLeaseOperationStatus.WrongOwner, close.Status);
            Assert.Equal(KbUseLeaseState.Active, registry.Get(lease.Token)!.State);
        }

        [Fact]
        public void ExpirationUsesInjectedMonotonicClockAndRenewIsIdempotentByRequest()
        {
            var clock = new TestMonotonicClock();
            var registry = new KbUseLeaseRegistry(clock);
            var lease = registry.Open("owner-a", "kb-1", 3, "identity-a", "open-1", TimeSpan.FromSeconds(10));

            clock.Advance(TimeSpan.FromSeconds(11));
            Assert.Equal(KbUseLeaseState.Expired, registry.Get(lease.Token)!.State);
            Assert.Equal(KbUseLeaseOperationStatus.Expired, registry.Renew(lease.Token, "owner-a", TimeSpan.FromSeconds(10)).Status);

            var fresh = registry.Open("owner-a", "kb-1", 3, "identity-a", "open-2", TimeSpan.FromSeconds(10));
            var renewed = registry.Renew(fresh.Token, "owner-a", TimeSpan.FromSeconds(20), "renew-1");
            clock.Advance(TimeSpan.FromSeconds(15));
            var repeated = registry.Renew(fresh.Token, "owner-a", TimeSpan.FromSeconds(20), "renew-1");

            Assert.Equal(renewed.ExpiresAt, repeated.ExpiresAt);
            Assert.Equal(KbUseLeaseState.Active, registry.Get(fresh.Token)!.State);
        }

        [Fact]
        public void CloseIsIdempotentAndInvalidTokensDoNotTouchOtherLeases()
        {
            var registry = new KbUseLeaseRegistry(new TestMonotonicClock());
            var lease = registry.Open("owner-a", "kb-1", 1, "identity-a", "request-1", TimeSpan.FromMinutes(1));

            var invalid = registry.Close("not-a-token", "owner-a");
            var wrong = registry.Revoke(lease.Token, "owner-b");
            var closed = registry.Close(lease.Token, "owner-a", "close-1");
            var repeated = registry.Close(lease.Token, "owner-a", "close-1");

            Assert.Equal(KbUseLeaseOperationStatus.InvalidToken, invalid.Status);
            Assert.Equal(KbUseLeaseOperationStatus.WrongOwner, wrong.Status);
            Assert.Equal(KbUseLeaseOperationStatus.Success, closed.Status);
            Assert.Equal(closed.State, repeated.State);
            Assert.Equal(KbUseLeaseState.Released, registry.Get(lease.Token)!.State);
        }

        [Fact]
        public void CloseDoesNotReleaseLeaseWhileAnInFlightOperationHoldsIt()
        {
            var registry = new KbUseLeaseRegistry(new TestMonotonicClock());
            var lease = registry.Open("owner-a", "kb-1", 1, "identity-a", "request-1", TimeSpan.FromMinutes(1));

            Assert.True(registry.TryEnterInFlight(lease.Token, "owner-a"));
            var blocked = registry.Close(lease.Token, "owner-a");
            Assert.Equal(KbUseLeaseOperationStatus.InFlight, blocked.Status);
            Assert.Equal(KbUseLeaseState.Active, registry.Get(lease.Token)!.State);

            registry.ExitInFlight(lease.Token, "owner-a");
            Assert.Equal(KbUseLeaseOperationStatus.Success, registry.Close(lease.Token, "owner-a").Status);
            Assert.False(registry.TryEnterInFlight(lease.Token, "owner-a"));
        }

        [Fact]
        public void RevokeAndCloseRemainTerminalAndRenewCannotResurrect()
        {
            var registry = new KbUseLeaseRegistry(new TestMonotonicClock());
            var lease = registry.Open("owner-a", "kb-1", 1, "identity-a", "request-1", TimeSpan.FromMinutes(1));

            Assert.Equal(KbUseLeaseOperationStatus.Success, registry.Revoke(lease.Token, "owner-a").Status);
            Assert.Equal(KbUseLeaseState.Revoked, registry.Get(lease.Token)!.State);
            Assert.Equal(KbUseLeaseOperationStatus.Revoked, registry.Renew(lease.Token, "owner-a", TimeSpan.FromMinutes(1)).Status);
            Assert.Equal(KbUseLeaseOperationStatus.Revoked, registry.Close(lease.Token, "owner-a").Status);
        }

        [Fact]
        public void ConcurrentOpenRequestsProduceOneLeaseForTheSameIdempotencyKey()
        {
            var registry = new KbUseLeaseRegistry(new TestMonotonicClock());
            var tokens = new string[32];

            Parallel.For(0, tokens.Length, i =>
                tokens[i] = registry.Open("owner-a", "kb-1", 1, "identity-a", "request-1", TimeSpan.FromMinutes(1)).Token);

            Assert.All(tokens, token => Assert.Equal(tokens[0], token));
        }

        [Fact]
        public void ValidateRejectsWrongOwnerOldGenerationAndExpiredLease()
        {
            var clock = new TestMonotonicClock();
            var registry = new KbUseLeaseRegistry(clock);
            var lease = registry.Open("owner-a", "kb-1", 4, "identity-a", "request-1", TimeSpan.FromSeconds(5));

            var wrongOwner = Assert.Throws<KbLeaseValidationException>(() =>
                registry.Validate(lease.Token, "owner-b", "kb-1", 4, "identity-a"));
            Assert.Equal("KB_NOT_OWNED", wrongOwner.Code);

            var oldGeneration = Assert.Throws<KbLeaseValidationException>(() =>
                registry.Validate(lease.Token, "owner-a", "kb-1", 3, "identity-a"));
            Assert.Equal("KB_LEASE_INVALID", oldGeneration.Code);

            clock.Advance(TimeSpan.FromSeconds(6));
            var expired = Assert.Throws<KbLeaseValidationException>(() =>
                registry.Validate(lease.Token, "owner-a", "kb-1", 4, "identity-a"));
            Assert.Equal("KB_LEASE_EXPIRED", expired.Code);
        }

        [Theory]
        [InlineData("KBTeste", "kbteste")]
        [InlineData("  Orders ", "orders")]
        public void KbAliasesAreCanonicalizedBeforeLeaseIdentityIsUsed(string input, string expected)
        {
            Assert.Equal(expected, Program.CanonicalizeKbAlias(input));
        }

        private sealed class TestMonotonicClock : IMonotonicClock
        {
            public TimeSpan Now { get; private set; }
            public void Advance(TimeSpan amount) => Now += amount;
        }
    }
}
