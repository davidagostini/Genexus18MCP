using System;
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    internal sealed class MutationRecoveryRegistry
    {
        private readonly ConcurrentDictionary<string, RecoveryRequirement> _pending = new();

        public void RequireRead(string? kbAlias, string? target, string? part, string? operationId)
        {
            if (string.IsNullOrWhiteSpace(kbAlias) || string.IsNullOrWhiteSpace(target)) return;
            _pending[Key(kbAlias, target)] = new RecoveryRequirement
            {
                KbAlias = kbAlias,
                Target = target,
                Part = string.IsNullOrWhiteSpace(part) ? "Source" : part,
                OperationId = operationId ?? string.Empty,
                RequiredAtUtc = DateTime.UtcNow
            };
        }

        public bool TryGet(string? kbAlias, string? target, out RecoveryRequirement requirement)
        {
            requirement = null!;
            if (string.IsNullOrWhiteSpace(kbAlias) || string.IsNullOrWhiteSpace(target)) return false;
            if (!_pending.TryGetValue(Key(kbAlias, target), out var found)) return false;
            requirement = found;
            return true;
        }

        public bool ConfirmRead(string? kbAlias, string? target, string? part)
        {
            if (!TryGet(kbAlias, target, out var requirement)) return false;
            if (!string.IsNullOrWhiteSpace(part)
                && !string.Equals(requirement.Part, part, StringComparison.OrdinalIgnoreCase))
                return false;
            return _pending.TryRemove(Key(requirement.KbAlias, requirement.Target), out _);
        }

        public static JObject BuildBlockedEnvelope(RecoveryRequirement requirement)
        {
            return new JObject
            {
                ["status"] = "Blocked",
                ["code"] = "PostTimeoutReadRequired",
                ["target"] = requirement.Target,
                ["part"] = requirement.Part,
                ["operationId"] = requirement.OperationId,
                ["persisted"] = false,
                ["message"] = "A previous write timed out or was cancelled, so its persisted state is unknown. Re-read this part before another write.",
                ["hint"] = "Call genexus_read for the target and part. A successful full read clears this recovery fence; then retry from the returned versionToken."
            };
        }

        public int Count => _pending.Count;
        public System.Collections.Generic.IReadOnlyCollection<RecoveryRequirement> Pending => _pending.Values.ToList();

        private static string Key(string kbAlias, string target)
            => kbAlias.Trim().ToLowerInvariant() + "|" + target.Trim().ToLowerInvariant();
    }

    internal sealed class RecoveryRequirement
    {
        public string KbAlias { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string Part { get; set; } = string.Empty;
        public string OperationId { get; set; } = string.Empty;
        public DateTime RequiredAtUtc { get; set; }
    }
}
