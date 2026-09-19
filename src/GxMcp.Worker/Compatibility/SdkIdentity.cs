using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Compatibility
{
    /// <summary>
    /// Identifies the SDK selected for this Worker without binding the Worker to
    /// a particular GeneXus major. The path is useful for diagnostics, while the
    /// version file or executable metadata is the preferred identity evidence.
    /// </summary>
    internal sealed class SdkIdentity
    {
        private static readonly Regex MajorRegex = new Regex(
            @"^\s*(?<major>\d+)", RegexOptions.Compiled);

        internal string InstallationPath { get; private set; }
        internal string Version { get; private set; }
        internal string Major { get; private set; }
        internal string DetectionSource { get; private set; }
        internal bool? CatalogSupported { get; private set; }
        internal string CatalogSource { get; private set; }
        internal IReadOnlyList<string> SupportedMajors { get; private set; }

        internal static SdkIdentity Detect()
        {
            string path = Environment.GetEnvironmentVariable("GX_PROGRAM_DIR");
            if (string.IsNullOrWhiteSpace(path))
                path = Environment.GetEnvironmentVariable("GX_PATH");
            CatalogInfo catalog = LoadCatalog();

            if (string.IsNullOrWhiteSpace(path))
            {
                return Create(null, null, null, "not-configured", catalog);
            }

            try { path = Path.GetFullPath(path.Trim().Trim('"')); }
            catch { return Create(path, null, "invalid-path", null, catalog); }
            foreach (string fileName in new[] { "version.txt", "Version.txt", "GeneXus.version" })
            {
                string versionPath = Path.Combine(path, fileName);
                try
                {
                    if (!File.Exists(versionPath)) continue;
                    string version = File.ReadAllLines(versionPath)
                        .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim();
                    if (!string.IsNullOrWhiteSpace(version))
                        return Create(path, version, "version-file", null, catalog);
                }
                catch { }
            }

            try
            {
                string exePath = Path.Combine(path, "GeneXus.exe");
                if (File.Exists(exePath))
                {
                    FileVersionInfo info = FileVersionInfo.GetVersionInfo(exePath);
                    string version = info.ProductVersion ?? info.FileVersion;
                    if (!string.IsNullOrWhiteSpace(version))
                        return Create(path, version.Trim(), "executable-metadata", null, catalog);
                }
            }
            catch { }

            string pathMajor = ExtractMajor(Path.GetFileName(path));
            return Create(path, null, "path-name", pathMajor, catalog);
        }

        internal static SdkIdentity FromVersion(
            string version,
            string detectionSource,
            string installationPath = null,
            IReadOnlyList<string> supportedMajors = null)
        {
            var catalog = new CatalogInfo
            {
                SupportedMajors = supportedMajors ?? new[] { "16", "17", "18" },
                Source = "test"
            };
            return Create(installationPath, version, detectionSource, null, catalog);
        }

        internal JObject ToJson()
        {
            return new JObject
            {
                ["installationPath"] = InstallationPath,
                ["version"] = Version,
                ["major"] = Major,
                ["detectionSource"] = DetectionSource,
                ["catalogSupported"] = CatalogSupported,
                ["catalogSource"] = CatalogSource,
                ["supportedMajors"] = JArray.FromObject(SupportedMajors ?? new string[0])
            };
        }

        private static SdkIdentity Create(
            string path,
            string version,
            string detectionSource,
            string fallbackMajor,
            CatalogInfo catalog)
        {
            string major = ExtractMajor(version) ?? fallbackMajor;
            bool? supported = string.IsNullOrWhiteSpace(major)
                ? (bool?)null
                : catalog.SupportedMajors.Contains(major, StringComparer.Ordinal);
            return new SdkIdentity
            {
                InstallationPath = path,
                Version = version,
                Major = major,
                DetectionSource = detectionSource,
                CatalogSupported = supported,
                CatalogSource = catalog.Source,
                SupportedMajors = catalog.SupportedMajors
            };
        }

        private static string ExtractMajor(string versionOrName)
        {
            if (string.IsNullOrWhiteSpace(versionOrName)) return null;
            Match match = MajorRegex.Match(versionOrName);
            return match.Success ? match.Groups["major"].Value : null;
        }

        private sealed class CatalogInfo
        {
            internal IReadOnlyList<string> SupportedMajors { get; set; } = new[] { "16", "17", "18" };
            internal string Source { get; set; } = "built-in-fallback";
        }

        private static CatalogInfo LoadCatalog()
        {
            var candidates = new List<string>();
            string configured = Environment.GetEnvironmentVariable("GXMCP_VERSION_CATALOG");
            if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "config", "gx-versions.json"));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "gx-versions.json"));
            candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "config", "gx-versions.json"));

            foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    JObject document = JObject.Parse(File.ReadAllText(candidate));
                    string primary = document["primaryMajor"]?.ToString();
                    var majors = (document["supportedMajors"] as JArray)?
                        .OfType<JObject>()
                        .Select(item => item["major"]?.ToString())
                        .Where(major => !string.IsNullOrWhiteSpace(major))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    if (string.IsNullOrWhiteSpace(primary) || majors == null || majors.Length == 0
                        || !majors.Contains(primary, StringComparer.Ordinal)) continue;
                    bool hasPrimaryPath = (document["supportedMajors"] as JArray)
                        .OfType<JObject>()
                        .Any(item => string.Equals(item["major"]?.ToString(), primary, StringComparison.Ordinal)
                            && !string.IsNullOrWhiteSpace(item["defaultInstallPath"]?.ToString()));
                    if (!hasPrimaryPath) continue;
                    return new CatalogInfo { SupportedMajors = majors, Source = candidate };
                }
                catch { }
            }

            return new CatalogInfo();
        }
    }
}
