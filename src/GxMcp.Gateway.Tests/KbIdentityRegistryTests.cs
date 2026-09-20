using System;
using System.IO;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public sealed class KbIdentityRegistryTests
    {
        [Fact]
        public void SamePhysicalPathKeepsOpaqueIdAcrossRegistryInstances()
        {
            string root = CreateTempDirectory();
            string kb = Path.Combine(root, "kb");
            Directory.CreateDirectory(kb);
            try
            {
                var first = new KbIdentityRegistry(root).GetOrCreate("Sales", kb);
                var second = new KbIdentityRegistry(root).GetOrCreate("sales", kb + Path.DirectorySeparatorChar);

                Assert.Equal(first.KbId, second.KbId);
                Assert.NotEqual(kb, first.KbId);
                Assert.Equal(1, second.ContextGeneration);
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void AliasBoundToAnotherPathReturnsDeterministicConflict()
        {
            string root = CreateTempDirectory();
            string kbA = Path.Combine(root, "a");
            string kbB = Path.Combine(root, "b");
            Directory.CreateDirectory(kbA);
            Directory.CreateDirectory(kbB);
            try
            {
                _ = new KbIdentityRegistry(root).GetOrCreate("sales", kbA);
                var error = Assert.Throws<KbIdentityConflictException>(() =>
                    new KbIdentityRegistry(root).GetOrCreate("SALES", kbB));

                Assert.Equal("KB_ALIAS_CONFLICT", error.Code);
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void DifferentAliasesForSamePathReuseOneIdentity()
        {
            string root = CreateTempDirectory();
            string kb = Path.Combine(root, "shared");
            Directory.CreateDirectory(kb);
            try
            {
                var first = new KbIdentityRegistry(root).GetOrCreate("one", kb);
                var second = new KbIdentityRegistry(root).GetOrCreate("two", kb);

                Assert.Equal(first.KbId, second.KbId);
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void CorruptedRegistryFailsClosedWithoutCreatingReplacementIdentity()
        {
            string root = CreateTempDirectory();
            string kb = Path.Combine(root, "shared");
            Directory.CreateDirectory(kb);
            File.WriteAllText(Path.Combine(root, "kb-identities.json"), "{not-json}");
            try
            {
                var error = Assert.Throws<KbIdentityConflictException>(() =>
                    new KbIdentityRegistry(root).GetOrCreate("shared", kb));

                Assert.Equal("KB_IDENTITY_REGISTRY_UNAVAILABLE", error.Code);
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void ExplicitRebindOfRemovedFolderPreservesIdAndAdvancesGeneration()
        {
            string root = CreateTempDirectory();
            string oldKb = Path.Combine(root, "old");
            string newKb = Path.Combine(root, "new");
            Directory.CreateDirectory(oldKb);
            Directory.CreateDirectory(newKb);
            try
            {
                var first = new KbIdentityRegistry(root).GetOrCreate("sales", oldKb);
                Directory.Delete(oldKb);

                var rebound = new KbIdentityRegistry(root).Rebind("SALES", newKb);

                Assert.Equal(first.KbId, rebound.KbId);
                Assert.Equal(2, rebound.ContextGeneration);
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void ReplacementWithoutExplicitRebindIsRejectedEvenWhenOldFolderWasRemoved()
        {
            string root = CreateTempDirectory();
            string oldKb = Path.Combine(root, "old");
            string newKb = Path.Combine(root, "new");
            Directory.CreateDirectory(oldKb);
            Directory.CreateDirectory(newKb);
            try
            {
                _ = new KbIdentityRegistry(root).GetOrCreate("sales", oldKb);
                Directory.Delete(oldKb);

                var error = Assert.Throws<KbIdentityConflictException>(() =>
                    new KbIdentityRegistry(root).GetOrCreate("sales", newKb));

                Assert.Equal("KB_ALIAS_CONFLICT", error.Code);
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void ExplicitRebindToSamePhysicalPathOnlyRevalidatesWithoutAdvancingGeneration()
        {
            string root = CreateTempDirectory();
            string kb = Path.Combine(root, "kb");
            Directory.CreateDirectory(kb);
            try
            {
                var first = new KbIdentityRegistry(root).GetOrCreate("sales", kb);
                var rebound = new KbIdentityRegistry(root).Rebind("SALES", kb + Path.DirectorySeparatorChar);

                Assert.Equal(first.KbId, rebound.KbId);
                Assert.Equal(1, rebound.ContextGeneration);
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void ExplicitRebindToDifferentPhysicalIdentityCreatesNewId()
        {
            string root = CreateTempDirectory();
            string firstKb = Path.Combine(root, "first");
            string secondKb = Path.Combine(root, "second");
            Directory.CreateDirectory(firstKb);
            Directory.CreateDirectory(secondKb);
            try
            {
                var first = new KbIdentityRegistry(root).GetOrCreate("sales", firstKb);

                var rebound = new KbIdentityRegistry(root).Rebind("sales", secondKb);

                Assert.NotEqual(first.KbId, rebound.KbId);
                Assert.Equal(1, rebound.ContextGeneration);
                Assert.Equal(rebound.KbId, new KbIdentityRegistry(root).GetOrCreate("sales", secondKb).KbId);
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void RebindConflictLeavesBothIdentitiesDeterministicallyUnchanged()
        {
            string root = CreateTempDirectory();
            string firstKb = Path.Combine(root, "first");
            string secondKb = Path.Combine(root, "second");
            Directory.CreateDirectory(firstKb);
            Directory.CreateDirectory(secondKb);
            try
            {
                var first = new KbIdentityRegistry(root).GetOrCreate("sales", firstKb);
                var second = new KbIdentityRegistry(root).GetOrCreate("marketing", secondKb);

                var error = Assert.Throws<KbIdentityConflictException>(() =>
                    new KbIdentityRegistry(root).Rebind("sales", secondKb));

                Assert.Equal("KB_ALIAS_CONFLICT", error.Code);
                Assert.Equal(first.KbId, new KbIdentityRegistry(root).GetOrCreate("sales", firstKb).KbId);
                Assert.Equal(second.KbId, new KbIdentityRegistry(root).GetOrCreate("marketing", secondKb).KbId);
            }
            finally { TryDelete(root); }
        }

        private static string CreateTempDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-identity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void TryDelete(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }
    }
}
