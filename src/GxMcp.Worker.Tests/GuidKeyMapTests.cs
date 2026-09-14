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
        public void AddOrUpdateBatch_deleteAndRecreate_sameName_keeps_new_guid_everywhere()
        {
            var cache = new IndexCacheService();
            cache.Initialize(UniqueKbPath());
            try
            {
                const string parent = "Root Module/M";
                string oldGuid = Guid.NewGuid().ToString();
                string newGuid = Guid.NewGuid().ToString();

                cache.AddOrUpdateBatch(new[]
                {
                    new SearchIndex.IndexEntry
                    {
                        Name = "Config",
                        Type = "File",
                        Guid = oldGuid,
                        ParentPath = parent
                    }
                });
                cache.AddOrUpdateBatch(new[]
                {
                    new SearchIndex.IndexEntry
                    {
                        Name = "Config",
                        Type = "File",
                        Guid = newGuid,
                        ParentPath = parent
                    }
                });

                var idx = cache.GetIndex();
                Assert.Equal(newGuid, idx.Objects["File:Config"].Guid);
                Assert.False(idx.GuidToKey.ContainsKey(oldGuid));
                Assert.Equal("File:Config", idx.GuidToKey[newGuid]);
                Assert.Single(idx.ChildrenByParent[parent]);
                Assert.Equal(newGuid, idx.ChildrenByParent[parent][0].Guid);
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void RemoveEntryByGuid_stale_mapping_does_not_remove_recreated_object()
        {
            var cache = new IndexCacheService();
            cache.Initialize(UniqueKbPath());
            try
            {
                string oldGuid = Guid.NewGuid().ToString();
                string newGuid = Guid.NewGuid().ToString();
                cache.AddOrUpdateBatch(new[]
                {
                    new SearchIndex.IndexEntry { Name = "Config", Type = "File", Guid = newGuid }
                });

                // Reproduce the stale reverse-map state seen by the pre-fix delta sweep.
                var idx = cache.GetIndex();
                idx.GuidToKey[oldGuid] = "File:Config";

                cache.RemoveEntryByGuid(oldGuid);

                Assert.True(idx.Objects.ContainsKey("File:Config"));
                Assert.Equal(newGuid, idx.Objects["File:Config"].Guid);
                Assert.False(idx.GuidToKey.ContainsKey(oldGuid));
                Assert.Equal("File:Config", idx.GuidToKey[newGuid]);
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }
    }
}
