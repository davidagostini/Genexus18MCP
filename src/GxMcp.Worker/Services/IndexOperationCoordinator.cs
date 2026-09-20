using System;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Serializes index recovery requests and gives every active run a stable
    /// identity. The callback is invoked only for an explicit recovery of a
    /// stalled or exited worker; observing a stall never cancels the worker.
    /// </summary>
    internal sealed class IndexOperationCoordinator
    {
        private sealed class Operation
        {
            public string Id;
            public int Generation;
            public bool Active;
            public bool Stalled;
            public bool WorkerStarted;
            public bool RetryPending;
            public bool RecoveryPending;
            public DateTime StartedAtUtc;
            public DateTime? StalledAtUtc;
        }

        internal sealed class Lease
        {
            internal Lease(string operationId, int generation, bool reused, bool recoveryPending = false)
            {
                OperationId = operationId;
                Generation = generation;
                Reused = reused;
                RecoveryPending = recoveryPending;
            }

            internal string OperationId { get; }
            internal int Generation { get; }
            internal bool Reused { get; }
            internal bool RecoveryPending { get; }
        }

        internal sealed class Snapshot
        {
            internal string OperationId { get; set; }
            internal int Generation { get; set; }
            internal bool Active { get; set; }
            internal bool Stalled { get; set; }
            internal bool WorkerAlive { get; set; }
            internal bool WorkerStarted { get; set; }
            internal bool RetryPending { get; set; }
            internal bool RecoveryPending { get; set; }
            internal bool Recoverable { get; set; }
            internal string State { get; set; }
            internal DateTime? StartedAtUtc { get; set; }
            internal DateTime? StalledAtUtc { get; set; }
        }

        private readonly object _sync = new object();
        private Operation _current;
        private string _lastOperationId;
        private int _nextGeneration;
        private bool _recoveryInProgress;

        internal Lease Acquire(bool force, bool workerAlive, Func<bool> recoverStalled)
        {
            Operation recoveryOperation = null;
            lock (_sync)
            {
                while (_recoveryInProgress)
                    System.Threading.Monitor.Wait(_sync);

                if (_current != null && _current.Active)
                {
                    bool needsRecovery = _current.RecoveryPending
                        ? force
                        : force
                            && _current.WorkerStarted
                            && !_current.RetryPending
                            && (_current.Stalled || !workerAlive);
                    if (!needsRecovery)
                    {
                        return new Lease(
                            _current.Id,
                            _current.Generation,
                            reused: true,
                            recoveryPending: _current.RecoveryPending);
                    }

                    // Fence the old generation before asking the owner to stop its
                    // threads. The callback runs outside _sync so a worker's late
                    // finally can observe the fence without deadlocking recovery.
                    _current.RecoveryPending = true;
                    _recoveryInProgress = true;
                    recoveryOperation = _current;
                }
                else
                {
                    return StartNewLease();
                }
            }

            bool workersStopped = true;
            try
            {
                workersStopped = recoverStalled == null || recoverStalled();
            }
            catch
            {
                workersStopped = false;
            }

            lock (_sync)
            {
                _recoveryInProgress = false;
                if (!workersStopped)
                {
                    System.Threading.Monitor.PulseAll(_sync);
                    return new Lease(
                        recoveryOperation.Id,
                        recoveryOperation.Generation,
                        reused: true,
                        recoveryPending: true);
                }

                // Do not publish a new generation until the old worker has
                // actually stopped. Late completion from the fenced generation
                // is rejected by IsCurrent/Complete.
                recoveryOperation.Active = false;
                recoveryOperation.RecoveryPending = false;
                var lease = StartNewLease();
                System.Threading.Monitor.PulseAll(_sync);
                return lease;
            }
        }

        private Lease StartNewLease()
        {
            var operation = new Operation
            {
                Id = "idx-" + Guid.NewGuid().ToString("N"),
                Generation = ++_nextGeneration,
                Active = true,
                StartedAtUtc = DateTime.UtcNow
            };
            _current = operation;
            _lastOperationId = operation.Id;
            return new Lease(operation.Id, operation.Generation, reused: false);
        }

        internal bool MarkStalled(int generation)
        {
            lock (_sync)
            {
                if (_current == null || !_current.Active || _current.RecoveryPending || _current.Generation != generation)
                    return false;
                _current.Stalled = true;
                _current.StalledAtUtc = DateTime.UtcNow;
                return true;
            }
        }

        internal bool MarkProgress(int generation)
        {
            lock (_sync)
            {
                if (_current == null || !_current.Active || _current.RecoveryPending || _current.Generation != generation)
                    return false;
                _current.Stalled = false;
                _current.StalledAtUtc = null;
                return true;
            }
        }

        internal bool MarkWorkerStarted(int generation)
        {
            lock (_sync)
            {
                if (_current == null || !_current.Active || _current.RecoveryPending || _current.Generation != generation)
                    return false;
                _current.WorkerStarted = true;
                _current.RetryPending = false;
                _current.Stalled = false;
                _current.StalledAtUtc = null;
                return true;
            }
        }

        internal bool MarkRetryPending(int generation, bool pending)
        {
            lock (_sync)
            {
                if (_current == null || !_current.Active || _current.RecoveryPending || _current.Generation != generation)
                    return false;
                _current.RetryPending = pending;
                return true;
            }
        }

        internal bool IsCurrent(int generation)
        {
            lock (_sync)
            {
                return _current != null
                    && _current.Active
                    && !_current.RecoveryPending
                    && _current.Generation == generation;
            }
        }

        internal bool Complete(int generation)
        {
            lock (_sync)
            {
                if (_current == null || !_current.Active || _current.RecoveryPending || _current.Generation != generation)
                    return false;
                _current.Active = false;
                return true;
            }
        }

        internal Snapshot GetSnapshot(bool workerAlive)
        {
            lock (_sync)
            {
                if (_current == null)
                {
                    return new Snapshot
                    {
                        OperationId = _lastOperationId,
                        Active = false,
                        WorkerAlive = false,
                        Recoverable = false,
                        State = "Idle"
                    };
                }

                string state = !_current.Active
                    ? "Idle"
                    : _current.RecoveryPending
                        ? "Recovering"
                        : !_current.WorkerStarted || _current.RetryPending
                            ? "Starting"
                            : _current.Stalled
                                ? "Stalled"
                                : workerAlive ? "Building" : "WorkerExited";
                bool recoverable = _current.Active
                    && (_current.RecoveryPending
                        || (_current.WorkerStarted
                            && !_current.RetryPending
                            && (_current.Stalled || !workerAlive)));

                return new Snapshot
                {
                    OperationId = _current.Id,
                    Generation = _current.Generation,
                    Active = _current.Active,
                    Stalled = _current.Stalled,
                    WorkerAlive = workerAlive,
                    WorkerStarted = _current.WorkerStarted,
                    RetryPending = _current.RetryPending,
                    RecoveryPending = _current.RecoveryPending,
                    Recoverable = recoverable,
                    State = state,
                    StartedAtUtc = _current.StartedAtUtc,
                    StalledAtUtc = _current.StalledAtUtc
                };
            }
        }
    }
}