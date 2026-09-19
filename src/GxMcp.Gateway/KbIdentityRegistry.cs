using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;

namespace GxMcp.Gateway
{
    public sealed class KbIdentity
    {
        public string KbId { get; }
        public string Alias { get; }
        public long ContextGeneration { get; internal set; }
        internal string CanonicalPath { get; }

        internal KbIdentity(string kbId, string alias, string canonicalPath, long contextGeneration)
        {
            KbId = kbId;
            Alias = alias;
            CanonicalPath = canonicalPath;
            ContextGeneration = contextGeneration;
        }
    }

    public sealed class KbIdentityConflictException : Exception
    {
        public string Code { get; }

        public KbIdentityConflictException(string code, string message) : base(message)
        {
            Code = code;
        }
    }

    /// <summary>
    /// Private profile-scoped mapping from a canonical physical KB root to an
    /// opaque stable identity. The canonical path is deliberately not exposed
    /// by MCP payloads; it is retained only in this local registry.
    /// </summary>
    public sealed class KbIdentityRegistry
    {
        private const int CurrentSchemaVersion = 1;
        private readonly string _registryPath;
        private readonly string _mutexName;

        public KbIdentityRegistry(string stateRoot)
        {
            if (string.IsNullOrWhiteSpace(stateRoot))
                throw new ArgumentException("Identity registry root is required.", nameof(stateRoot));

            string root = Path.GetFullPath(stateRoot);
            Directory.CreateDirectory(root);
            _registryPath = Path.Combine(root, "kb-identities.json");
            _mutexName = "Global\\GenexusMcp.KbIdentityRegistry." +
                Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root)))
                    .Replace("/", "_").Replace("+", "-").TrimEnd('=');
        }

        public KbIdentity GetOrCreate(string alias, string path)
        {
            string normalizedAlias = NormalizeAlias(alias);
            string canonicalPath = CanonicalizePath(path);
            using var mutex = new Mutex(false, _mutexName);
            if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
                throw new KbIdentityConflictException("KB_IDENTITY_REGISTRY_UNAVAILABLE", "KB identity registry lock could not be acquired.");

            try
            {
                var document = ReadDocument();
                var byAlias = document.Entries.FirstOrDefault(e => string.Equals(e.Alias, normalizedAlias, StringComparison.OrdinalIgnoreCase));
                if (byAlias != null && !string.Equals(byAlias.CanonicalPath, canonicalPath, StringComparison.OrdinalIgnoreCase))
                    throw new KbIdentityConflictException("KB_ALIAS_CONFLICT", $"KB alias '{normalizedAlias}' is already bound to another identity.");

                var byPath = document.Entries.FirstOrDefault(e => string.Equals(e.CanonicalPath, canonicalPath, StringComparison.OrdinalIgnoreCase));
                if (byPath != null)
                {
                    if (byAlias == null)
                    {
                        byPath.Alias = normalizedAlias;
                        WriteDocument(document);
                    }
                    return ToIdentity(byPath);
                }

                var entry = new IdentityEntry
                {
                    KbId = Guid.NewGuid().ToString("N"),
                    Alias = normalizedAlias,
                    CanonicalPath = canonicalPath,
                    ContextGeneration = 1
                };
                document.Entries.Add(entry);
                WriteDocument(document);
                return ToIdentity(entry);
            }
            catch (KbIdentityConflictException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new KbIdentityConflictException("KB_IDENTITY_REGISTRY_UNAVAILABLE", "KB identity registry is unavailable: " + ex.Message);
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
        }

        /// <summary>
        /// Explicitly changes the physical path bound to an alias. This is the
        /// only operation that may replace a path whose previous folder is gone
        /// or move an alias to a different physical identity.
        /// </summary>
        public KbIdentity Rebind(string alias, string path)
        {
            string normalizedAlias = NormalizeAlias(alias);
            string canonicalPath = CanonicalizePath(path);
            using var mutex = new Mutex(false, _mutexName);
            if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
                throw new KbIdentityConflictException("KB_IDENTITY_REGISTRY_UNAVAILABLE", "KB identity registry lock could not be acquired.");

            try
            {
                var document = ReadDocument();
                var byAlias = document.Entries.FirstOrDefault(e => string.Equals(e.Alias, normalizedAlias, StringComparison.OrdinalIgnoreCase));
                var byPath = document.Entries.FirstOrDefault(e => string.Equals(e.CanonicalPath, canonicalPath, StringComparison.OrdinalIgnoreCase));

                if (byPath != null && byAlias != null && !ReferenceEquals(byAlias, byPath))
                    throw new KbIdentityConflictException("KB_ALIAS_CONFLICT", $"KB alias '{normalizedAlias}' is already bound to another identity.");

                if (byAlias == null)
                {
                    if (byPath == null)
                    {
                        byPath = new IdentityEntry
                        {
                            KbId = Guid.NewGuid().ToString("N"),
                            Alias = normalizedAlias,
                            CanonicalPath = canonicalPath,
                            ContextGeneration = 1
                        };
                        document.Entries.Add(byPath);
                        WriteDocument(document);
                    }
                    else if (!string.Equals(byPath.Alias, normalizedAlias, StringComparison.OrdinalIgnoreCase))
                    {
                        byPath.Alias = normalizedAlias;
                        WriteDocument(document);
                    }

                    return ToIdentity(byPath);
                }

                if (string.Equals(byAlias.CanonicalPath, canonicalPath, StringComparison.OrdinalIgnoreCase))
                    return ToIdentity(byAlias);

                if (byPath != null)
                    throw new KbIdentityConflictException("KB_ALIAS_CONFLICT", $"KB path '{canonicalPath}' is already bound to another identity.");

                if (Directory.Exists(byAlias.CanonicalPath))
                {
                    document.Entries.Remove(byAlias);
                    var replacement = new IdentityEntry
                    {
                        KbId = Guid.NewGuid().ToString("N"),
                        Alias = normalizedAlias,
                        CanonicalPath = canonicalPath,
                        ContextGeneration = 1
                    };
                    document.Entries.Add(replacement);
                    WriteDocument(document);
                    return ToIdentity(replacement);
                }

                byAlias.CanonicalPath = canonicalPath;
                checked { byAlias.ContextGeneration++; }
                WriteDocument(document);
                return ToIdentity(byAlias);
            }
            catch (KbIdentityConflictException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new KbIdentityConflictException("KB_IDENTITY_REGISTRY_UNAVAILABLE", "KB identity registry is unavailable: " + ex.Message);
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
        }

        public static string NormalizeAlias(string alias)
        {
            if (string.IsNullOrWhiteSpace(alias))
                throw new KbIdentityConflictException("KB_ALIAS_CONFLICT", "KB alias is required.");
            return alias.Trim().ToLowerInvariant();
        }

        public static string CanonicalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new KbIdentityConflictException("KB_PATH_NOT_ALLOWED", "KB path must be absolute.");
            if (!Directory.Exists(path))
                throw new KbIdentityConflictException("KB_NOT_FOUND", "KB path does not exist.");

            var info = new DirectoryInfo(path);
            try
            {
                var resolved = info.ResolveLinkTarget(true);
                if (resolved is DirectoryInfo resolvedDirectory)
                    info = resolvedDirectory;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException)
            {
                throw new KbIdentityConflictException("KB_IDENTITY_REGISTRY_UNAVAILABLE", "KB path cannot be resolved safely.");
            }

            return info.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private IdentityDocument ReadDocument()
        {
            if (!File.Exists(_registryPath))
                return new IdentityDocument { SchemaVersion = CurrentSchemaVersion };

            var document = JsonConvert.DeserializeObject<IdentityDocument>(File.ReadAllText(_registryPath));
            if (document == null || document.SchemaVersion != CurrentSchemaVersion || document.Entries == null)
                throw new InvalidDataException("KB identity registry schema is invalid.");
            return document;
        }

        private void WriteDocument(IdentityDocument document)
        {
            document.SchemaVersion = CurrentSchemaVersion;
            string tempPath = _registryPath + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tempPath, JsonConvert.SerializeObject(document, Formatting.Indented));
            try
            {
                if (File.Exists(_registryPath))
                    File.Replace(tempPath, _registryPath, _registryPath + ".bak", true);
                else
                    File.Move(tempPath, _registryPath);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        private static KbIdentity ToIdentity(IdentityEntry entry)
            => new KbIdentity(entry.KbId, entry.Alias, entry.CanonicalPath, entry.ContextGeneration);

        private sealed class IdentityDocument
        {
            [JsonProperty("schemaVersion")]
            public int SchemaVersion { get; set; }
            [JsonProperty("entries")]
            public List<IdentityEntry> Entries { get; set; } = new List<IdentityEntry>();
        }

        private sealed class IdentityEntry
        {
            [JsonProperty("kbId")]
            public string KbId { get; set; } = string.Empty;
            [JsonProperty("alias")]
            public string Alias { get; set; } = string.Empty;
            [JsonProperty("canonicalPath")]
            public string CanonicalPath { get; set; } = string.Empty;
            [JsonProperty("contextGeneration")]
            public long ContextGeneration { get; set; }
        }
    }
}
