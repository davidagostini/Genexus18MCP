using System;
using System.IO;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public sealed class OperationalStateIsolationTests
    {
        [Fact]
        public void SameOwnerKeyChangesWhenGenerationOrKbChanges()
        {
            using var scope = StateScope.Create(Path.Combine(Path.GetTempPath(), "gxmcp-op-" + Guid.NewGuid().ToString("N")));
            var a = scope.ForKb("kb-a", 1);
            var b = scope.ForKb("kb-a", 2);
            var c = scope.ForKb("kb-b", 1);
            Assert.NotEqual(a, b);
            Assert.NotEqual(a, c);
            Assert.NotEqual(a.SnapshotKey("object"), b.SnapshotKey("object"));
            Assert.NotEqual(a.JobKey("job"), c.JobKey("job"));
        }

        [Fact]
        public void ComponentPathsCarryOwnerIdentityAndDoNotUseWorkerPath()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-op-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var scope = StateScope.Create(root);
                string first = scope.RecoveryPath("kb-a", 7);
                string second = scope.RecoveryPath("kb-a", 8);
                Assert.NotEqual(first, second);
                Assert.Contains("g7", first);
                Assert.Contains(scope.Id.ToString(), first);
                Assert.DoesNotContain("worker", first, StringComparison.OrdinalIgnoreCase);
            }
            finally { try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void RecoveryFenceCannotBeConsumedByAnotherGeneration()
        {
            using var scope = StateScope.Create(Path.Combine(Path.GetTempPath(), "gxmcp-recovery-" + Guid.NewGuid().ToString("N")));
            var ownerA = scope.ForKb("kb-a", 1);
            var ownerB = scope.ForKb("kb-a", 2);
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead(ownerA, "Customer", "Source", "op-a");
            Assert.True(registry.TryGet(ownerA, "Customer", "Source", out _));
            Assert.False(registry.TryGet(ownerB, "Customer", "Source", out _));
            Assert.False(registry.ConfirmRead(ownerB, "Customer", "Source"));
            Assert.True(registry.ConfirmRead(ownerA, "Customer", "Source"));
        }
        [Fact]
        public void WorkerPathBindingRejectsRebindAndAcceptsEquivalentPath()
        {
            string path = Path.Combine(Path.GetTempPath(), "kb-owner");
            var binding = CrashLedger.IsKbContextMatch(path, path + Path.DirectorySeparatorChar);
            Assert.True(binding);
            Assert.False(CrashLedger.IsKbContextMatch(path, Path.Combine(Path.GetTempPath(), "other-kb")));
            string first = CrashLedger.ResolveScopedPath(StateScope.ProcessScopeId, "kb-a", 1);
            string second = CrashLedger.ResolveScopedPath(StateScope.ProcessScopeId, "kb-a", 2);
            Assert.NotEqual(first, second);
        }
    }
}
