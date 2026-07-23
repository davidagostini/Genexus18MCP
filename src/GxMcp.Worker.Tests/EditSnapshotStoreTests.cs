using System;
using System.IO;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// v2.6.6 FR#11 — pre-write snapshot store. Pure helper tests; no SDK access.
    /// </summary>
    public class EditSnapshotStoreTests : IDisposable
    {
        private readonly string _root;

        public EditSnapshotStoreTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "GxMcpEditSnapshotTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        [Fact]
        public void ResolveRoot_WithKbPath_PutsUnderDotGxSnapshots()
        {
            string root = EditSnapshotStore.ResolveRoot(@"C:\kb\MyKb");
            Assert.Equal(Path.Combine(@"C:\kb\MyKb", ".gx", "snapshots"), root);
        }

        [Fact]
        public void ResolveRoot_NullKbPath_FallsBackToLocalAppData()
        {
            string root = EditSnapshotStore.ResolveRoot(null);
            Assert.False(string.IsNullOrWhiteSpace(root));
            Assert.Contains("edit-snapshots", root);
        }

        [Fact]
        public void SaveSnapshot_RoundTrip_SmallPayload_NotCompressed()
        {
            var info = EditSnapshotStore.SaveSnapshot(_root, "{abc-123}", "Source", "hello world");
            Assert.NotNull(info);
            Assert.False(info.Compressed);
            Assert.True(File.Exists(info.Path));
            Assert.EndsWith(".bak", info.Path);
            string read = EditSnapshotStore.ReadSnapshot(info.Path);
            Assert.Equal("hello world", read);
        }

        [Fact]
        public void SaveSnapshot_LargePayload_IsCompressed()
        {
            string big = new string('A', EditSnapshotStore.GzipThresholdBytes + 100);
            var info = EditSnapshotStore.SaveSnapshot(_root, "obj1", "Events", big);
            Assert.NotNull(info);
            Assert.True(info.Compressed);
            Assert.EndsWith(".bak.gz", info.Path);
            string read = EditSnapshotStore.ReadSnapshot(info.Path);
            Assert.Equal(big, read);
            // Compressed file must be smaller than raw payload.
            Assert.True(new FileInfo(info.Path).Length < big.Length);
        }

        [Fact]
        public void SaveSnapshot_KeepsLast20_PrunesOlder()
        {
            for (int i = 0; i < EditSnapshotStore.MaxSnapshotsPerKey + 5; i++)
            {
                var info = EditSnapshotStore.SaveSnapshot(_root, "obj42", "Source", "rev " + i);
                Assert.NotNull(info);
                // Ensure unique timestamps by sleeping enough for the ms-resolution stamp to tick.
                System.Threading.Thread.Sleep(2);
            }
            var list = EditSnapshotStore.List(_root, "obj42", "Source");
            Assert.True(list.Count <= EditSnapshotStore.MaxSnapshotsPerKey,
                $"Expected ≤{EditSnapshotStore.MaxSnapshotsPerKey} snapshots, got {list.Count}");
        }

        [Fact]
        public void List_FiltersByGuidAndPart()
        {
            EditSnapshotStore.SaveSnapshot(_root, "g1", "Source", "a");
            System.Threading.Thread.Sleep(2);
            EditSnapshotStore.SaveSnapshot(_root, "g1", "Events", "b");
            System.Threading.Thread.Sleep(2);
            EditSnapshotStore.SaveSnapshot(_root, "g2", "Source", "c");

            var src1 = EditSnapshotStore.List(_root, "g1", "Source");
            var evt1 = EditSnapshotStore.List(_root, "g1", "Events");
            var src2 = EditSnapshotStore.List(_root, "g2", "Source");

            Assert.Single(src1);
            Assert.Single(evt1);
            Assert.Single(src2);
        }

        [Fact]
        public void ResolveByTimestamp_Latest_ReturnsNewest()
        {
            var first = EditSnapshotStore.SaveSnapshot(_root, "gX", "Source", "old");
            System.Threading.Thread.Sleep(5);
            var second = EditSnapshotStore.SaveSnapshot(_root, "gX", "Source", "newer");
            string latest = EditSnapshotStore.ResolveByTimestamp(_root, "gX", "Source", "latest");
            Assert.Equal(second.Path, latest);
        }

        [Fact]
        public void ResolveByTimestamp_BadToken_ReturnsNull()
        {
            EditSnapshotStore.SaveSnapshot(_root, "gZ", "Source", "x");
            string hit = EditSnapshotStore.ResolveByTimestamp(_root, "gZ", "Source", "9999-not-a-real-stamp");
            Assert.Null(hit);
        }

        [Fact]
        public void ReadSnapshot_MissingFile_ReturnsNull()
        {
            string missing = Path.Combine(_root, "does-not-exist.bak");
            Assert.Null(EditSnapshotStore.ReadSnapshot(missing));
        }

        // issue #43 #3 — ListForGuid surfaces every part's snapshot for an object so
        // genexus_lifecycle snapshots-list can show the pre-write .bak the edit created.
        [Fact]
        public void ListForGuid_ReturnsAllPartsNewestFirst()
        {
            // Realistic guid with hyphens — Sanitize turns '-' into '_', so the field
            // separators are the only literal '-' the part parser can key on.
            const string guid = "f6ca1c0d-5ac1-4c2b-9e11-abc123456789";
            EditSnapshotStore.SaveSnapshot(_root, guid, "Source", "src rev 1");
            System.Threading.Thread.Sleep(2);
            EditSnapshotStore.SaveSnapshot(_root, guid, "Source", "src rev 2");
            System.Threading.Thread.Sleep(2);
            EditSnapshotStore.SaveSnapshot(_root, guid, "Events", "evt rev 1");
            // A different object's snapshot must not leak in.
            EditSnapshotStore.SaveSnapshot(_root, "99999999-0000-0000-0000-000000000000", "Source", "other");

            var entries = EditSnapshotStore.ListForGuid(_root, guid);

            Assert.Equal(3, entries.Count);
            // newest first (Events was saved last).
            Assert.Equal("Events", entries[0].Part);
            Assert.Contains(entries, e => e.Part == "Source");
            Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Timestamp)));
            Assert.All(entries, e => Assert.True(e.Bytes > 0));
        }

        [Fact]
        public void ListForGuid_EmptyRoot_ReturnsEmpty()
        {
            var entries = EditSnapshotStore.ListForGuid(_root, "no-such-guid");
            Assert.Empty(entries);
        }
    }
}
