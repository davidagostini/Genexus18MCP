using System;
using System.Linq;
using GxMcp.Worker.Services;
using GxMcp.Worker.Models;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Fase 2 — the Guid→storage-key map (SearchIndex.GuidToKey) underpins rename collapse
    // and Guid-based deletion. A rename keeps the Guid stable but changes the Type:Name key,
    // so the index must be able to find an object by Guid regardless of its current name.
    public class GuidKeyMapTests
    {
        private static string UniqueKbPath() =>
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gxmcp-guidtest-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void ReplaceAll_builds_GuidToKey_for_every_entry()
        {
            var cache = new IndexCacheService();
            cache.Initialize(UniqueKbPath());
            try
            {
                var g1 = Guid.NewGuid().ToString();
                var g2 = Guid.NewGuid().ToString();
                cache.ReplaceAll(new[]
                {
                    new SearchIndex.IndexEntry { Name = "Proc1", Type = "Procedure", Guid = g1 },
                    new SearchIndex.IndexEntry { Name = "Trn1",  Type = "Transaction", Guid = g2 }
                });

                var idx = cache.GetIndex();
                Assert.NotNull(idx.GuidToKey);
                Assert.Equal("Procedure:Proc1", idx.GuidToKey[g1]);
                Assert.Equal("Transaction:Trn1", idx.GuidToKey[g2]);
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void RemoveEntryByGuid_drops_object_and_mapping()
        {
            var cache = new IndexCacheService();
            cache.Initialize(UniqueKbPath());
            try
            {
                var g1 = Guid.NewGuid().ToString();
                cache.ReplaceAll(new[]
                {
                    new SearchIndex.IndexEntry { Name = "Gone", Type = "Procedure", Guid = g1 },
                    new SearchIndex.IndexEntry { Name = "Stay", Type = "Procedure", Guid = Guid.NewGuid().ToString() }
                });

                cache.RemoveEntryByGuid(g1);

                var idx = cache.GetIndex();
                Assert.False(idx.Objects.ContainsKey("Procedure:Gone"));
                Assert.True(idx.Objects.ContainsKey("Procedure:Stay"));
                Assert.False(idx.GuidToKey.ContainsKey(g1));
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void RemoveEntryByGuid_is_noop_for_unknown_guid()
        {
            var cache = new IndexCacheService();
            cache.Initialize(UniqueKbPath());
            try
            {
                cache.ReplaceAll(new[]
                {
                    new SearchIndex.IndexEntry { Name = "Stay", Type = "Procedure", Guid = Guid.NewGuid().ToString() }
                });

                cache.RemoveEntryByGuid(Guid.NewGuid().ToString()); // unknown — must not throw or remove

                Assert.True(cache.GetIndex().Objects.ContainsKey("Procedure:Stay"));
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void RemoveEntryByGuid_does_not_remove_recreated_entry_at_same_storage_key()
        {
            var cache = new IndexCacheService();
            cache.Initialize(UniqueKbPath());
            try
            {
                var oldGuid = Guid.NewGuid().ToString();
                var newGuid = Guid.NewGuid().ToString();
                cache.ReplaceAll(new[]
                {
                    new SearchIndex.IndexEntry { Name = "Recreated", Type = "File", Guid = oldGuid }
                });

                var index = cache.GetIndex();
                const string key = "File:Recreated";
                index.Objects[key] = new SearchIndex.IndexEntry
                {
                    Name = "Recreated",
                    Type = "File",
                    Guid = newGuid,
                    StorageKey = key
                };
                index.GuidToKey[oldGuid] = key;
                index.GuidToKey[newGuid] = key;

                cache.RemoveEntryByGuid(oldGuid);

                Assert.True(index.Objects.ContainsKey(key));
                Assert.Equal(newGuid, index.Objects[key].Guid);
                Assert.False(index.GuidToKey.ContainsKey(oldGuid));
                Assert.Equal(key, index.GuidToKey[newGuid]);
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void Parent_index_replaces_entry_when_same_storage_key_gets_new_guid()
        {
            var cache = new IndexCacheService();
            cache.Initialize(UniqueKbPath());
            try
            {
                var oldGuid = Guid.NewGuid().ToString();
                var newGuid = Guid.NewGuid().ToString();
                cache.ReplaceAll(new[]
                {
                    new SearchIndex.IndexEntry
                    {
                        Name = "Recreated",
                        Type = "File",
                        Guid = oldGuid,
                        ParentPath = "Module"
                    }
                });

                var replacement = new SearchIndex.IndexEntry
                {
                    Name = "Recreated",
                    Type = "File",
                    Guid = newGuid,
                    ParentPath = "Module",
                    StorageKey = "File:Recreated"
                };
                var method = typeof(IndexCacheService).GetMethod(
                    "AddOrUpdateEntryInParentIndex",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                method.Invoke(cache, new object[] { cache.GetIndex(), replacement });

                var children = cache.GetIndex().ChildrenByParent["Module"];
                Assert.Single(children);
                Assert.Equal(newGuid, children[0].Guid);
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }
    }
}
