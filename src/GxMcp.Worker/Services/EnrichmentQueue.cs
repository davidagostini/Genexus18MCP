using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// FIFO enrichment queue. Synchronous by design: the previous implementation used
    /// <c>await SemaphoreSlim.WaitAsync(...).ConfigureAwait(false)</c>, so contended
    /// continuations resumed on MTA threadpool threads and then called straight into
    /// the (thread-unsafe) GeneXus SDK. Nothing here needs async — a plain lock keeps
    /// the caller's thread (STA drain thread or gated dispatcher thread) for the whole
    /// SDK call. The methods keep their Task-returning signatures for source
    /// compatibility with existing callers (AnalyzeService awaits PromoteAsync).
    /// </summary>
    public class EnrichmentQueue
    {
        private readonly IndexEntryEnricher _enricher;
        private readonly ConcurrentQueue<SearchIndex.IndexEntry> _queue = new ConcurrentQueue<SearchIndex.IndexEntry>();
        private readonly ConcurrentDictionary<SearchIndex.IndexEntry, byte> _queued = new ConcurrentDictionary<SearchIndex.IndexEntry, byte>();
        private readonly object _enrichGate = new object();
        private int _pendingCount;

        public EnrichmentQueue(IndexEntryEnricher enricher)
        {
            _enricher = enricher;
        }

        public int PendingCount { get { return Volatile.Read(ref _pendingCount); } }

        public void Enqueue(SearchIndex.IndexEntry entry)
        {
            if (entry == null || entry.IsEnriched) return;
            // PERFORMANCE (perf-review): dedup in-flight entries. During a lite pass +
            // concurrent watcher saves the same un-enriched entry could be enqueued many
            // times, and each duplicate re-ran the full SDK enrichment (GetReferences +
            // textual scan). The _queued marker is removed on dequeue so failed
            // enrichment stays re-enqueueable — same semantics, minus duplicate work.
            if (!_queued.TryAdd(entry, 1)) return;
            _queue.Enqueue(entry);
            Interlocked.Increment(ref _pendingCount);
        }

        // issue #25 follow-up (P3): optional per-item progress callback (processed, total)
        // so the caller can surface Enriching-phase progress instead of a blind block.
        public Task DrainAsync(CancellationToken cancellationToken = default(CancellationToken),
            System.Action<int, int> onProgress = null)
        {
            int total = Volatile.Read(ref _pendingCount);
            int processed = 0;
            SearchIndex.IndexEntry entry;
            while (_queue.TryDequeue(out entry))
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_enrichGate)
                {
                    _enricher.Enrich(entry);
                    Interlocked.Decrement(ref _pendingCount);
                }
                // Free the dedup marker so a later failed-enrichment requeue works.
                _queued.TryRemove(entry, out _);
                processed++;
                if (onProgress != null)
                {
                    try { onProgress(processed, total); } catch { }
                }
            }
            return CompletedTask;
        }

        public Task PromoteAsync(SearchIndex.IndexEntry entry, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (entry == null || entry.IsEnriched) return CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            lock (_enrichGate)
            {
                _enricher.Enrich(entry);
            }
            return CompletedTask;
        }

        private static readonly Task CompletedTask = Task.FromResult(0);
    }
}
