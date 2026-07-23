using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// v2.6.6 FR#11 — pre-write snapshot store for KB object parts. Before any
    /// destructive WriteService persist, the prior on-disk content is captured to
    /// <c>&lt;snapshotRoot&gt;/&lt;guid&gt;-&lt;part&gt;-&lt;utc-iso&gt;.bak</c>
    /// (gzip when payload &gt; 32KB → <c>.bak.gz</c>). Last
    /// <see cref="MaxSnapshotsPerKey"/> snapshots per (guid,part) are retained;
    /// older entries are garbage-collected on each save.
    ///
    /// Pure helper (no GeneXus SDK dependencies) so it can be unit-tested without
    /// a live KB. The orchestrator wiring lives in <c>WriteService.WriteObject</c>
    /// and <c>HistoryService.RestoreSnapshot</c>.
    /// </summary>
    public static class EditSnapshotStore
    {
        public const int GzipThresholdBytes = 32 * 1024;
        public const int MaxSnapshotsPerKey = 20;

        /// <summary>
        /// Build the canonical snapshot directory under a KB path:
        /// <c>&lt;kbPath&gt;/.gx/snapshots/</c>.
        /// </summary>
        public static string ResolveRoot(string kbPath)
        {
            if (string.IsNullOrWhiteSpace(kbPath))
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
                return Path.Combine(local, "GenexusMCP", "edit-snapshots");
            }
            return Path.Combine(kbPath, ".gx", "snapshots");
        }

        /// <summary>
        /// Persist <paramref name="content"/> as a snapshot. Returns the absolute file
        /// path on success or <c>null</c> when the inputs are invalid. Errors are
        /// logged (no silent catch) and reported back as <c>null</c>.
        /// </summary>
        public static SnapshotInfo SaveSnapshot(string snapshotRoot, string objectGuid, string partName, string content)
        {
            if (string.IsNullOrWhiteSpace(snapshotRoot)) return null;
            if (string.IsNullOrWhiteSpace(objectGuid)) return null;
            if (content == null) content = string.Empty;

            try
            {
                string safeGuid = Sanitize(objectGuid);
                string safePart = Sanitize(string.IsNullOrWhiteSpace(partName) ? "Source" : partName);
                Directory.CreateDirectory(snapshotRoot);

                string timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                bool gzip = bytes.Length > GzipThresholdBytes;
                string ext = gzip ? ".bak.gz" : ".bak";
                string fileName = safeGuid + "-" + safePart + "-" + timestamp + ext;
                string path = Path.Combine(snapshotRoot, fileName);

                if (gzip)
                {
                    using (var fs = File.Create(path))
                    using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
                    {
                        gz.Write(bytes, 0, bytes.Length);
                    }
                }
                else
                {
                    File.WriteAllBytes(path, bytes);
                }

                PruneOldSnapshots(snapshotRoot, safeGuid, safePart);

                return new SnapshotInfo
                {
                    Path = path,
                    Timestamp = timestamp,
                    Guid = safeGuid,
                    Part = safePart,
                    Compressed = gzip,
                    Bytes = bytes.Length,
                    PriorContent = content
                };
            }
            catch (Exception ex)
            {
                Logger.Warn("[EditSnapshotStore] Save failed for " + objectGuid + "/" + partName + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// List snapshots for a (guid, part) pair, newest first. Returns absolute paths.
        /// </summary>
        public static List<string> List(string snapshotRoot, string objectGuid, string partName)
        {
            var hits = new List<string>();
            if (string.IsNullOrWhiteSpace(snapshotRoot) || !Directory.Exists(snapshotRoot)) return hits;
            string safeGuid = Sanitize(objectGuid ?? string.Empty);
            string safePart = Sanitize(string.IsNullOrWhiteSpace(partName) ? "Source" : partName);
            string prefix = safeGuid + "-" + safePart + "-";

            try
            {
                hits = Directory.EnumerateFiles(snapshotRoot)
                    .Where(p =>
                    {
                        string fn = Path.GetFileName(p);
                        return fn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            && (fn.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                                || fn.EndsWith(".bak.gz", StringComparison.OrdinalIgnoreCase));
                    })
                    .OrderByDescending(p => p, StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex)
            {
                Logger.Warn("[EditSnapshotStore] List failed for " + objectGuid + "/" + partName + ": " + ex.Message);
            }
            return hits;
        }

        /// <summary>
        /// A single edit-snapshot entry surfaced to the tool surface.
        /// </summary>
        public sealed class SnapshotEntry
        {
            public string Path;
            public string FileName;
            public string Part;
            public string Timestamp;
            public long Bytes;
        }

        /// <summary>
        /// issue #43 #3 — list ALL edit snapshots for an object guid across every part,
        /// newest first. <c>snapshots-list</c> used to look only at the pattern-snapshot
        /// store, so the pre-write <c>.bak</c> every WriteObject creates was invisible.
        /// The part token is parsed back out of the filename
        /// (<c>&lt;guid&gt;-&lt;part&gt;-&lt;timestamp&gt;.bak</c>).
        /// </summary>
        public static List<SnapshotEntry> ListForGuid(string snapshotRoot, string objectGuid)
        {
            var hits = new List<SnapshotEntry>();
            if (string.IsNullOrWhiteSpace(snapshotRoot) || !Directory.Exists(snapshotRoot)) return hits;
            string safeGuid = Sanitize(objectGuid ?? string.Empty);
            string prefix = safeGuid + "-";

            try
            {
                foreach (var p in Directory.EnumerateFiles(snapshotRoot))
                {
                    string fn = Path.GetFileName(p);
                    if (!fn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    bool gz = fn.EndsWith(".bak.gz", StringComparison.OrdinalIgnoreCase);
                    bool bak = fn.EndsWith(".bak", StringComparison.OrdinalIgnoreCase);
                    if (!gz && !bak) continue;

                    // Strip prefix + extension, then split the "<part>-<timestamp>" remainder
                    // on the LAST '-' (Sanitize turns '-' into '_', so neither part nor guid
                    // token contains a literal '-'; only the field separators do).
                    string stem = fn.Substring(prefix.Length);
                    string ext = gz ? ".bak.gz" : ".bak";
                    if (stem.Length >= ext.Length) stem = stem.Substring(0, stem.Length - ext.Length);
                    string part = null, ts = null;
                    int lastDash = stem.LastIndexOf('-');
                    if (lastDash > 0)
                    {
                        part = stem.Substring(0, lastDash);
                        ts = stem.Substring(lastDash + 1);
                    }

                    long size = 0;
                    try { size = new FileInfo(p).Length; } catch { }
                    hits.Add(new SnapshotEntry { Path = p, FileName = fn, Part = part, Timestamp = ts, Bytes = size });
                }
                // Sort by the parsed timestamp, newest first. Sorting by FileName would sort by
                // the part token first (it precedes the timestamp in the name), interleaving parts.
                hits = hits.OrderByDescending(h => h.Timestamp ?? string.Empty, StringComparer.Ordinal).ToList();
            }
            catch (Exception ex)
            {
                Logger.Warn("[EditSnapshotStore] ListForGuid failed for " + objectGuid + ": " + ex.Message);
            }
            return hits;
        }

        /// <summary>
        /// Read snapshot content from a path. Auto-detects gzip via <c>.gz</c> extension.
        /// Returns <c>null</c> on any I/O error (logged).
        /// </summary>
        public static string ReadSnapshot(string snapshotPath)
        {
            if (string.IsNullOrWhiteSpace(snapshotPath) || !File.Exists(snapshotPath)) return null;
            try
            {
                if (snapshotPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                {
                    using (var fs = File.OpenRead(snapshotPath))
                    using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                    using (var ms = new MemoryStream())
                    {
                        gz.CopyTo(ms);
                        return Encoding.UTF8.GetString(ms.ToArray());
                    }
                }
                return File.ReadAllText(snapshotPath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Logger.Warn("[EditSnapshotStore] Read failed for " + snapshotPath + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Resolve a snapshot by timestamp token or <c>"latest"</c>. Returns absolute
        /// path or <c>null</c> if not found.
        /// </summary>
        public static string ResolveByTimestamp(string snapshotRoot, string objectGuid, string partName, string timestampOrLatest)
        {
            var list = List(snapshotRoot, objectGuid, partName);
            if (list.Count == 0) return null;
            if (string.IsNullOrWhiteSpace(timestampOrLatest) ||
                string.Equals(timestampOrLatest, "latest", StringComparison.OrdinalIgnoreCase))
            {
                return list[0];
            }

            foreach (var path in list)
            {
                string fn = Path.GetFileName(path);
                if (fn.IndexOf(timestampOrLatest, StringComparison.OrdinalIgnoreCase) >= 0)
                    return path;
            }
            return null;
        }

        private static void PruneOldSnapshots(string snapshotRoot, string safeGuid, string safePart)
        {
            try
            {
                string prefix = safeGuid + "-" + safePart + "-";
                var files = Directory.EnumerateFiles(snapshotRoot)
                    .Where(p =>
                    {
                        string fn = Path.GetFileName(p);
                        return fn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            && (fn.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                                || fn.EndsWith(".bak.gz", StringComparison.OrdinalIgnoreCase));
                    })
                    .OrderByDescending(p => p, StringComparer.Ordinal)
                    .ToList();
                for (int i = MaxSnapshotsPerKey; i < files.Count; i++)
                {
                    try { File.Delete(files[i]); }
                    catch (Exception ex)
                    {
                        Logger.Debug("[EditSnapshotStore] Prune skipped " + files[i] + ": " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("[EditSnapshotStore] Prune failed: " + ex.Message);
            }
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (c == '-' || c == ' ') { sb.Append('_'); continue; }
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }
            return sb.ToString();
        }

        public sealed class SnapshotInfo
        {
            public string Path;
            public string Timestamp;
            public string Guid;
            public string Part;
            public bool Compressed;
            public int Bytes;
            // The pre-write content captured for this snapshot. Used by the writer to
            // detect a no-op (persisted == prior) without a second read. issue #31.2.
            public string PriorContent;
        }
    }
}
