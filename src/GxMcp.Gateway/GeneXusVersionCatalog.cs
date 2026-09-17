using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// The explicit compatibility contract for GeneXus installations.
    /// Add a major to config/gx-versions.json only after the Worker has a
    /// build/runtime validation path for it.
    /// </summary>
    internal static class GeneXusVersionCatalog
    {
        private sealed class CatalogData
        {
            internal string PrimaryMajor { get; set; } = "18";
            internal IReadOnlyList<string> SupportedMajors { get; set; } = new[] { "17", "18" };
            internal string PrimaryInstallPath { get; set; } = @"C:\Program Files (x86)\GeneXus\GeneXus18";
            internal string Source { get; set; } = "built-in-fallback";
            internal JArray Entries { get; set; } = new JArray();
        }

        private static readonly CatalogData Data = Load();

        internal static string PrimaryMajor => Data.PrimaryMajor;
        internal static IReadOnlyList<string> SupportedMajors => Data.SupportedMajors;
        internal static string PrimaryInstallPath => Data.PrimaryInstallPath;
        internal static string CatalogSource => Data.Source;

        private static readonly Regex MajorRegex =
            new Regex(@"^\s*(?<major>\d+)", RegexOptions.Compiled);

        internal static string SupportedMajorsDisplay
        {
            get { return string.Join(", ", SupportedMajors); }
        }

        internal static string? GetMajor(string? version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;

            Match match = MajorRegex.Match(version);
            return match.Success ? match.Groups["major"].Value : null;
        }

        internal static bool IsSupported(string? version)
        {
            string? major = GetMajor(version);
            return IsSupportedMajor(major);
        }

        internal static string? GetMatchingMajor(string? version)
        {
            string? major = GetMajor(version);
            return IsSupportedMajor(major) ? major : null;
        }

        private static bool IsSupportedMajor(string? major)
        {
            if (string.IsNullOrEmpty(major)) return false;

            for (int i = 0; i < SupportedMajors.Count; i++)
                if (string.Equals(SupportedMajors[i], major, StringComparison.Ordinal)) return true;

            return false;
        }

        internal static JObject ToDiagnosticObject()
        {
            return new JObject
            {
                ["source"] = CatalogSource,
                ["primaryMajor"] = PrimaryMajor,
                ["supportedMajors"] = JArray.FromObject(SupportedMajors),
                ["entries"] = Data.Entries.DeepClone()
            };
        }

        private static CatalogData Load()
        {
            string[] candidates = GetCatalogCandidates();
            foreach (string path in candidates)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    JObject document = JObject.Parse(File.ReadAllText(path));
                    string? primary = document["primaryMajor"]?.ToString();
                    var entries = document["supportedMajors"] as JArray;
                    if (string.IsNullOrWhiteSpace(primary) || entries == null || entries.Count == 0) continue;

                    var majors = entries
                        .OfType<JObject>()
                        .Select(entry => entry["major"]?.ToString())
                        .Where(major => !string.IsNullOrWhiteSpace(major))
                        .Select(major => major!)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    if (majors.Length == 0 || !majors.Contains(primary, StringComparer.Ordinal)) continue;

                    string? primaryPath = entries
                        .OfType<JObject>()
                        .Where(entry => string.Equals(entry["major"]?.ToString(), primary, StringComparison.Ordinal))
                        .Select(entry => entry["defaultInstallPath"]?.ToString())
                        .FirstOrDefault(pathValue => !string.IsNullOrWhiteSpace(pathValue));
                    if (string.IsNullOrWhiteSpace(primaryPath)) continue;

                    return new CatalogData
                    {
                        PrimaryMajor = primary,
                        SupportedMajors = majors,
                        PrimaryInstallPath = primaryPath,
                        Source = path,
                        Entries = entries.DeepClone() as JArray ?? new JArray()
                    };
                }
                catch
                {
                    // A broken optional catalog must not prevent the Gateway from
                    // starting. The diagnostic source exposes that fallback was used.
                }
            }

            return new CatalogData
            {
                Entries = new JArray
                {
                    new JObject { ["major"] = "16", ["displayName"] = "GeneXus 16" },
                    new JObject { ["major"] = "17", ["displayName"] = "GeneXus 17" },
                    new JObject { ["major"] = "18", ["displayName"] = "GeneXus 18" }
                }
            };
        }

        private static string[] GetCatalogCandidates()
        {
            var paths = new List<string>();
            string? configured = Environment.GetEnvironmentVariable("GXMCP_VERSION_CATALOG");
            if (!string.IsNullOrWhiteSpace(configured)) paths.Add(configured);

            string baseDirectory = AppContext.BaseDirectory;
            paths.Add(Path.Combine(baseDirectory, "config", "gx-versions.json"));
            paths.Add(Path.Combine(baseDirectory, "gx-versions.json"));

            string current = Directory.GetCurrentDirectory();
            paths.Add(Path.Combine(current, "config", "gx-versions.json"));

            return paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path =>
                {
                    try { return Path.GetFullPath(path); }
                    catch { return path; }
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }
}
