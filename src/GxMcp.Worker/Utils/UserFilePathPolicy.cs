using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GxMcp.Worker.Utils
{
    /// <summary>
    /// Resolves file paths supplied through MCP arguments against the worker's
    /// configured roots. Relative paths are anchored at the active KB rather
    /// than the process CWD. An additional root is opt-in through
    /// GXMCP_EXTERNAL_IO_ROOT for deliberate exchange with a staging folder.
    /// </summary>
    internal sealed class UserFilePathPolicy : IUserFilePathPolicy
    {
        public const string ExternalRootEnvironmentVariable = "GXMCP_EXTERNAL_IO_ROOT";

        private readonly Func<string> _kbPathProvider;
        private readonly string _baseDirectory;

        public UserFilePathPolicy(Func<string> kbPathProvider, string baseDirectory = null)
        {
            _kbPathProvider = kbPathProvider;
            _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : baseDirectory;
        }

        public bool TryResolveReadPath(string rawPath, out string fullPath, out string error)
        {
            return TryResolve(rawPath, out fullPath, out error);
        }

        public bool TryResolveWritePath(string rawPath, out string fullPath, out string error)
        {
            return TryResolve(rawPath, out fullPath, out error);
        }

        public string AllowedRootsDescription()
        {
            var roots = GetAllowedRoots().Select(x => x.Path).ToArray();
            return roots.Length == 0 ? "<none configured>" : string.Join("; ", roots);
        }

        private bool TryResolve(string rawPath, out string fullPath, out string error)
        {
            fullPath = null;
            error = null;

            string input = CleanInput(rawPath);
            if (string.IsNullOrWhiteSpace(input))
            {
                error = "The path is empty.";
                return false;
            }

            List<RootEntry> roots = GetAllowedRoots();
            if (roots.Count == 0)
            {
                error = "No configured file root is available for this worker.";
                return false;
            }

            string anchor = roots[0].Path;
            string candidate;
            try
            {
                candidate = Path.IsPathRooted(input)
                    ? Path.GetFullPath(input)
                    : Path.GetFullPath(Path.Combine(anchor, input));
            }
            catch (Exception ex)
            {
                error = "The path is invalid: " + ex.Message;
                return false;
            }

            foreach (RootEntry root in roots)
            {
                string resolved;
                if (PathSafety.TryResolveWithinRoot(root.Path, candidate, out resolved))
                {
                    fullPath = resolved;
                    return true;
                }
            }

            fullPath = candidate;
            error = "The path resolves outside the configured file roots. Allowed roots: "
                + AllowedRootsDescription();
            return false;
        }

        private List<RootEntry> GetAllowedRoots()
        {
            var result = new List<RootEntry>();
            AddRoot(result, SafeGet(_kbPathProvider));
            AddRoot(result, Environment.GetEnvironmentVariable("GX_PROGRAM_DIR"));
            AddRoot(result, Environment.GetEnvironmentVariable(ExternalRootEnvironmentVariable));
            return result;
        }

        private void AddRoot(List<RootEntry> roots, string rawRoot)
        {
            string root = CleanInput(rawRoot);
            if (string.IsNullOrWhiteSpace(root)) return;
            try
            {
                if (!Path.IsPathRooted(root))
                    root = Path.Combine(_baseDirectory, root);
                root = Path.GetFullPath(root);
                string pathRoot = Path.GetPathRoot(root);
                if (!string.IsNullOrEmpty(pathRoot)
                    && !string.Equals(root, pathRoot, StringComparison.OrdinalIgnoreCase))
                {
                    root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }

                if (!roots.Any(x => string.Equals(x.Path, root, StringComparison.OrdinalIgnoreCase)))
                    roots.Add(new RootEntry(root));
            }
            catch
            {
                // An invalid optional root must not make all other configured
                // roots unusable. The caller receives the valid-root list.
            }
        }

        private static string SafeGet(Func<string> provider)
        {
            try { return provider == null ? null : provider(); }
            catch { return null; }
        }

        internal static string CleanInput(string value)
        {
            string result = (value ?? string.Empty).Trim();
            while (result.Length >= 2 && result[0] == '"' && result[result.Length - 1] == '"')
                result = result.Substring(1, result.Length - 2).Trim();
            try { result = Environment.ExpandEnvironmentVariables(result); } catch { }
            return result;
        }

        private sealed class RootEntry
        {
            public RootEntry(string path)
            {
                Path = path;
            }

            public string Path { get; }
        }
    }
}
