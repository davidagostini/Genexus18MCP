using System;
using System.IO;
using System.Collections.Generic;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SourceStoreDurabilityTests
    {
        [Fact]
        public void DurableRootSurvivesGatewayScopeAndSeparatesIdentityEvenWithOverride()
        {
            string root = Path.Combine(Path.GetTempPath(), "durable-source-test");
            string first = RuntimePaths.ResolveSourceStoreRoot(root, root, "kb-a", root, "1", "process-a");
            Assert.Equal(first, RuntimePaths.ResolveSourceStoreRoot(root, root, "kb-a", root + Path.DirectorySeparatorChar, "1", "process-b"));
            Assert.NotEqual(first, RuntimePaths.ResolveSourceStoreRoot(root, root, "kb-b", root, "1", "process-a"));
            Assert.NotEqual(first, RuntimePaths.ResolveSourceStoreRoot(root, root, "kb-a", root, "2", "process-a"));
            Assert.NotEqual(first, RuntimePaths.ResolveSourceStoreRoot(root, root, "kb-a", root + "-other", "1", "process-a"));
        }

        [Fact]
        public void ExclusiveStoreRejectsSecondOwnerAndResumesRecordsAndCursorAfterRestart()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-durable-" + Guid.NewGuid().ToString("N"));
            var entries = new[] {
                new SearchIndex.IndexEntry { Guid = Guid.NewGuid().ToString(), Name = "First", Type = "Procedure", LastUpdate = DateTime.UtcNow },
                new SearchIndex.IndexEntry { Guid = Guid.NewGuid().ToString(), Name = "Second", Type = "Procedure", LastUpdate = DateTime.UtcNow.AddMinutes(-1) }
            };
            try
            {
                using (var first = new SourceStoreService(root, true))
                {
                    Assert.True(first.IsAvailable);
                    var fill = new SourceStoreBackfillService(first);
                    Assert.True(fill.Start(() => entries, entry => new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "source", LastUpdate = entry.LastUpdate } }, schedule: false));
                    Assert.True(fill.RunOneSliceForTest(1, 1000));
                    Assert.Equal(1, fill.GetState().CursorIndex);
                    using (var second = new SourceStoreService(root, true))
                    {
                        Assert.False(second.IsAvailable);
                        Assert.False(second.Put(entries[1].Guid, "Source", "overwrite", null, null));
                        var competingFill = new SourceStoreBackfillService(second);
                        Assert.False(competingFill.Start(() => entries, e => null, schedule: false));
                        Assert.Equal("storeOwnedOrUnavailable", competingFill.GetState().NotStartedReason);
                    }
                }
                using (var restarted = new SourceStoreService(root, true))
                {
                    Assert.True(restarted.IsAvailable);
                    Assert.True(restarted.TryGet(entries[0].Guid, "Source", out string source));
                    Assert.Equal("source", source);
                    var fill = new SourceStoreBackfillService(restarted);
                    Assert.Equal(1, fill.GetState().CursorIndex);
                    var read = new List<string>();
                    Assert.True(fill.Start(() => entries, entry => { read.Add(entry.Guid); return new[] { new SourceStoreBackfillPart { PartName = "Source", Source = "second", LastUpdate = entry.LastUpdate } }; }, schedule: false));
                    Assert.True(fill.RunOneSliceForTest(10, 1000));
                    Assert.Equal(new[] { entries[1].Guid }, read);
                    Assert.Equal(2, fill.GetState().ProcessedObjects);
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData("current", true)]
        [InlineData("stale", false)]
        [InlineData("refreshing", false)]
        public void OnlyCurrentCatalogConfirmsAbsence(string freshness, bool expected)
        {
            var entry = new SearchIndex.IndexEntry { Guid = Guid.NewGuid().ToString(), Name = "Gone", Type = "Procedure" };
            var index = new SearchIndex();
            Assert.Equal(expected, ObjectService.IsConfirmedAbsentFromCurrentIndex(entry, index, freshness));
            index.Objects["Procedure:Renamed"] = new SearchIndex.IndexEntry { Guid = entry.Guid, Name = "Renamed", Type = "Procedure" };
            Assert.False(ObjectService.IsConfirmedAbsentFromCurrentIndex(entry, index, freshness));
        }

        [Fact]
        public void ConfirmedDeletionRemovesStoredPartsAndAdvancesWithoutSdkRead()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-deleted-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var store = new SourceStoreService(root, true))
                {
                    var entry = new SearchIndex.IndexEntry { Guid = Guid.NewGuid().ToString(), Name = "Gone", Type = "Procedure", LastUpdate = DateTime.UtcNow };
                    Assert.True(store.Put(entry.Guid, "Source", "old", entry.LastUpdate, "v1"));
                    var fill = new SourceStoreBackfillService(store);
                    Assert.True(fill.Start(() => new[] { entry }, e => throw new InvalidOperationException("must not read deleted object"), schedule: false, confirmedAbsent: e => true));
                    Assert.True(fill.RunOneSliceForTest(5, 1000));
                    Assert.Equal("complete", fill.GetState().State);
                    Assert.Equal(1, fill.GetState().RemovedObjects);
                    Assert.Equal(1, fill.GetState().ProcessedObjects);
                    Assert.False(store.TryGet(entry.Guid, "Source", out _));
                    Assert.Equal(0, store.StoredRecordCount);
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Fact]
        public void UnconfirmedResolutionFailureStillBlocksCursor()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-unconfirmed-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var store = new SourceStoreService(root, true))
                {
                    var entry = new SearchIndex.IndexEntry { Guid = Guid.NewGuid().ToString(), Name = "MaybePresent", Type = "Procedure" };
                    var fill = new SourceStoreBackfillService(store);
                    Assert.True(fill.Start(() => new[] { entry }, e => null, schedule: false, confirmedAbsent: e => false));
                    Assert.False(fill.RunOneSliceForTest(5, 1000));
                    Assert.Equal(0, fill.GetState().CursorIndex);
                    Assert.Equal(0, fill.GetState().RemovedObjects);
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Fact]
        public void IndexReadyNotificationRunsAfterStateAndCatalogArePublished()
        {
            var cache = new IndexCacheService();
            int notifications = 0;
            cache.IndexReady += index => { Assert.Equal("Ready", cache.GetState().Status); Assert.Single(index.Objects); notifications++; };
            cache.LoadFromEntries(new[] { new SearchIndex.IndexEntry { Guid = Guid.NewGuid().ToString(), Name = "ReadOnly", Type = "Procedure" } });
            Assert.Equal(1, notifications);
        }

        [Fact]
        public void LateFlushAfterDisposeCannotOverwriteNextOwnersCatalog()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-late-flush-" + Guid.NewGuid().ToString("N"));
            try
            {
                var oldOwner = new SourceStoreService(root, true);
                Assert.True(oldOwner.Put("first", "Source", "old", null, "v1"));
                oldOwner.Dispose();
                using (var newOwner = new SourceStoreService(root, true))
                {
                    Assert.True(newOwner.Put("second", "Source", "new", null, "v2"));
                    newOwner.FlushCatalog();
                    string catalog = Path.Combine(root, "catalog.json.gz");
                    var before = File.ReadAllBytes(catalog);
                    // Simulate a failed old flush leaving its dirty flag set and a
                    // queued callback entering after ownership has been released.
                    typeof(SourceStoreService).GetField("_isCatalogDirty", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .SetValue(oldOwner, true);
                    oldOwner.FlushCatalog();
                    Assert.Equal(before, File.ReadAllBytes(catalog));
                    Assert.False(oldOwner.Put("third", "Source", "late", null, "v3"));
                    string checkpoint = Path.Combine(root, "backfill-state.json");
                    Assert.True(newOwner.WriteBackfillState("new-owner-checkpoint"));
                    Assert.False(oldOwner.WriteBackfillState("obsolete-owner-checkpoint"));
                    Assert.Equal("new-owner-checkpoint", File.ReadAllText(checkpoint));
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Fact]
        public void FailedCatalogFlushCannotPublishAnAdvancedDurableCursor()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-checkpoint-failure-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var store = new SourceStoreService(root, true))
                {
                    Assert.True(store.WriteBackfillState("original-checkpoint"));
                    string catalog = Path.Combine(root, "catalog.json.gz");
                    Directory.CreateDirectory(catalog);
                    Assert.True(store.Put("first", "Source", "record", null, "v1"));
                    Assert.False(store.WriteBackfillState("advanced-checkpoint"));
                    Assert.Equal("original-checkpoint", File.ReadAllText(Path.Combine(root, "backfill-state.json")));
                    Directory.Delete(catalog);
                    Assert.True(store.WriteBackfillState("advanced-checkpoint"));
                    Assert.True(File.Exists(catalog));
                    Assert.Equal("advanced-checkpoint", File.ReadAllText(Path.Combine(root, "backfill-state.json")));
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
