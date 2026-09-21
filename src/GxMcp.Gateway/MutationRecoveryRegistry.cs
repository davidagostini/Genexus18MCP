using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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

        private volatile ConcurrentDictionary<string, RecoveryRequirement> _pending = new();
        private readonly Dictionary<string, RecoveryRequirement> _undurable = new();
        private bool _journalObserved;
        private volatile bool _journalBusy;
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

        public bool IsHealthy => _journalHealthy && !_journalBusy;
        public string JournalError => _journalHealthy && _journalBusy ? "Mutation recovery journal busy; retry after the other Gateway finishes." : _journalError;
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
            lock (_journalLock)
            {
                string key = Key(requirement.OwnerKey, requirement.KbAlias, requirement.Target, requirement.Part);
                _undurable[key] = requirement;
                try
                {
                    using var lease = AcquireJournalLock();
                    ReloadTrustedJournal();
                    _pending[key] = requirement;
                    if (_journalHealthy) PersistJournal();
                    else WriteCandidate(ValidateSize(new[] { requirement }));
                }
                catch (Exception ex)
                {
                    _pending[Key(requirement.OwnerKey, requirement.KbAlias, requirement.Target, requirement.Part)] = requirement;
                    MarkJournalUnhealthy("Mutation recovery journal persistence failed: " + ex.Message);
                    // Preserve newly observed uncertainty even if the existing
                    // journal cannot be trusted or the destination is locked.
                    try { WriteCandidate(ValidateSize(new[] { requirement })); } catch { }
                }
            }
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
            return ConfirmReadCore(Key(owner.Token, owner.KbId, target, part), null, useCurrent: true);
        }

        public bool ConfirmRead(string? kbAlias, string? target, string? part)
        {
            if (string.IsNullOrWhiteSpace(kbAlias) || string.IsNullOrWhiteSpace(target)) return false;
            return ConfirmReadCore(Key(_defaultOwner?.Token ?? string.Empty, kbAlias, target, part), null, useCurrent: true);
        }

        public bool ConfirmRead(string? kbAlias, string? target, string? part, RecoveryRequirement? observedRequirement)
        {
            if (string.IsNullOrWhiteSpace(kbAlias) || string.IsNullOrWhiteSpace(target)) return false;
            return ConfirmReadCore(Key(_defaultOwner?.Token ?? string.Empty, kbAlias, target, part), observedRequirement, useCurrent: false);
        }

        private bool ConfirmReadCore(string key, RecoveryRequirement? observed, bool useCurrent)
        {
            lock (_journalLock)
            {
                if (!_journalHealthy) return false;
                if (useCurrent) _pending.TryGetValue(key, out observed);
                if (observed == null || Key(observed.OwnerKey, observed.KbAlias, observed.Target, observed.Part) != key) return false;
                try
                {
                    using var lease = AcquireJournalLock();
                    ReloadTrustedJournal();
                    if (!_pending.TryGetValue(key, out var current)
                        || current.RequiredAtUtc != observed.RequiredAtUtc
                        || current.OperationId != observed.OperationId) return false;
                    _pending.TryRemove(key, out _);
                    _undurable.Remove(key);
                    try { PersistJournal(); }
                    catch
                    {
                        _pending[key] = current;
                        _undurable[key] = current;
                        try { WriteCandidate(ValidateSize(new[] { current })); } catch { }
                        throw;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    RecordFault("Mutation recovery journal persistence failed: ", ex);
                    return false;
                }
            }
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
            bool busy = journalError?.StartsWith("Mutation recovery journal busy;", StringComparison.Ordinal) == true;
            bool missing = journalError?.Contains("previously observed journal is missing", StringComparison.Ordinal) == true;
            return new JObject
            {
                ["status"] = "error",
                ["error"] = new JObject
                {
                    ["code"] = busy ? "MutationRecoveryJournalBusy" : "MutationRecoveryJournalUnavailable",
                    ["message"] = busy ? "Another Gateway holds the mutation recovery journal lock. Retry this call." : "The mutation recovery journal could not be trusted; writes are blocked until it is repaired.",
                    ["hint"] = busy ? "Retry after the other Gateway finishes. No repair is required solely for lock contention."
                        : missing ? "Stop writers and restore the missing journal from verified retained evidence before journal_repair. Do not restart to bypass this fence or replace it with an empty journal."
                        : "Call genexus_connection_recover with action=journal_status, then action=journal_repair and dryRun=true. Review the pending fences before retrying journal_repair with dryRun=false. Read-only calls remain available.",
                    ["retryable"] = busy,
                    ["reconciliationRequired"] = !busy,
                    ["detail"] = string.IsNullOrWhiteSpace(journalError) ? null : journalError
                }
            };
        }

        private void LoadJournal() => Refresh();

        // Read-only refresh is required at the write gate: several Gateways can
        // share this path. An unhealthy instance recovers only via explicit repair.
        public void Refresh()
        {
            lock (_journalLock)
            {
                try
                {
                    using var lease = AcquireJournalLock();
                    ReloadTrustedJournal();
                }
                catch (Exception ex) { RecordFault("Mutation recovery journal rejected: ", ex); }
            }
        }

        internal JObject GetJournalStatus()
        {
            Refresh();
            var result = new JObject
            {
                ["status"] = IsHealthy ? "ok" : "error",
                ["healthy"] = IsHealthy,
                ["pendingCount"] = Count,
                ["pending"] = ProjectPending(Pending),
                ["truncated"] = Count > 32
            };
            if (!IsHealthy) result["error"] = BuildJournalBlockedEnvelope(JournalError)["error"];
            return result;
        }

        internal JObject RepairJournal(bool dryRun = true)
        {
            lock (_journalLock)
            {
                try
                {
                    using var lease = AcquireJournalLock();
                    if (_journalObserved && !string.IsNullOrWhiteSpace(_journalPath) && !File.Exists(_journalPath))
                        throw new InvalidDataException("previously observed journal is missing");
                    var candidates = new Dictionary<string, RecoveryRequirement>();
                    foreach (var entry in _undurable.Values) Merge(candidates, entry);
                    if (!string.IsNullOrWhiteSpace(_journalPath) && File.Exists(_journalPath))
                        foreach (var entry in ReadJournal(_journalPath)) Merge(candidates, entry);
                    var temporaries = TemporaryFiles();
                    foreach (var temporary in temporaries)
                        foreach (var entry in ReadJournal(temporary)) Merge(candidates, entry);
                    ValidateSize(candidates.Values);
                    if (!dryRun)
                    {
                        if (!string.IsNullOrWhiteSpace(_journalPath) && File.Exists(_journalPath))
                        {
                            using var source = new FileStream(_journalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                            using var backup = new FileStream(_journalPath + ".backup-" + Guid.NewGuid().ToString("N"),
                                FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                            source.CopyTo(backup);
                            backup.Flush(true);
                        }
                        _pending = new ConcurrentDictionary<string, RecoveryRequirement>(candidates);
                        PersistJournal();
                        // Preserve recovery evidence, but remove it from the active
                        // candidate namespace only AFTER verified atomic publication.
                        foreach (var temporary in temporaries)
                            File.Move(temporary, temporary + ".reconciled");
                        _journalError = string.Empty;
                        _journalHealthy = true;
                    }
                    return new JObject
                    {
                        ["status"] = "ok", ["dryRun"] = dryRun,
                        ["healthy"] = IsHealthy, ["repairable"] = true,
                        ["repaired"] = !dryRun, ["persisted"] = !dryRun && !string.IsNullOrWhiteSpace(_journalPath),
                        ["verified"] = !dryRun, ["pendingCount"] = candidates.Count,
                        ["pending"] = ProjectPending(candidates.Values),
                        ["truncated"] = candidates.Count > 32,
                        ["recoveredTemporaryFiles"] = temporaries.Length
                    };
                }
                catch (Exception ex)
                {
                    RecordFault("Mutation recovery journal repair failed: ", ex);
                    var result = BuildJournalBlockedEnvelope(JournalError);
                    result["dryRun"] = dryRun;
                    result["healthy"] = false;
                    result["repaired"] = false;
                    result["persisted"] = false;
                    result["verified"] = false;
                    result["pendingCount"] = Count;
                    return result;
                }
            }
        }

        private FileStream? AcquireJournalLock()
        {
            if (string.IsNullOrWhiteSpace(_journalPath)) return null;
            var directory = Path.GetDirectoryName(Path.GetFullPath(_journalPath));
            Directory.CreateDirectory(directory!);
            var started = Environment.TickCount64;
            while (true)
            {
                try
                {
                    var lease = new FileStream(_journalPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    _journalBusy = false;
                    return lease;
                }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
                {
                    if (Environment.TickCount64 - started >= 2000) throw new JournalBusyException();
                    Thread.Sleep(20);
                }
            }
        }

        private string[] TemporaryFiles()
        {
            if (string.IsNullOrWhiteSpace(_journalPath)) return Array.Empty<string>();
            string full = Path.GetFullPath(_journalPath);
            return Directory.GetFiles(Path.GetDirectoryName(full)!, Path.GetFileName(full) + ".tmp-*")
                .Where(path => !path.EndsWith(".reconciled", StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        private void ReloadTrustedJournal()
        {
            if (string.IsNullOrWhiteSpace(_journalPath)) return;
            var merged = new Dictionary<string, RecoveryRequirement>();
            if (File.Exists(_journalPath))
            {
                foreach (var entry in ReadJournal(_journalPath)) Merge(merged, entry);
                _journalObserved = true;
            }
            else if (_journalObserved) throw new InvalidDataException("previously observed journal is missing");
            foreach (var entry in _undurable.Values) Merge(merged, entry);
            _pending = new ConcurrentDictionary<string, RecoveryRequirement>(merged);
            if (TemporaryFiles().Length != 0)
                throw new InvalidDataException("uncommitted journal candidates require explicit journal_repair");
        }

        private IReadOnlyList<RecoveryRequirement> ReadJournal(string path)
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxJournalBytes)
                throw new InvalidDataException("journal size is outside the accepted bounds");
            JToken root = JToken.Parse(File.ReadAllText(path));
            JArray entries = root is JArray legacy ? legacy
                : root is JObject envelope
                    && string.Equals(envelope["schemaVersion"]?.ToString(), JournalSchemaVersion, StringComparison.Ordinal)
                    && envelope["entries"] is JArray versioned ? versioned
                : throw new InvalidDataException("journal schemaVersion is missing or unsupported");
            if (entries.Count > MaxJournalEntries) throw new InvalidDataException("journal contains too many recovery fences");
            var result = new List<RecoveryRequirement>();
            foreach (var item in entries)
            {
                var requirement = item is JObject json ? json.ToObject<RecoveryRequirement>() : null;
                if (!IsValid(requirement)) throw new InvalidDataException("journal contains an invalid recovery fence");
                if (_defaultOwner.HasValue && !string.Equals(requirement!.OwnerKey, _defaultOwner.Value.Token, StringComparison.Ordinal))
                    throw new InvalidDataException("recovery fence belongs to another operational state scope");
                requirement!.RequiredAtUtc = requirement.RequiredAtUtc.ToUniversalTime();
                result.Add(requirement);
            }
            return result;
        }

        private static void Merge(IDictionary<string, RecoveryRequirement> entries, RecoveryRequirement entry)
        {
            string key = Key(entry.OwnerKey, entry.KbAlias, entry.Target, entry.Part);
            if (!entries.TryGetValue(key, out var previous) || entry.RequiredAtUtc >= previous.RequiredAtUtc)
                entries[key] = entry;
        }

        private static byte[] ValidateSize(IEnumerable<RecoveryRequirement> entries)
        {
            var array = entries.ToArray();
            if (array.Length > MaxJournalEntries) throw new InvalidDataException("mutation recovery journal reached its entry limit");
            byte[] bytes = Encoding.UTF8.GetBytes(new JObject
            {
                ["schemaVersion"] = JournalSchemaVersion, ["entries"] = JArray.FromObject(array)
            }.ToString());
            if (bytes.LongLength > MaxJournalBytes) throw new InvalidDataException("mutation recovery journal exceeded its byte limit");
            return bytes;
        }

        // Caller owns both locks. Never delete the destination to work around a
        // sharing/ACL error: rename on the same volume is the atomic commit point.
        private void PersistJournal()
        {
            if (string.IsNullOrWhiteSpace(_journalPath)) return;
            var entries = Pending.ToArray();
            byte[] bytes = ValidateSize(entries);
            string temporary = WriteCandidate(bytes);
            // File.Replace can fail on Windows with "Unable to remove the file
            // to be replaced" even with a writable destination. Overwrite rename
            // avoids its metadata-merging semantics and retains atomicity.
            File.Move(temporary, _journalPath, overwrite: true);
            var verified = ReadJournal(_journalPath);
            if (!JToken.DeepEquals(JArray.FromObject(entries), JArray.FromObject(verified)))
                throw new InvalidDataException("journal readback did not match the committed recovery fences");
            _undurable.Clear();
            _journalObserved = true;
        }

        private string WriteCandidate(byte[] bytes)
        {
            string temporary = _journalPath + ".tmp-" + Guid.NewGuid().ToString("N");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            return temporary;
        }

        private static JArray ProjectPending(IEnumerable<RecoveryRequirement> pending) => new JArray(pending
            .OrderBy(item => item.RequiredAtUtc).ThenBy(item => item.Target, StringComparer.OrdinalIgnoreCase)
            .Take(32).Select(item => new JObject
            {
                ["kbAlias"] = item.KbAlias, ["target"] = item.Target, ["part"] = item.Part,
                ["operationId"] = item.OperationId, ["requiredAtUtc"] = item.RequiredAtUtc
            }));

        private sealed class JournalBusyException : IOException { }

        private void RecordFault(string prefix, Exception exception)
        {
            if (exception is JournalBusyException) _journalBusy = true;
            else MarkJournalUnhealthy(prefix + exception.Message);
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
