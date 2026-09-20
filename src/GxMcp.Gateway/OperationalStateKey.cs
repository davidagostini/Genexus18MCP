using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GxMcp.Gateway
{
    /// <summary>Identity of durable operational state. Never use an alias or a path alone.</summary>
    internal readonly struct OperationalStateKey : IEquatable<OperationalStateKey>
    {
        internal OperationalStateKey(StateScopeId stateScopeId, string kbId, long generation)
        {
            if (string.IsNullOrWhiteSpace(kbId)) throw new ArgumentException("KB identity is required.", nameof(kbId));
            if (generation < 0) throw new ArgumentOutOfRangeException(nameof(generation));
            StateScopeId = stateScopeId;
            KbId = kbId.Trim();
            Generation = generation;
        }

        internal StateScopeId StateScopeId { get; }
        internal string KbId { get; }
        internal long Generation { get; }

        internal string Token => StateScopeId + "-" + Hash(KbId) + "-g" + Generation.ToString(CultureInfo.InvariantCulture);
        internal string JournalKey(string item) => Token + "|journal|" + (item ?? string.Empty);
        internal string ReceiptKey(string item) => Token + "|receipt|" + (item ?? string.Empty);
        internal string SnapshotKey(string item) => Token + "|snapshot|" + (item ?? string.Empty);
        internal string JobKey(string item) => Token + "|job|" + (item ?? string.Empty);

        public bool Equals(OperationalStateKey other) => StateScopeId == other.StateScopeId
            && string.Equals(KbId, other.KbId, StringComparison.Ordinal) && Generation == other.Generation;
        public override bool Equals(object? obj) => obj is OperationalStateKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(StateScopeId, KbId, Generation);
        public static bool operator ==(OperationalStateKey left, OperationalStateKey right) => left.Equals(right);
        public static bool operator !=(OperationalStateKey left, OperationalStateKey right) => !left.Equals(right);

        private static string Hash(string value)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant().Substring(0, 32);
        }
    }

    internal static class OperationalStatePaths
    {
        internal static string For(StateScope scope, string kbId, long generation, string component, string fileName)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (string.IsNullOrWhiteSpace(component) || string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("Operational component and file name are required.");
            var key = new OperationalStateKey(scope.Id, kbId, generation);
            string componentPath = Path.Combine(scope.RootDirectory, component, key.Token);
            Directory.CreateDirectory(componentPath);
            return Path.Combine(componentPath, fileName);
        }
    }
}
