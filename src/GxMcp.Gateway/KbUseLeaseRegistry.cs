using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace GxMcp.Gateway
{
    public enum KbUseLeaseState
    {
        Active,
        Expired,
        Revoked,
        Released
    }

    public enum KbUseLeaseOperationStatus
    {
        Success,
        InvalidToken,
        WrongOwner,
        Expired,
        Revoked,
        Released,
        InFlight
    }

    public sealed class KbLeaseValidationException : Exception
    {
        public string Code { get; }
        public KbLeaseValidationException(string code, string message) : base(message) { Code = code; }
    }

    /// <summary>A process-local clock whose value never moves backwards.</summary>
    public interface IMonotonicClock
    {
        TimeSpan Now { get; }
    }

    public sealed class StopwatchMonotonicClock : IMonotonicClock
    {
        private readonly long _startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        public TimeSpan Now => TimeSpan.FromSeconds(
            (double)(System.Diagnostics.Stopwatch.GetTimestamp() - _startedAt) /
            System.Diagnostics.Stopwatch.Frequency);
    }

    public sealed class KbUseLease
    {
        internal KbUseLease(string token, string ownerScopeId, string kbId, long contextGeneration,
            string identity, string clientRequestId, KbUseLeaseState state, TimeSpan expiresAt)
        {
            Token = token;
            OwnerScopeId = ownerScopeId;
            KbId = kbId;
            ContextGeneration = contextGeneration;
            Identity = identity;
            ClientRequestId = clientRequestId;
            State = state;
            ExpiresAt = expiresAt;
        }

        public string Token { get; }
        public string OwnerScopeId { get; }
        public string KbId { get; }
        public long ContextGeneration { get; }
        public string Identity { get; }
        public string ClientRequestId { get; }
        public KbUseLeaseState State { get; }
        public TimeSpan ExpiresAt { get; }
    }

    public sealed class KbUseLeaseOperationResult
    {
        internal KbUseLeaseOperationResult(KbUseLeaseOperationStatus status, KbUseLease? lease)
        {
            Status = status;
            Lease = lease;
        }

        public KbUseLeaseOperationStatus Status { get; }
        public KbUseLease? Lease { get; }
        public KbUseLeaseState? State => Lease?.State;
        public TimeSpan? ExpiresAt => Lease?.ExpiresAt;
    }

    /// <summary>
    /// Owner-bound, in-memory leases for a KB context. Tokens are bearer
    /// capabilities only after the caller's owner scope has been checked.
    /// </summary>
    public sealed class KbUseLeaseRegistry
    {
        private readonly object _gate = new object();
        private readonly IMonotonicClock _clock;
        private readonly Dictionary<string, Entry> _byToken = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<OpenKey, Entry> _byOpenKey = new Dictionary<OpenKey, Entry>();

        public KbUseLeaseRegistry(IMonotonicClock clock)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public KbUseLease Open(string ownerScopeId, string kbId, long contextGeneration,
            string identity, string clientRequestId, TimeSpan ttl)
        {
            Require(ownerScopeId, nameof(ownerScopeId));
            Require(kbId, nameof(kbId));
            Require(identity, nameof(identity));
            Require(clientRequestId, nameof(clientRequestId));
            RequireTtl(ttl);

            lock (_gate)
            {
                var key = new OpenKey(ownerScopeId, identity, clientRequestId);
                if (_byOpenKey.TryGetValue(key, out var existing))
                {
                    RefreshState(existing);
                    if (existing.State == KbUseLeaseState.Active)
                        return Snapshot(existing);
                    _byOpenKey.Remove(key);
                }

                var entry = new Entry
                {
                    Token = CreateToken(),
                    OwnerScopeId = ownerScopeId,
                    KbId = kbId,
                    ContextGeneration = contextGeneration,
                    Identity = identity,
                    ClientRequestId = clientRequestId,
                    State = KbUseLeaseState.Active,
                    ExpiresAt = Add(_clock.Now, ttl)
                };
                _byToken.Add(entry.Token, entry);
                _byOpenKey.Add(key, entry);
                return Snapshot(entry);
            }
        }

        public KbUseLeaseOperationResult Renew(string token, string ownerScopeId, TimeSpan ttl, string? clientRequestId = null)
        {
            Require(ownerScopeId, nameof(ownerScopeId));
            RequireTtl(ttl);
            lock (_gate)
            {
                var check = FindOwned(token, ownerScopeId);
                if (check.Status != KbUseLeaseOperationStatus.Success)
                    return new KbUseLeaseOperationResult(check.Status, null);
                var entry = check.LeaseEntry!;
                RefreshState(entry);
                if (entry.State != KbUseLeaseState.Active)
                    return ResultForState(entry);
                if (clientRequestId != null)
                {
                    if (entry.Renewals.TryGetValue(clientRequestId, out var prior))
                        return new KbUseLeaseOperationResult(KbUseLeaseOperationStatus.Success, Snapshot(prior));
                    entry.Renewals.Add(clientRequestId, Clone(entry));
                }
                entry.ExpiresAt = Add(_clock.Now, ttl);
                if (clientRequestId != null)
                    entry.Renewals[clientRequestId] = Clone(entry);
                return new KbUseLeaseOperationResult(KbUseLeaseOperationStatus.Success, Snapshot(entry));
            }
        }

        public KbUseLeaseOperationResult Close(string token, string ownerScopeId, string? clientRequestId = null)
        {
            Require(ownerScopeId, nameof(ownerScopeId));
            lock (_gate)
            {
                var check = FindOwned(token, ownerScopeId);
                if (check.Status != KbUseLeaseOperationStatus.Success)
                    return new KbUseLeaseOperationResult(check.Status, null);
                var entry = check.LeaseEntry!;
                RefreshState(entry);
                if (clientRequestId != null && entry.Closes.TryGetValue(clientRequestId, out var prior))
                    return new KbUseLeaseOperationResult(prior.Status, Snapshot(entry));
                if (entry.State == KbUseLeaseState.Active && entry.InFlight != 0)
                    return new KbUseLeaseOperationResult(KbUseLeaseOperationStatus.InFlight, Snapshot(entry));
                bool released = entry.State == KbUseLeaseState.Active;
                if (released)
                    entry.State = KbUseLeaseState.Released;
                var result = released
                    ? new KbUseLeaseOperationResult(KbUseLeaseOperationStatus.Success, Snapshot(entry))
                    : ResultForState(entry);
                if (clientRequestId != null)
                    entry.Closes[clientRequestId] = result;
                return result;
            }
        }

        public KbUseLeaseOperationResult Revoke(string token, string ownerScopeId)
        {
            Require(ownerScopeId, nameof(ownerScopeId));
            lock (_gate)
            {
                var check = FindOwned(token, ownerScopeId);
                if (check.Status != KbUseLeaseOperationStatus.Success)
                    return new KbUseLeaseOperationResult(check.Status, null);
                var entry = check.LeaseEntry!;
                RefreshState(entry);
                bool revoked = entry.State == KbUseLeaseState.Active;
                if (revoked)
                    entry.State = KbUseLeaseState.Revoked;
                return revoked
                    ? new KbUseLeaseOperationResult(KbUseLeaseOperationStatus.Success, Snapshot(entry))
                    : ResultForState(entry);
            }
        }

        public KbUseLease? Get(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            lock (_gate)
            {
                if (!_byToken.TryGetValue(token, out var entry)) return null;
                RefreshState(entry);
                return Snapshot(entry);
            }
        }

        public void Validate(string token, string ownerScopeId, string kbId, long contextGeneration, string identity)
        {
            Require(ownerScopeId, nameof(ownerScopeId));
            Require(kbId, nameof(kbId));
            Require(identity, nameof(identity));
            lock (_gate)
            {
                var check = FindOwned(token, ownerScopeId);
                if (check.Status == KbUseLeaseOperationStatus.WrongOwner)
                    throw new KbLeaseValidationException("KB_NOT_OWNED", "The KB lease belongs to another session owner.");
                if (check.Status != KbUseLeaseOperationStatus.Success)
                    throw new KbLeaseValidationException("KB_LEASE_INVALID", "The KB lease token is invalid.");
                var entry = check.LeaseEntry!;
                RefreshState(entry);
                if (entry.State == KbUseLeaseState.Expired)
                    throw new KbLeaseValidationException("KB_LEASE_EXPIRED", "The KB lease has expired.");
                if (entry.State != KbUseLeaseState.Active
                    || !string.Equals(entry.KbId, kbId, StringComparison.Ordinal)
                    || entry.ContextGeneration != contextGeneration
                    || !string.Equals(entry.Identity, identity, StringComparison.Ordinal))
                    throw new KbLeaseValidationException("KB_LEASE_INVALID", "The KB lease does not match the requested KB context snapshot.");
            }
        }

        public bool TryEnterInFlight(string token, string ownerScopeId)
        {
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(ownerScopeId)) return false;
            lock (_gate)
            {
                var check = FindOwned(token, ownerScopeId);
                if (check.Status != KbUseLeaseOperationStatus.Success) return false;
                var entry = check.LeaseEntry!;
                RefreshState(entry);
                if (entry.State != KbUseLeaseState.Active) return false;
                entry.InFlight++;
                return true;
            }
        }

        public bool ExitInFlight(string token, string ownerScopeId)
        {
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(ownerScopeId)) return false;
            lock (_gate)
            {
                if (!_byToken.TryGetValue(token, out var entry) || entry.OwnerScopeId != ownerScopeId || entry.InFlight == 0)
                    return false;
                entry.InFlight--;
                return true;
            }
        }

        private OperationCheck FindOwned(string token, string ownerScopeId)
        {
            if (string.IsNullOrEmpty(token) || !_byToken.TryGetValue(token, out var entry))
                return OperationCheck.Invalid;
            return entry.OwnerScopeId == ownerScopeId ? new OperationCheck(entry) : OperationCheck.WrongOwner;
        }

        private void RefreshState(Entry entry)
        {
            if (entry.State == KbUseLeaseState.Active && _clock.Now >= entry.ExpiresAt)
            {
                entry.State = KbUseLeaseState.Expired;
                _byOpenKey.Remove(new OpenKey(entry.OwnerScopeId, entry.Identity, entry.ClientRequestId));
            }
        }

        private static KbUseLeaseOperationResult ResultForState(Entry entry)
        {
            var status = entry.State == KbUseLeaseState.Active ? KbUseLeaseOperationStatus.Success :
                entry.State == KbUseLeaseState.Expired ? KbUseLeaseOperationStatus.Expired :
                entry.State == KbUseLeaseState.Revoked ? KbUseLeaseOperationStatus.Revoked : KbUseLeaseOperationStatus.Released;
            return new KbUseLeaseOperationResult(status, Snapshot(entry));
        }

        private static KbUseLease Snapshot(Entry e) => new KbUseLease(e.Token, e.OwnerScopeId, e.KbId, e.ContextGeneration, e.Identity, e.ClientRequestId, e.State, e.ExpiresAt);
        private static Entry Clone(Entry e) => new Entry { Token = e.Token, OwnerScopeId = e.OwnerScopeId, KbId = e.KbId, ContextGeneration = e.ContextGeneration, Identity = e.Identity, ClientRequestId = e.ClientRequestId, State = e.State, ExpiresAt = e.ExpiresAt };
        private static TimeSpan Add(TimeSpan now, TimeSpan ttl) => now + ttl;
        private static void Require(string value, string name) { if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A non-empty value is required.", name); }
        private static void RequireTtl(TimeSpan ttl) { if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromDays(7)) throw new ArgumentOutOfRangeException(nameof(ttl)); }
        private static string CreateToken() { var bytes = RandomNumberGenerator.GetBytes(32); return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('='); }

        private sealed class Entry
        {
            public string Token = string.Empty, OwnerScopeId = string.Empty, KbId = string.Empty, Identity = string.Empty, ClientRequestId = string.Empty;
            public long ContextGeneration;
            public KbUseLeaseState State;
            public TimeSpan ExpiresAt;
            public int InFlight;
            public Dictionary<string, Entry> Renewals { get; } = new Dictionary<string, Entry>(StringComparer.Ordinal);
            public Dictionary<string, KbUseLeaseOperationResult> Closes { get; } = new Dictionary<string, KbUseLeaseOperationResult>(StringComparer.Ordinal);
        }

        private readonly struct OpenKey : IEquatable<OpenKey>
        {
            private readonly string Owner, Identity, Request;
            public OpenKey(string owner, string identity, string request) { Owner = owner; Identity = identity; Request = request; }
            public bool Equals(OpenKey other) => Owner == other.Owner && Identity == other.Identity && Request == other.Request;
            public override bool Equals(object? obj) => obj is OpenKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Owner, Identity, Request);
        }

        private readonly struct OperationCheck
        {
            public readonly Entry? LeaseEntry;
            public readonly KbUseLeaseOperationStatus Status;
            public OperationCheck(Entry entry) { LeaseEntry = entry; Status = KbUseLeaseOperationStatus.Success; }
            private OperationCheck(KbUseLeaseOperationStatus status) { LeaseEntry = null; Status = status; }
            public static OperationCheck Invalid => new OperationCheck(KbUseLeaseOperationStatus.InvalidToken);
            public static OperationCheck WrongOwner => new OperationCheck(KbUseLeaseOperationStatus.WrongOwner);
        }
    }
}
