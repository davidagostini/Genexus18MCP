using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    public interface IWorkerSupervisor
    {
        Task<WorkerProcess> AcquireAsync(KbHandle handle, CancellationToken ct);
        WorkerProcess? TryGet(string alias);
        bool IsSpawning(string alias);
        bool IsAtCapacity();
        IReadOnlyList<KbHandle> ListOpen();
        IReadOnlyList<KbHandle> ListKnown();
        IReadOnlyList<KbPoolStatus> Snapshot();
        Task<WorkerProcess> DrainAndReplaceAsync(KbHandle handle, int drainTimeoutMs, CancellationToken ct, Func<WorkerProcess?, Task>? afterDrainBeforeSpawn = null);
        bool RecycleStalledWorker(string alias);
        void StopAll(WorkerStopReason reason = WorkerStopReason.GatewayShutdown);
        void DropLiveEntry(string alias);
        void RegisterKnown(KbHandle handle);
        event Action<string, JObject>? OnRpcResponse;
        event Action<KbHandle, WorkerStopReason>? OnWorkerExited;
    }

    /// <summary>
    /// Deep Worker Supervisor for Gateway process orchestration.
    /// Encapsulates per-KB concurrency gates, LRU capacity eviction, draining,
    /// crash-recovery backoff coordination, and durable KB resolution across worker recycles.
    /// </summary>
    public sealed class WorkerSupervisor : IWorkerSupervisor
    {
        private readonly Configuration _config;
        private readonly ConcurrentDictionary<string, Entry> _entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, KbHandle> _known =
            new ConcurrentDictionary<string, KbHandle>(StringComparer.OrdinalIgnoreCase);

        private readonly object _capacityLock = new object();

        public event Action<string, JObject>? OnRpcResponse;
        public event Action<KbHandle, WorkerStopReason>? OnWorkerExited;

        public Func<KbHandle, WorkerProcess>? SpawnFactory { get; set; }

        public WorkerSupervisor(Configuration config)
        {
            _config = config;
        }

        private sealed class Entry
        {
            public KbHandle Handle = null!;
            public WorkerProcess? Worker;
            public DateTime LastActivityUtc = DateTime.UtcNow;
            public readonly SemaphoreSlim SpawnGate = new SemaphoreSlim(1, 1);
            public volatile bool Draining;
            public volatile bool Spawning;
            public TaskCompletionSource<bool> DrainComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public IReadOnlyList<KbHandle> ListOpen() =>
            _entries.Values
                .Where(e => e.Worker != null)
                .Select(e => e.Handle)
                .ToArray();

        public IReadOnlyList<KbHandle> ListKnown() => _known.Values.ToArray();

        public void RegisterKnown(KbHandle handle)
        {
            if (handle != null)
            {
                _known[handle.NormalizedAlias] = handle;
            }
        }

        public bool IsSpawning(string alias)
        {
            if (alias == null) return false;
            return _entries.TryGetValue(alias, out var e) && (e.Spawning || e.Draining);
        }

        public bool IsAtCapacity()
        {
            int max = _config.Server?.MaxOpenKbs ?? 3;
            return _entries.Count >= max;
        }

        public WorkerProcess? TryGet(string alias)
        {
            if (alias == null) return null;
            if (_entries.TryGetValue(alias.ToLowerInvariant(), out var entry))
            {
                return entry.Worker;
            }
            return null;
        }

        public IReadOnlyList<KbPoolStatus> Snapshot()
        {
            return _entries.Values
                .Where(e => e.Worker != null)
                .Select(e => new KbPoolStatus(
                    e.Handle,
                    e.Worker!.Pid,
                    e.Worker!.WorkingSetBytes,
                    e.LastActivityUtc))
                .ToArray();
        }

        public async Task<WorkerProcess> AcquireAsync(KbHandle handle, CancellationToken ct)
        {
            var entry = _entries.GetOrAdd(handle.NormalizedAlias, _ => new Entry { Handle = handle });
            _known[handle.NormalizedAlias] = handle;

            if (entry.Draining)
            {
                await entry.DrainComplete.Task.ConfigureAwait(false);
                if (_entries.TryGetValue(handle.NormalizedAlias, out var fresh) && fresh != entry)
                {
                    entry = fresh;
                }
            }

            if (entry.Worker != null)
            {
                entry.LastActivityUtc = DateTime.UtcNow;
                return entry.Worker;
            }

            await entry.SpawnGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (entry.Worker != null)
                {
                    entry.LastActivityUtc = DateTime.UtcNow;
                    return entry.Worker;
                }

                lock (_capacityLock)
                {
                    int max = _config.Server?.MaxOpenKbs ?? 3;
                    if (_entries.Count > max || (_entries.Count == max && !_entries.ContainsKey(handle.NormalizedAlias)))
                    {
                        var victim = SelectVictim();
                        if (victim != null && !victim.Handle.NormalizedAlias.Equals(handle.NormalizedAlias, StringComparison.OrdinalIgnoreCase))
                        {
                            EvictEntry(victim);
                        }
                        else
                        {
                            throw new WorkerPoolFullException(ListOpen());
                        }
                    }
                }

                WorkerProcess worker = SpawnFactory != null
                    ? SpawnFactory(handle)
                    : new WorkerProcess(_config, handle);

                worker.OnRpcResponse += (json, parsed) => OnRpcResponse?.Invoke(json, parsed);
                var capturedHandle = handle;
                worker.OnWorkerExited += (reason) =>
                {
                    OnWorkerExited?.Invoke(capturedHandle, reason);
                    if (_entries.TryGetValue(capturedHandle.NormalizedAlias, out var currentEntry) && currentEntry.Draining)
                        return;
                    _entries.TryRemove(capturedHandle.NormalizedAlias, out _);
                };

                if (SpawnFactory == null)
                {
                    entry.Spawning = true;
                    try
                    {
                        worker.Start();
                    }
                    finally
                    {
                        entry.Spawning = false;
                    }
                }

                entry.Worker = worker;
                entry.LastActivityUtc = DateTime.UtcNow;
                return worker;
            }
            finally
            {
                entry.SpawnGate.Release();
            }
        }

        public async Task<WorkerProcess> DrainAndReplaceAsync(KbHandle handle, int drainTimeoutMs, CancellationToken ct,
            Func<WorkerProcess?, Task>? afterDrainBeforeSpawn = null)
        {
            var entry = _entries.GetOrAdd(handle.NormalizedAlias, _ => new Entry { Handle = handle });
            entry.Draining = true;
            try
            {
                var oldWorker = entry.Worker;
                if (oldWorker != null)
                {
                    oldWorker.StopWithReason(WorkerStopReason.PlannedReload);
                    try
                    {
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(drainTimeoutMs);
                        await oldWorker.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                }

                if (afterDrainBeforeSpawn != null)
                {
                    await afterDrainBeforeSpawn(oldWorker).ConfigureAwait(false);
                }

                entry.Worker = null;
                entry.Spawning = true;
                WorkerProcess replacement;
                try
                {
                    replacement = SpawnFactory != null ? SpawnFactory(handle) : new WorkerProcess(_config, handle);
                    replacement.OnRpcResponse += (json, parsed) => OnRpcResponse?.Invoke(json, parsed);
                    var capturedHandle = handle;
                    replacement.OnWorkerExited += (reason) =>
                    {
                        OnWorkerExited?.Invoke(capturedHandle, reason);
                        if (_entries.TryGetValue(capturedHandle.NormalizedAlias, out var currentEntry) && currentEntry.Draining)
                            return;
                        _entries.TryRemove(capturedHandle.NormalizedAlias, out _);
                    };

                    if (SpawnFactory == null)
                    {
                        replacement.Start();
                    }
                }
                finally
                {
                    entry.Spawning = false;
                }

                entry.Worker = replacement;
                entry.LastActivityUtc = DateTime.UtcNow;
                return replacement;
            }
            finally
            {
                entry.Draining = false;
                entry.DrainComplete.TrySetResult(true);
            }
        }

        public bool RecycleStalledWorker(string alias)
        {
            if (alias == null) return false;
            if (!_entries.TryRemove(alias.ToLowerInvariant(), out var entry)) return false;

            entry.Worker?.StopWithReason(WorkerStopReason.Wedged);
            return true;
        }

        public void StopAll(WorkerStopReason reason = WorkerStopReason.GatewayShutdown)
        {
            foreach (var e in _entries.Values)
            {
                try { e.Worker?.StopWithReason(reason); } catch { }
            }
            _entries.Clear();
        }

        public void DropLiveEntry(string alias)
        {
            if (alias != null)
            {
                _entries.TryRemove(alias.ToLowerInvariant(), out _);
            }
        }

        private Entry? SelectVictim()
        {
            Entry? victim = null;
            DateTime oldest = DateTime.MaxValue;
            foreach (var e in _entries.Values)
            {
                if (e.Worker == null) continue;
                if (e.LastActivityUtc < oldest)
                {
                    oldest = e.LastActivityUtc;
                    victim = e;
                }
            }
            return victim;
        }

        private void EvictEntry(Entry entry)
        {
            try { entry.Worker?.StopWithReason(WorkerStopReason.ExplicitClose); } catch { }
            _entries.TryRemove(entry.Handle.NormalizedAlias, out _);
        }
    }
}
