using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public sealed class ScopedReceiptPersistenceTests
    {
        [Fact]
        public async Task IdempotencyReceiptSurvivesRestartAndUsesKbGenerationOwner()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-scoped-receipt-" + Guid.NewGuid().ToString("N"));
            int executions = 0;
            try
            {
                using var scope = StateScope.Create(root);
                var first = new IdempotencyCache(1, 8, TimeSpan.FromSeconds(1), scope, "kb-a", 4);
                await first.GetOrCompute(scope.Id, "kb-a", 4, "edit", "receipt-1", "payload", () =>
                {
                    executions++;
                    return Task.FromResult(new JObject { ["ok"] = true });
                });

                var restarted = new IdempotencyCache(1, 8, TimeSpan.FromSeconds(1), scope, "kb-a", 4);
                await Assert.ThrowsAsync<UsageException>(() => restarted.GetOrCompute(
                    scope.Id, "kb-a", 4, "edit", "receipt-1", "payload", () =>
                    {
                        executions++;
                        return Task.FromResult(new JObject { ["ok"] = true });
                    }));
                Assert.Equal(1, executions);
                Assert.Contains(scope.Id.ToString(), scope.JournalPath("kb-a", 4));
                Assert.Contains("g4", scope.JournalPath("kb-a", 4));
            }
            finally { try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void RecoveryJournalRejectsAnotherScopeOnReadBack()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-scoped-recovery-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var scopeA = StateScope.Create(root, StateScopeId.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
                var ownerA = scopeA.ForKb("kb-a", 1);
                var path = scopeA.RecoveryPath("kb-a", 1);
                var first = new MutationRecoveryRegistry(scopeA, "kb-a", 1);
                first.RequireRead(ownerA, "Customer", "Source", "op-a");

                using var scopeB = StateScope.Create(root, StateScopeId.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
                var rejected = new MutationRecoveryRegistry(path, scopeB.ForKb("kb-a", 1));
                Assert.False(rejected.IsHealthy);
                Assert.False(rejected.TryGet("kb-a", "Customer", "Source", out _));
                Assert.True(rejected.JournalError.Contains("another operational state scope", StringComparison.Ordinal));
            }
            finally { try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void RecoveryReadBackDoesNotLeakAcrossGeneration()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-scoped-recovery-generation-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var scope = StateScope.Create(root);
                var first = new MutationRecoveryRegistry(scope, "kb-a", 1);
                first.RequireRead("kb-a", "Customer", "Source", "op-a");
                var second = new MutationRecoveryRegistry(scope, "kb-a", 2);
                Assert.False(second.TryGet("kb-a", "Customer", "Source", out _));
                Assert.False(second.ConfirmRead("kb-a", "Customer", "Source"));
            }
            finally { try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { } }
        }
    }
}
