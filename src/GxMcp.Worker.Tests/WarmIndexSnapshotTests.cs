using System;
using System.Collections.Generic;
using System.Text;
using GxMcp.Worker.Services;
using GxMcp.Worker.Models;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Item 51 (mcp-improvements-2026-05-22, Tier-S, EXPERIMENTAL) — metadata-
    // validation tests for warm reload. The IWarmSnapshotStore seam lets us
    // exercise Save/TryLoad without touching real disk; the DLL-hash gate is
    // verified by stubbing the current SHA at the same value (happy path) and
    // a different value (fallback).
    public class WarmIndexSnapshotTests
    {
        private sealed class InMemoryStore : IWarmSnapshotStore
        {
            public readonly Dictionary<string, (WarmIndexSnapshotMetadata m, byte[] p)> Items
                = new Dictionary<string, (WarmIndexSnapshotMetadata, byte[])>();
            public bool ThrowOnSave = false;

            public void Save(string path, WarmIndexSnapshotMetadata metadata, byte[] payload)
            {
                if (ThrowOnSave) throw new System.IO.IOException("disk-full");
                Items[path] = (metadata, payload);
            }

            public bool TryLoad(string path, out WarmIndexSnapshotMetadata metadata, out byte[] payload)
            {
                if (Items.TryGetValue(path, out var pair))
                {
                    metadata = pair.m;
                    payload = pair.p;
                    return true;
                }
                metadata = null;
                payload = null;
                return false;
            }
        }

        // Force ComputeWorkerDllSha256 to a controllable value by pointing it at
        // a file we just wrote in TempPath. This is the cheapest reliable way to
        // simulate "DLL changed" without rewriting the production helper.
        private static string WriteFakeDll(string contents)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "warmsnap-fake-" + System.Guid.NewGuid().ToString("N") + ".dll");
            System.IO.File.WriteAllText(path, contents);
            return path;
        }

        [Fact]
        public void Save_then_TryLoad_roundtrips_with_matching_dll_hash()
        {
            var store = new InMemoryStore();
            WarmIndexSnapshot.SetStoreForTests(store);
            try
            {
                string dll = WriteFakeDll("dll-bytes-v1");
                string path = @"C:\fake-kb\.gx\index-snapshot.bin";
                byte[] payload = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");

                WarmIndexSnapshot.Save(path, payload, @"C:\fake-kb", objectCount: 42, workerDllPath: dll);

                var result = WarmIndexSnapshot.TryLoad(path, workerDllPath: dll);

                Assert.True(result.Loaded);
                Assert.False(result.Fallback);
                Assert.Null(result.FallbackReason);
                Assert.NotNull(result.Metadata);
                Assert.Equal(@"C:\fake-kb", result.Metadata.KbPath);
                Assert.Equal(42, result.Metadata.ObjectCount);
                Assert.Equal(payload, result.Payload);
            }
            finally
            {
                WarmIndexSnapshot.SetStoreForTests(null);
            }
        }

        [Fact]
        public void TryLoad_falls_back_when_worker_dll_hash_mismatches()
        {
            var store = new InMemoryStore();
            WarmIndexSnapshot.SetStoreForTests(store);
            try
            {
                string dllAtSave = WriteFakeDll("old-bytes");
                string dllAtLoad = WriteFakeDll("new-bytes-DIFFERENT");
                string path = @"C:\fake-kb\.gx\index-snapshot.bin";
                byte[] payload = Encoding.UTF8.GetBytes("{}");

                WarmIndexSnapshot.Save(path, payload, @"C:\fake-kb", objectCount: 0, workerDllPath: dllAtSave);

                var result = WarmIndexSnapshot.TryLoad(path, workerDllPath: dllAtLoad);

                Assert.False(result.Loaded);
                Assert.True(result.Fallback);
                Assert.Equal("worker-dll-hash-mismatch", result.FallbackReason);
                // Metadata is still surfaced so the agent can diagnose.
                Assert.NotNull(result.Metadata);
            }
            finally
            {
                WarmIndexSnapshot.SetStoreForTests(null);
            }
        }

        [Fact]
        public void TryLoad_falls_back_when_snapshot_missing()
        {
            var store = new InMemoryStore();
            WarmIndexSnapshot.SetStoreForTests(store);
            try
            {
                string dll = WriteFakeDll("any");
                var result = WarmIndexSnapshot.TryLoad(@"C:\no-such-path\.gx\index-snapshot.bin", workerDllPath: dll);

                Assert.False(result.Loaded);
                Assert.True(result.Fallback);
                Assert.Equal("snapshot-missing-or-unreadable", result.FallbackReason);
                Assert.Null(result.Payload);
            }
            finally
            {
                WarmIndexSnapshot.SetStoreForTests(null);
            }
        }

        [Fact]
        public void DefaultPath_returns_kb_relative_snapshot_path()
        {
            string p = WarmIndexSnapshot.DefaultPath(@"C:\KBs\MyKb");
            Assert.NotNull(p);
            Assert.EndsWith(System.IO.Path.Combine(".gx", "index-snapshot.bin"), p);
            Assert.StartsWith(@"C:\KBs\MyKb", p);
        }

        [Fact]
        public void DiskWarmSnapshotStore_SaveRoundTripsThroughTemporaryDirectory()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gx_warm_atomic_" + System.Guid.NewGuid().ToString("N"));
            string path = System.IO.Path.Combine(dir, "index-snapshot.bin");
            try
            {
                var store = new DiskWarmSnapshotStore();
                var metadata = new WarmIndexSnapshotMetadata { WorkerDllSha256 = "hash", KbPath = dir, SchemaVersion = 1 };
                store.Save(path, metadata, Encoding.UTF8.GetBytes("first"));
                store.Save(path, metadata, Encoding.UTF8.GetBytes("second"));

                WarmIndexSnapshotMetadata loadedMetadata;
                byte[] loadedPayload;
                Assert.True(store.TryLoad(path, out loadedMetadata, out loadedPayload));
                Assert.Equal("second", Encoding.UTF8.GetString(loadedPayload));
                Assert.Empty(System.IO.Directory.GetFiles(dir, "*.tmp*", System.IO.SearchOption.AllDirectories));
            }
            finally
            {
                try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void IndexCache_restores_valid_warm_snapshot_and_rebuilds_derived_indexes()
        {
            var store = new InMemoryStore();
            WarmIndexSnapshot.SetStoreForTests(store);
            try
            {
                string kbPath = @"C:\KBs\WarmRestore";
                string snapshotPath = WarmIndexSnapshot.DefaultPath(kbPath);
                var source = new SearchIndex
                {
                    LastUpdated = System.DateTime.UtcNow
                };
                source.Objects["Procedure:WarmProc"] = new SearchIndex.IndexEntry
                {
                    Name = "WarmProc",
                    Type = "Procedure",
                    FullSource = "call MissingProc()"
                };
                byte[] payload = Encoding.UTF8.GetBytes(source.ToJson());
                WarmIndexSnapshot.Save(
                    snapshotPath,
                    payload,
                    kbPath,
                    objectCount: 1,
                    schemaVersion: IndexCacheService.CurrentSchemaVersion,
                    highWaterMarkUtc: System.DateTime.UtcNow.ToString("o"));

                var cache = new IndexCacheService();
                var result = cache.TryRestoreWarmSnapshot(kbPath);

                Assert.True(result["loaded"]?.ToObject<bool>());
                Assert.False(result["fallback"]?.ToObject<bool>());
                Assert.Equal(1, result["objectCount"]?.ToObject<int>());
                var restored = cache.TryGetLoadedIndex();
                Assert.NotNull(restored);
                Assert.True(restored.Objects.ContainsKey("Procedure:WarmProc"));
                Assert.NotNull(restored.ChildrenByParent);
                Assert.NotNull(restored.SourceTokenIndex);
                Assert.True(restored.SourceTokenIndex.ContainsKey("missingproc"));
            }
            finally
            {
                WarmIndexSnapshot.SetStoreForTests(null);
            }
        }

        [Fact]
        public void IndexCache_rejects_warm_snapshot_with_wrong_schema()
        {
            var store = new InMemoryStore();
            WarmIndexSnapshot.SetStoreForTests(store);
            try
            {
                string kbPath = @"C:\KBs\WarmSchemaMismatch";
                string path = WarmIndexSnapshot.DefaultPath(kbPath);
                WarmIndexSnapshot.Save(
                    path,
                    Encoding.UTF8.GetBytes("{}"),
                    kbPath,
                    objectCount: 0,
                    schemaVersion: IndexCacheService.CurrentSchemaVersion + 1);

                var result = new IndexCacheService().TryRestoreWarmSnapshot(kbPath);

                Assert.False(result["loaded"]?.ToObject<bool>() ?? false);
                Assert.True(result["fallback"]?.ToObject<bool>());
                Assert.Equal("schema-mismatch", result["fallbackReason"]?.ToString());
            }
            finally
            {
                WarmIndexSnapshot.SetStoreForTests(null);
            }
        }

        [Fact]
        public void InternSharedStrings_CollapsesDuplicateEdgeAndScalarInstances()
        {
            // Concat defeats literal interning: distinct instances, equal content.
            string typeA = string.Concat("Trans", "action");
            string typeB = string.Concat("Trans", "action");
            string edgeA = string.Concat("Cust", "omer");
            string edgeB = string.Concat("Cust", "omer");
            Assert.False(ReferenceEquals(typeA, typeB));
            Assert.False(ReferenceEquals(edgeA, edgeB));

            var index = new SearchIndex();
            index.Objects["Transaction:A"] = new SearchIndex.IndexEntry { Name = "A", Type = typeA, Calls = new List<string> { edgeA } };
            index.Objects["Transaction:B"] = new SearchIndex.IndexEntry { Name = "B", Type = typeB, Calls = new List<string> { edgeB } };

            SearchIndex.InternSharedStrings(index);

            var a = index.Objects["Transaction:A"];
            var b = index.Objects["Transaction:B"];
            Assert.True(ReferenceEquals(a.Type, b.Type));
            Assert.True(ReferenceEquals(a.Calls[0], b.Calls[0]));
            // Unique-per-object values are untouched.
            Assert.Equal("A", a.Name);
            // Serialized form is unchanged (value semantics preserved).
            Assert.Equal("Transaction", a.Type);
            Assert.Equal("Customer", a.Calls[0]);
        }

        [Fact]
        public void AddCallCow_InternsEdgeValues()
        {
            var entry = new SearchIndex.IndexEntry();
            string first = string.Concat("Pro", "c1");
            string second = string.Concat("Pro", "c1");
            Assert.True(IndexCacheService.AddCallCow(entry, first));
            Assert.False(IndexCacheService.AddCallCow(entry, second)); // duplicate by value
            Assert.True(ReferenceEquals(string.Intern(second), entry.Calls[0]));
        }

        private static string WriteOrphanMeta(string cacheDir, string hash, string kbPath, bool validJson = true)
        {
            string metaPath = System.IO.Path.Combine(cacheDir, "index_" + hash + ".meta.json");
            System.IO.File.WriteAllText(metaPath, validJson
                ? "{\"KbPath\":" + Newtonsoft.Json.JsonConvert.SerializeObject(kbPath) + "}"
                : "{not-json");
            return metaPath;
        }

        private static void WriteOrphanFamily(string cacheDir, string hash)
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(cacheDir, "index_" + hash + ".json"), "{}");
            System.IO.File.WriteAllText(System.IO.Path.Combine(cacheDir, "index_" + hash + ".json.gz"), "gz");
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(cacheDir, "index_" + hash + ".json_shards"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(cacheDir, "index_" + hash + ".json_slots"));
        }

        [Fact]
        public void SweepOrphanSnapshots_RemovesOnlyDeadKbFamilies()
        {
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gxmcp_sweep_" + Guid.NewGuid().ToString("N"));
            string liveKb = System.IO.Path.Combine(tmp, "LiveKb");
            System.IO.Directory.CreateDirectory(liveKb);
            try
            {
                string deadHash = "AAAAAAAAAAAAAAAA";
                string liveHash = "BBBBBBBBBBBBBBBB";
                string corruptHash = "CCCCCCCCCCCCCCCC";
                string keepHash = "DDDDDDDDDDDDDDDD";
                string deadKb = System.IO.Path.Combine(tmp, "MissingKb");
                WriteOrphanMeta(tmp, deadHash, deadKb);
                WriteOrphanFamily(tmp, deadHash);
                // Live KB + corrupt meta + keepHash guard: all stay.
                WriteOrphanMeta(tmp, liveHash, liveKb);
                WriteOrphanFamily(tmp, liveHash);
                WriteOrphanMeta(tmp, corruptHash, null, validJson: false);
                WriteOrphanFamily(tmp, corruptHash);
                WriteOrphanMeta(tmp, keepHash, deadKb);
                WriteOrphanFamily(tmp, keepHash);

                int removed = IndexCacheService.SweepOrphanSnapshots(tmp, keepHash);

                Assert.Equal(1, removed);
                Assert.False(System.IO.File.Exists(System.IO.Path.Combine(tmp, "index_" + deadHash + ".meta.json")));
                Assert.False(System.IO.Directory.Exists(System.IO.Path.Combine(tmp, "index_" + deadHash + ".json_shards")));
                Assert.True(System.IO.File.Exists(System.IO.Path.Combine(tmp, "index_" + liveHash + ".meta.json")));
                Assert.True(System.IO.File.Exists(System.IO.Path.Combine(tmp, "index_" + corruptHash + ".meta.json")));
                Assert.True(System.IO.File.Exists(System.IO.Path.Combine(tmp, "index_" + keepHash + ".meta.json")));
            }
            finally
            {
                try { System.IO.Directory.Delete(tmp, true); } catch { }
            }
        }

        [Fact]
        public void SweepOrphanSnapshots_DisabledByEnv()
        {
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gxmcp_sweep_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(tmp);
            try
            {
                WriteOrphanMeta(tmp, "AAAAAAAAAAAAAAAA", System.IO.Path.Combine(tmp, "Missing"));
                Environment.SetEnvironmentVariable("GXMCP_SNAPSHOT_SWEEP", "0");
                Assert.Equal(0, IndexCacheService.SweepOrphanSnapshots(tmp, null));
                Assert.True(System.IO.File.Exists(System.IO.Path.Combine(tmp, "index_AAAAAAAAAAAAAAAA.meta.json")));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_SNAPSHOT_SWEEP", null);
                try { System.IO.Directory.Delete(tmp, true); } catch { }
            }
        }

        [Fact]
        public void ComputeKbHash_IsStableAcrossPathSpellings()
        {
            string a = IndexCacheService.ComputeKbHash(@"C:\KBs\KBTeste");
            string b = IndexCacheService.ComputeKbHash(@"C:\KBs\KBTeste\");
            string c = IndexCacheService.ComputeKbHash(@"c:\kbs\kbteste");
            Assert.Equal(16, a.Length);
            Assert.Equal(a, b);
            Assert.Equal(a, c);
        }
    }
}
