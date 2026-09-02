using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public interface IIndexStorageEngine
    {
        int ShardOf(string storageKey);
        bool Flush(SearchIndex index, ISet<int> dirtyShards, long generation);
        SearchIndex Load();
        void DeleteOnDiskSnapshot();
    }

    /// <summary>
    /// Deep storage engine for the SearchIndex.
    /// Encapsulates on-disk 16-shard partitioning, deterministic FNV-1a hashing,
    /// atomic GZip stream serialization, and dirty-generation flushing.
    /// </summary>
    public sealed class IndexStorageEngine : IIndexStorageEngine
    {
        public const int ShardCount = 16;
        private readonly string _storageDirectory;
        private readonly object _ioLock = new object();

        public IndexStorageEngine(string storageDirectory)
        {
            _storageDirectory = storageDirectory ?? Path.GetTempPath();
        }

        public int ShardOf(string storageKey)
        {
            if (string.IsNullOrEmpty(storageKey)) return 0;
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in storageKey)
                {
                    char upper = char.ToUpperInvariant(c);
                    hash = (hash ^ upper) * 16777619;
                }
                return (int)(hash % ShardCount);
            }
        }

        private string GetShardFilePath(int shardId)
        {
            return Path.Combine(_storageDirectory, $"shard_{shardId:D2}.json.gz");
        }

        public bool Flush(SearchIndex index, ISet<int> dirtyShards, long generation)
        {
            if (index == null) return false;
            lock (_ioLock)
            {
                try
                {
                    if (!Directory.Exists(_storageDirectory))
                    {
                        Directory.CreateDirectory(_storageDirectory);
                    }

                    // Group entries by shard
                    var shardBuckets = new Dictionary<int, List<SearchIndex.IndexEntry>>();
                    for (int i = 0; i < ShardCount; i++)
                    {
                        shardBuckets[i] = new List<SearchIndex.IndexEntry>();
                    }

                    foreach (var kvp in index.Objects)
                    {
                        if (kvp.Value == null) continue;
                        int shard = ShardOf(kvp.Key);
                        shardBuckets[shard].Add(kvp.Value);
                    }

                    // Flush dirty shards
                    foreach (int shardId in dirtyShards ?? Enumerable.Range(0, ShardCount))
                    {
                        if (shardId < 0 || shardId >= ShardCount) continue;
                        var entries = shardBuckets[shardId];
                        string filePath = GetShardFilePath(shardId);
                        string tempFile = filePath + $".tmp-{Guid.NewGuid():N}";

                        using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                        using (var gz = new GZipStream(fs, CompressionMode.Compress))
                        using (var sw = new StreamWriter(gz, Encoding.UTF8))
                        using (var jw = new JsonTextWriter(sw))
                        {
                            var serializer = new JsonSerializer();
                            serializer.Serialize(jw, entries);
                        }

                        if (File.Exists(filePath))
                        {
                            File.Delete(filePath);
                        }
                        File.Move(tempFile, filePath);
                    }

                    // Write manifest
                    string manifestPath = Path.Combine(_storageDirectory, "manifest.json");
                    File.WriteAllText(manifestPath, JsonConvert.SerializeObject(new
                    {
                        generation = generation,
                        flushedAt = DateTime.UtcNow,
                        objectCount = index.Objects.Count,
                        shardCount = ShardCount
                    }));

                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Error($"[INDEX-STORAGE] Flush failed: {ex.Message}");
                    return false;
                }
            }
        }

        public SearchIndex Load()
        {
            lock (_ioLock)
            {
                try
                {
                    if (!Directory.Exists(_storageDirectory)) return null;

                    var index = new SearchIndex();
                    bool loadedAny = false;

                    for (int i = 0; i < ShardCount; i++)
                    {
                        string filePath = GetShardFilePath(i);
                        if (!File.Exists(filePath)) continue;

                        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                        using (var sr = new StreamReader(gz, Encoding.UTF8))
                        using (var jr = new JsonTextReader(sr))
                        {
                            var serializer = new JsonSerializer();
                            var entries = serializer.Deserialize<List<SearchIndex.IndexEntry>>(jr);
                            if (entries != null)
                            {
                                foreach (var e in entries)
                                {
                                    if (e != null && !string.IsNullOrEmpty(e.Name) && !string.IsNullOrEmpty(e.Type))
                                    {
                                        string key = $"{e.Type}:{e.Name}";
                                        index.Objects[key] = e;
                                        loadedAny = true;
                                    }
                                }
                            }
                        }
                    }

                    return loadedAny ? index : null;
                }
                catch (Exception ex)
                {
                    Logger.Error($"[INDEX-STORAGE] Load failed: {ex.Message}");
                    return null;
                }
            }
        }

        public void DeleteOnDiskSnapshot()
        {
            lock (_ioLock)
            {
                try
                {
                    if (Directory.Exists(_storageDirectory))
                    {
                        Directory.Delete(_storageDirectory, true);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[INDEX-STORAGE] DeleteOnDiskSnapshot failed: {ex.Message}");
                }
            }
        }
    }
}
