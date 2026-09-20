using System;
using System.IO;

namespace GxMcp.Worker
{
    /// <summary>Guards the worker's immutable KB binding.</summary>
    public sealed class WorkerKbBinding
    {
        private readonly string _openedPath;
        public WorkerKbBinding(string openedPath)
        {
            _openedPath = Canonical(openedPath);
        }

        public string OpenedPath => _openedPath;

        public void ValidateEnvironment(string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !SamePath(_openedPath, value))
                throw new InvalidOperationException("GX_KB_PATH does not match the gateway-selected KB; worker rebind rejected.");
        }

        public void RejectRebind(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !SamePath(_openedPath, value))
                throw new InvalidOperationException("GX_KB_PATH cannot be rebound after the KB has been opened.");
        }

        private static bool SamePath(string left, string right)
            => string.Equals(left, Canonical(right), StringComparison.OrdinalIgnoreCase);

        private static string Canonical(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("KB path is required.", nameof(path));
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
