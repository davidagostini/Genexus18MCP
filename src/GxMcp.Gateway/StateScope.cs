using System;
using System.IO;
using System.Security.Cryptography;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Opaque, process-local discriminator for operational state.
    /// It is deliberately not derived from a KB path, alias, or display name.
    /// </summary>
    internal readonly struct StateScopeId : IEquatable<StateScopeId>
    {
        private const int HexLength = 32;
        private readonly string _value;

        private StateScopeId(string value)
        {
            _value = value;
        }

        internal static StateScopeId Create()
        {
            Span<byte> bytes = stackalloc byte[HexLength / 2];
            RandomNumberGenerator.Fill(bytes);
            return new StateScopeId(Convert.ToHexString(bytes).ToLowerInvariant());
        }

        internal static StateScopeId Parse(string value)
        {
            if (value == null || value.Length != HexLength)
                throw new ArgumentException("State scope identifiers must be 32 hexadecimal characters.", nameof(value));

            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                bool hexadecimal = character is >= '0' and <= '9'
                    or >= 'a' and <= 'f';
                if (!hexadecimal)
                    throw new ArgumentException("State scope identifiers must be lowercase hexadecimal.", nameof(value));
            }

            return new StateScopeId(value);
        }

        public override string ToString() => _value;

        public bool Equals(StateScopeId other) => string.Equals(_value, other._value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is StateScopeId other && Equals(other);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_value);
        public static bool operator ==(StateScopeId left, StateScopeId right) => left.Equals(right);
        public static bool operator !=(StateScopeId left, StateScopeId right) => !left.Equals(right);
    }

    /// <summary>
    /// Owns the process-specific operational state directory.
    /// </summary>
    internal sealed class StateScope : IDisposable
    {
        private const string StateDirectoryName = "state";
        private static readonly StateScopeId ProcessId = StateScopeId.Create();
        private readonly string _baseDirectory;
        private bool _disposed;

        private StateScope(string baseDirectory, StateScopeId id)
        {
            _baseDirectory = baseDirectory;
            Id = id;
            RootDirectory = Path.Combine(baseDirectory, id.ToString());
            EnsureScopePathIsOwned(baseDirectory, RootDirectory, id);

            Directory.CreateDirectory(RootDirectory);
            EnsureNotReparsePoint(RootDirectory);
            JournalDirectory = CreateChild("journal");
            RecoveryDirectory = CreateChild("recovery");
            JobsDirectory = CreateChild("jobs");
            LogsDirectory = CreateChild("logs");
        }

        internal static StateScopeId ProcessScopeId => ProcessId;
        internal StateScopeId Id { get; }
        internal string RootDirectory { get; }
        internal string JournalDirectory { get; }
        internal string RecoveryDirectory { get; }
        internal string JobsDirectory { get; }
        internal string LogsDirectory { get; }

        internal OperationalStateKey ForKb(string kbId, long generation)
            => new OperationalStateKey(Id, kbId, generation);

        internal string JournalPath(string kbId, long generation, string fileName = "mutation-operations.json")
            => OperationalStatePaths.For(this, kbId, generation, "journal", fileName);

        internal string RecoveryPath(string kbId, long generation, string fileName = "mutation-recovery.json")
            => OperationalStatePaths.For(this, kbId, generation, "recovery", fileName);

        internal string JobsPath(string kbId, long generation, string fileName = "jobs.json")
            => OperationalStatePaths.For(this, kbId, generation, "jobs", fileName);

        internal string LogsPath(string kbId, long generation, string fileName = "worker_debug.log")
            => OperationalStatePaths.For(this, kbId, generation, "logs", fileName);

        internal static StateScope Create(string? baseDirectory = null, StateScopeId? id = null)
        {
            string root = baseDirectory ?? GetDefaultBaseDirectory();
            root = Path.GetFullPath(root);
            Directory.CreateDirectory(root);
            EnsureNotReparsePoint(root);

            // The inherited OS ACL is intentionally preserved: this component never
            // broadens access or grants permissions to other users. Retention is also
            // explicit: Dispose removes only this scope; no background scavenger runs.
            return new StateScope(root, id ?? ProcessId);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            // Refuse to recurse through a reparse point and verify the exact parent
            // and leaf before deleting, so cleanup cannot target a sibling scope.
            if (!Directory.Exists(RootDirectory) || IsReparsePoint(RootDirectory))
                return;

            string expectedParent = TrimTrailingSeparators(_baseDirectory);
            string actualParent = TrimTrailingSeparators(Path.GetDirectoryName(RootDirectory) ?? string.Empty);
            string expectedLeaf = Id.ToString();
            string actualLeaf = Path.GetFileName(RootDirectory);
            if (!string.Equals(expectedParent, actualParent, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(expectedLeaf, actualLeaf, StringComparison.Ordinal))
                return;

            Directory.Delete(RootDirectory, recursive: true);
        }

        private string CreateChild(string name)
        {
            string path = Path.Combine(RootDirectory, name);
            Directory.CreateDirectory(path);
            EnsureNotReparsePoint(path);
            return path;
        }

        private static string GetDefaultBaseDirectory()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                localAppData = Path.GetTempPath();
            return Path.Combine(localAppData, "GeneXusMcp", StateDirectoryName);
        }

        private static void EnsureScopePathIsOwned(string baseDirectory, string scopeDirectory, StateScopeId id)
        {
            string parent = TrimTrailingSeparators(Path.GetDirectoryName(scopeDirectory) ?? string.Empty);
            string expectedParent = TrimTrailingSeparators(baseDirectory);
            string leaf = Path.GetFileName(scopeDirectory);
            if (!string.Equals(parent, expectedParent, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(leaf, id.ToString(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The state scope path is not owned by its validated identifier.");
            }
        }

        private static void EnsureNotReparsePoint(string path)
        {
            if (IsReparsePoint(path))
                throw new IOException("Operational state directories cannot be reparse points.");
        }

        private static bool IsReparsePoint(string path)
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }

        private static string TrimTrailingSeparators(string path)
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
