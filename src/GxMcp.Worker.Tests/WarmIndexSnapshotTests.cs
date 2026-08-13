using System.Collections.Generic;
using System.Text;
using GxMcp.Worker.Services;
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
    }
}
