using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Decides where gxwire's index lives, and recognises a Knowledge Base that
    /// gxwire cannot read at all.
    ///
    /// Both exist because the published contract declares
    /// <c>genexus_wire</c> read-only - <c>readOnlyHint: true</c> and
    /// "Mutating actions: none" - while the tool did two things that contract
    /// does not admit (#499).
    /// </summary>
    internal static class GxwireCacheLocation
    {
        /// <summary>
        /// Resolves the index file for <paramref name="analysedDirectory"/>.
        ///
        /// Lives under <c>%LOCALAPPDATA%\GxMcp\Cache\gxwire</c>, the same cache
        /// root <c>IndexCacheService</c> already uses. The file name is a hash of
        /// the resolved directory so two Knowledge Bases never share an index,
        /// and so a directory whose display name contains characters a file name
        /// cannot carry still works.
        ///
        /// A hash rather than the directory name for a second reason: two
        /// directories can share a name, and an index is only valid for the
        /// directory it was built from.
        /// </summary>
        internal static string Resolve(string? analysedDirectory)
        {
            string dir = Path.GetFullPath(analysedDirectory ?? string.Empty).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GxMcp", "Cache", "gxwire");
            Directory.CreateDirectory(root);

            return Path.Combine(root, StableKey(dir) + ".bin");
        }

        /// <summary>
        /// A stable, filesystem-safe key for a directory path.
        ///
        /// SHA-256 over the lower-cased, separator-normalized path, truncated to 32
        /// hex characters: long enough that two Knowledge Bases colliding by
        /// accident is not a practical concern, short enough to stay readable in
        /// a directory listing while diagnosing a cache problem.
        /// </summary>
        internal static string StableKey(string fullPath)
        {
            string normalized = fullPath
                .Replace('\\', '/')
                .TrimEnd('/')
                .ToLowerInvariant();

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            return Convert.ToHexString(hash)[..32].ToLowerInvariant();
        }
    }

    /// <summary>
    /// Recognises a native GeneXus Knowledge Base, where object sources live in
    /// the KB database instead of files.
    ///
    /// gxwire walks a directory and indexes what it finds. Against a native KB
    /// that means indexing generated output - the folder holds the KB database,
    /// environments, and everything the IDE has ever produced - and reporting the
    /// result as a graph. It is not a slow graph, it is an inferred one built from
    /// text that was never an object source, which is why it reads as plausible
    /// and is wrong (#499).
    /// </summary>
    internal static class NativeKnowledgeBaseEvidence
    {
        /// <summary>
        /// Files only a native Knowledge Base has: its database. An XML/text
        /// export is a directory of object text and never carries the KB's own
        /// store, so this marker is decisive on its own.
        ///
        /// This tier exists because the obvious rule does not work. A native KB
        /// root carries GeneXus metadata of its own - `CategoryViewConfig.xml`,
        /// `iar_objs.xml`, `nav_objs.xml` all match the extensions gxwire
        /// indexes - so "a KB marker plus no source files" never fires against a
        /// real one, and a guard built on it would let the original bug through.
        /// </summary>
        private static readonly (string Pattern, string Label)[] HardMarkers =
        {
            ("*.mdf", "Knowledge Base database"),
            ("*.ldf", "Knowledge Base database log"),
        };

        /// <summary>
        /// Files a native KB has and an export may also carry, so they only count
        /// when there is no source text to read.
        /// </summary>
        private static readonly (string Pattern, string Label)[] SoftMarkers =
        {
            ("GXLOCK", "GeneXus KB lock"),
            ("*.gxw", "Knowledge Base workspace"),
        };

        /// <summary>
        /// Returns the markers found, or <c>null</c> when this is not a native
        /// Knowledge Base.
        ///
        /// A soft marker alone is not enough to refuse: a GeneXus XML/text export
        /// is a supported input for gxwire and can carry the KB's own files. So a
        /// soft marker is honoured only when the directory also has no source
        /// text, while a hard marker is honoured regardless.
        /// </summary>
        internal static JArray? Describe(string? directory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;

                var hard = FindMarkers(directory, HardMarkers);
                var soft = FindMarkers(directory, SoftMarkers);
                if (hard.Count == 0 && soft.Count == 0) return null;

                if (hard.Count == 0 && HasSourceText(directory)) return null;

                var evidence = new JArray();
                foreach (var marker in hard.Concat(soft)) evidence.Add(marker);
                if (hard.Count == 0)
                {
                    evidence.Add("no GeneXus source files (txt/gxd/xml/gx/prc/trn/wp/sdp) at the directory root");
                }
                return evidence;
            }
            catch
            {
                // Detection is a safety net, not the contract. If the directory
                // cannot be inspected, let gxwire decide for itself.
                return null;
            }
        }

        private static List<string> FindMarkers(string directory, (string Pattern, string Label)[] markers)
        {
            var found = new List<string>();

            foreach (var (pattern, label) in markers)
            {
                if (pattern == "GXLOCK")
                {
                    if (File.Exists(Path.Combine(directory, "GXLOCK"))) found.Add("GXLOCK (" + label + ")");
                    continue;
                }

                foreach (var path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
                {
                    found.Add(Path.GetFileName(path) + " (" + label + ")");
                }
            }

            return found;
        }

        /// <summary>
        /// True when the directory root holds object source rather than known KB metadata.
        ///
        /// Shallow on purpose. The point is to tell a native KB from an export of
        /// one; a deep walk would cost exactly what this check exists to avoid.
        /// </summary>
        private static readonly HashSet<string> SourceExtensions = new HashSet<string>(
            new[] { "txt", "gxd", "xml", "gx", "prc", "trn", "wp", "sdp" },
            StringComparer.OrdinalIgnoreCase);

        internal static bool HasSourceText(string directory)
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                // Match gxwire's root metadata exclusion; these are not object exports.
                string stem = Path.GetFileNameWithoutExtension(path);
                if (new[] { "CategoryViewConfig", "iar_objs", "nav_objs" }
                    .Contains(stem, StringComparer.OrdinalIgnoreCase)) continue;
                string ext = Path.GetExtension(path).TrimStart('.');
                if (ext.Length > 0 && SourceExtensions.Contains(ext)) return true;
            }
            return false;
        }
    }
}
