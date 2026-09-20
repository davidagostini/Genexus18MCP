using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Persists post-timeout mutation fences. The journal is deliberately a
    /// recovery fence, not a content snapshot: a restart must block writes
    /// until a caller explicitly re-reads the affected part.
    /// </summary>
    internal sealed class MutationRecoveryRegistry
    {
        private const string JournalSchemaVersion = "genexus-mutation-recovery/1";
        private const int MaxJournalEntries = 1024;
        private const long MaxJournalBytes = 1024 * 1024;
        private static readonly TimeSpan JournalRetention = TimeSpan.FromDays(7);

        private readonly ConcurrentDictionary<string, RecoveryRequirement> _pending = new();
        private readonly string? _journalPath;
        private readonly OperationalStateKey? _defaultOwner;
        private readonly object _journalLock = new object();
        private volatile bool _journalHealthy = true;
        private string _journalError = string.Empty;

        public MutationRecoveryRegistry(string? journalPath = null)
        {
            _journalPath = journalPath;
            LoadJournal();
        }

        internal MutationRecoveryRegistry(string journalPath, OperationalStateKey owner)
        {
            _defaultOwner = owner;
            _journalPath = journalPath;
            LoadJournal();
        }

        internal MutationRecoveryRegistry(StateScope scope, string kbId, long generation)
        {
            _defaultOwner = scope.ForKb(kbId, generation);
            _journalPath = scope.RecoveryPath(kbId, generation);
            LoadJournal();
        }

        public bool IsHealthy => _journalHealthy;
        public string JournalError => _journalError;
        public int Count => _pending.Count;
        public IReadOnlyCollection<RecoveryRequirement> Pending => _pending.Values
            .OrderBy(item => item.RequiredAtUtc)
            .ThenBy(item => item.KbAlias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Target, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Part, StringComparer.OrdinalIgnoreCase)
            .ToList();

        public void RequireRead(string? kbAlias, string? target, string? part, string? operationId)
        {
            RequireReadCore(_defaultOwner, kbAlias, target, part, operationId);
        }

        internal void RequireRead(OperationalStateKey owner, string target, string? part, string? operationId)
        {
            RequireReadCore(owner, owner.KbId, target, part, operationId);
        }

        private void RequireReadCore(OperationalStateKey? owner, string? kbAlias, string? target, string? part, string? operationId)
        {
            if (string.IsNullOrWhiteSpace(kbAlias) || string.IsNullOrWhiteSpace(target)) return;
            var requirement = new RecoveryRequirement
            {
                KbAlias = kbAlias.Trim(),
                OwnerKey = owner.HasValue ? owner.Value.Token : string.Empty,
                Target = target.Trim(),
                Part = string.IsNullOrWhiteSpace(part) ? "Source" : part.Trim(),
                OperationId = operationId?.Trim() ?? string.Empty,
                RequiredAtUtc = DateTime.UtcNow
            };
            _pending[Key(requirement.OwnerKey, requirement.KbAlias, requirement.Target, requirement.Part)] = requirement;
            PersistJournal();
        }

        public bool TryGet(string? kbAlias, string? target, out RecoveryRequirement requirement)
        {
            requirement = null!;
            if (string.IsNullOrWhiteSpace(kbAlias) || string.IsNullOrWhiteSpace(target)) return false;
            string prefix = (_defaultOwner.HasValue ? _defaultOwner.Value.Token.ToLowerInvariant() : string.Empty)
                + "|" + Prefix(kbAlias, target) + "|";
            var found = _pending
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(pair => pair.Value)
                .OrderBy(item => item.RequiredAtUtc)
                .FirstOrDefault();
            if (found == null) return false;
            requirement = found;
            return true;
        }

        public bool TryGet(string? kbAlias, string? target, string? part, out RecoveryRequirement requirement)
        {
            requirement = null!;
            if (string.IsNullOrWhiteSpace(kbAlias) || string.IsNullOrWhiteSpace(target)) return false;
            return _pending.TryGetValue(Key(_defaultOwner.HasValue ? _defaultOwner.Value.Token : string.Empty, kbAlias, target, part), out requirement!);
        }

        internal bool TryGet(OperationalStateKey owner, string target, string? part, out RecoveryRequirement requirement)
        {
            return _pending.TryGetValue(Key(owner.Token, owner.KbId, target, part), out requirement!);
        }

        internal bool ConfirmRead(OperationalStateKey owner, string target, string? part)
        {
            if (!TryGet(owner, target, part, out var requirement)) return false;
            bool removed = _pending.TryRemove(Key(owner.Token, requirement.KbAlias, requirement.Target, requirement.Part), out _);
            if (removed) PersistJournal();
            return removed;
        }

        public bool ConfirmRead(string? kbAlias, string? target, string? part)
        {
            if (!TryGet(kbAlias, target, part, out var requirement)) return false;
            bool removed = _pending.TryRemove(Key(_defaultOwner.HasValue ? _defaultOwner.Value.Token : string.Empty, requirement.KbAlias, requirement.Target, requirement.Part), out _);
            if (removed) PersistJournal();
            return removed;
        }

        public static JObject BuildBlockedEnvelope(RecoveryRequirement requirement)
        {
            return new JObject
            {
                ["status"] = "error",
                ["target"] = requirement.Target,
                ["operationId"] = requirement.OperationId,
                ["error"] = new JObject
                {
                    ["code"] = "PostTimeoutReadRequired",
                    ["message"] = "A previous write timed out or was cancelled, so its persisted state is unknown.",
                    ["hint"] = "Call genexus_read for the target and part. A successful full read clears this recovery fence; then retry from the returned versionToken.",
                    ["retryable"] = false,
                    ["reconciliationRequired"] = true,
                    ["nextSteps"] = new JArray
                    {
                        new JObject
                        {
                            ["tool"] = "genexus_read",
                            ["args"] = new JObject { ["name"] = requirement.Target, ["part"] = requirement.Part },
                            ["why"] = "Confirm whether the timed-out mutation was persisted before retrying."
                        }
                    }
                }
            };
        }

        public static JObject BuildJournalBlockedEnvelope(string? journalError)
        {
            return new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject
                {
                    ["code"] = "MutationRecoveryJournalUnavailable",
                    ["message"] = "The mutation recovery journal could not be trusted; writes are blocked until it is repaired.",
                    ["hint"] = "Inspect the journal under the Gateway state directory, restore a valid versioned file, then restart the Gateway. Read-only calls remain available.",
                    ["retryable"] = false,
                    ["reconciliationRequired"] = true,
                    ["detail"] = string.IsNullOrWhiteSpace(journalError) ? null : journalError
                }
            };
        }

        private void LoadJournal()
        {
            if (string.IsNullOrWhiteSpace(_journalPath) || !File.Exists(_journalPath)) return;
            try
            {
                var info = new FileInfo(_journalPath);
                if (info.Length <= 0 || info.Length > MaxJournalBytes)
                    throw new InvalidDataException("journal size is outside the accepted bounds");

                string text = File.ReadAllText(_journalPath);
                if (string.IsNullOrWhiteSpace(text))
                    throw new InvalidDataException("journal is empty");

                JToken root = JToken.Parse(text);
                JArray entries;
                if (root is JArray legacyEntries)
                {
                    // v0 was an array. Accept it once so upgrades do not discard
                    // existing safety fences; the next mutation writes v1.
                    entries = legacyEntries;
                }
                else if (root is JObject envelope
                    && string.Equals(envelope["schemaVersion"]?.ToString(), JournalSchemaVersion, StringComparison.Ordinal)
                    && envelope["entries"] is JArray versionedEntries)
                {
                    entries = versionedEntries;
                }
                else
                {
                    throw new InvalidDataException("journal schemaVersion is missing or unsupported");
                }

                if (entries.Count > MaxJournalEntries)
                    throw new InvalidDataException("journal contains too many recovery fences");

                foreach (var item in entries)
                {
                    if (!(item is JObject json))
                        throw new InvalidDataException("journal contains a non-object entry");
                    var requirement = json.ToObject<RecoveryRequirement>();
                    if (!IsValid(requirement))
                        throw new InvalidDataException("journal contains an invalid recovery fence");
                    if (_defaultOwner.HasValue
                        && !string.Equals(requirement!.OwnerKey, _defaultOwner.Value.Token, StringComparison.Ordinal))
                        throw new InvalidDataException("recovery fence belongs to another operational state scope");
                    if (DateTime.UtcNow - requirement!.RequiredAtUtc.ToUniversalTime() <= JournalRetention)
                    {
                        requirement.RequiredAtUtc = requirement.RequiredAtUtc.ToUniversalTime();
                        _pending[Key(requirement.OwnerKey, requirement.KbAlias, requirement.Target, requirement.Part)] = requirement;
                    }
                }

                if (entries.Count != _pending.Count)
                    PersistJournal();
            }
            catch (Exception ex)
            {
                _pending.Clear();
                MarkJournalUnhealthy("Mutation recovery journal rejected: " + ex.Message);
            }
        }

        private void PersistJournal()
        {
            if (string.IsNullOrWhiteSpace(_journalPath) || !_journalHealthy) return;
            lock (_journalLock)
            {
                string? temporary = null;
                try
                {
                    var entries = Pending.ToArray();
                    if (entries.Length > MaxJournalEntries)
                        throw new InvalidDataException("mutation recovery journal reached its entry limit");

                    string? directory = Path.GetDirectoryName(_journalPath);
                    if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                    temporary = _journalPath + ".tmp-" + Guid.NewGuid().ToString("N");
                    var document = new JObject
                    {
                        ["schemaVersion"] = JournalSchemaVersion,
                        ["entries"] = JArray.FromObject(entries)
                    };
                    byte[] bytes = Encoding.UTF8.GetBytes(document.ToString());
                    if (bytes.LongLength > MaxJournalBytes)
                        throw new InvalidDataException("mutation recovery journal exceeded its byte limit");

                    using (var stream = new FileStream(
                        temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        4096, FileOptions.WriteThrough))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }

                    if (File.Exists(_journalPath)) File.Replace(temporary, _journalPath, null);
                    else File.Move(temporary, _journalPath);
                    temporary = null;
                }
                catch (Exception ex)
                {
                    MarkJournalUnhealthy("Mutation recovery journal persistence failed: " + ex.Message);
                }
                finally
                {
                    if (temporary != null)
                    {
                        try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                    }
                }
            }
        }

        private void MarkJournalUnhealthy(string message)
        {
            _journalError = message ?? "Mutation recovery journal unavailable.";
            _journalHealthy = false;
        }

        private static bool IsValid(RecoveryRequirement? requirement)
            => requirement != null
                && !string.IsNullOrWhiteSpace(requirement.KbAlias)
                && !string.IsNullOrWhiteSpace(requirement.Target)
                && !string.IsNullOrWhiteSpace(requirement.Part)
                && requirement.RequiredAtUtc != default;

        private static string Prefix(string kbAlias, string target)
            => kbAlias.Trim().ToLowerInvariant() + "|" + target.Trim().ToLowerInvariant();

        private static string Key(string kbAlias, string target, string? part)
            => Key(string.Empty, kbAlias, target, part);

        private static string Key(string ownerKey, string kbAlias, string target, string? part)
            => (ownerKey ?? string.Empty).Trim().ToLowerInvariant() + "|" + Prefix(kbAlias, target) + "|" + (string.IsNullOrWhiteSpace(part) ? "source" : part.Trim().ToLowerInvariant());
    }

    internal sealed class RecoveryRequirement
    {
        public string KbAlias { get; set; } = string.Empty;
        public string OwnerKey { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string Part { get; set; } = string.Empty;
        public string OperationId { get; set; } = string.Empty;
        public DateTime RequiredAtUtc { get; set; }
    }
}
