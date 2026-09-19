using System;
using Xunit;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Tests
{
    public sealed class OperationalIsolationTests
    {
        [Fact]
        public void WorkerKbBindingRejectsEnvironmentRebind()
        {
            var binding = new WorkerKbBinding(@"C:\kb\one");
            Assert.Throws<InvalidOperationException>(() => binding.ValidateEnvironment(@"C:\kb\two"));
            Assert.Throws<InvalidOperationException>(() => binding.RejectRebind(@"C:\kb\two"));
            binding.ValidateEnvironment(@"C:\kb\one\");
            binding.RejectRebind(@"C:\kb\one");
        }

        [Fact]
        public void ScopedSnapshotRootsDifferByGenerationAndKb()
        {
            string a = EditSnapshotStore.ResolveRoot("scope", "kb-a", 1, @"C:\state");
            string b = EditSnapshotStore.ResolveRoot("scope", "kb-a", 2, @"C:\state");
            string c = EditSnapshotStore.ResolveRoot("scope", "kb-b", 1, @"C:\state");
            Assert.NotEqual(a, b);
            Assert.NotEqual(a, c);
            Assert.Contains("snapshots", a);
            Assert.Contains("g1", a);
        }
    }
}
