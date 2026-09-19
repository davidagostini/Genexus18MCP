using System;
using System.Collections.Concurrent;
using System.Threading;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Bounded MTA dispatch for commands that do not touch the SDK. Admission is
    /// bounded independently from execution so a burst cannot create an unbounded
    /// number of ThreadPool tasks. Health and cancellation work is prioritized.
    /// </summary>
    internal sealed class MtaCommandExecutor : IDisposable
    {
        private readonly ConcurrentQueue<WorkItem> _priority = new ConcurrentQueue<WorkItem>();
        private readonly ConcurrentQueue<WorkItem> _normal = new ConcurrentQueue<WorkItem>();
        private readonly SemaphoreSlim _queueSlots;
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly Thread[] _workers;
        private readonly object _admissionGate = new object();
        private int _stopping;
        private int _active;

        public MtaCommandExecutor(int maxConcurrency, int queueCapacity = 256)
        {
            if (maxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
            if (queueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
            Capacity = maxConcurrency;
            _queueSlots = new SemaphoreSlim(queueCapacity, queueCapacity);
            _workers = new Thread[maxConcurrency];
            for (int i = 0; i < _workers.Length; i++)
            {
                _workers[i] = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "MtaCommandWorker-" + (i + 1),
                    Priority = ThreadPriority.Normal
                };
                _workers[i].SetApartmentState(ApartmentState.MTA);
                _workers[i].Start();
            }
        }

        public int Capacity { get; }
        public int ActiveCount => Volatile.Read(ref _active);

        public bool TrySubmit(Action work, bool highPriority = false,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (work == null) return false;
            lock (_admissionGate)
            {
                if (Volatile.Read(ref _stopping) != 0 || !_queueSlots.Wait(0)) return false;
                var item = new WorkItem(work, cancellationToken);
                if (highPriority) _priority.Enqueue(item);
                else _normal.Enqueue(item);
                _signal.Set();
                return true;
            }
        }

        private void WorkerLoop()
        {
            while (true)
            {
                if (!TryTake(out var item))
                {
                    if (Volatile.Read(ref _stopping) != 0 && QueuesEmpty()) return;
                    _signal.WaitOne(100);
                    continue;
                }

                _queueSlots.Release();
                if (item.CancellationToken.IsCancellationRequested) continue;

                Interlocked.Increment(ref _active);
                try { item.Work(); }
                catch (Exception ex) { Logger.Error("MTA Command Error: " + ex.Message); }
                finally { Interlocked.Decrement(ref _active); }
            }
        }

        private bool TryTake(out WorkItem item)
        {
            if (_priority.TryDequeue(out item)) return true;
            return _normal.TryDequeue(out item);
        }

        private bool QueuesEmpty() => _priority.IsEmpty && _normal.IsEmpty;

        public void Dispose()
        {
            lock (_admissionGate)
            {
                if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
            }
            _signal.Set();
            foreach (var worker in _workers)
            {
                if (worker != Thread.CurrentThread) worker.Join();
            }
            _queueSlots.Dispose();
            _signal.Dispose();
        }

        private sealed class WorkItem
        {
            public WorkItem(Action work, CancellationToken cancellationToken)
            {
                Work = work;
                CancellationToken = cancellationToken;
            }

            public Action Work { get; }
            public CancellationToken CancellationToken { get; }
        }
    }
}
