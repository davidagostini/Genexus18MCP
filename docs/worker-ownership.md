# Worker ownership and orphan cleanup

`WorkerProcess` owns a checkout-scoped lease for each spawned worker. The lease key is a SHA-256 digest of the normalized worker executable path and KB path, so two checkouts serving the same KB do not share cleanup records. A named Windows mutex serializes reservation writes across gateway processes.

The registry stores the owning gateway PID, worker PID, and the worker's UTC start time. Starting a worker with an existing live lease fails closed; it never enumerates or kills unrelated processes. When the gateway exits cleanly, `StopProcess` releases the record before notifying the pool so a replacement can reserve it. If a gateway dies, the next reservation may reap only the recorded worker when both PID and start time still match, preventing PID-reuse cleanup.

A health-check tick performs a once-per-minute reconciliation against this worker's exact registry record. This is a narrow legacy/orphan fallback, not a system-wide process or WMI scan. The old per-start `KillOrphanWorkers` sweep was removed. The existing command-line/WMI lookup used for the exit-code-17 diagnostic remains separate and is not a cleanup mechanism.

This design intentionally scopes ownership by executable path as well as KB path. A debug worker from checkout A and a debug worker from checkout B therefore have independent records and cannot reap one another. Operators should remove stale files under `%LOCALAPPDATA%\GxMcp\worker-ownership` only when no corresponding gateway is running; normal recovery handles stale records automatically.

The default ownership contract remains Gateway-owned and isolated. The supported
`Server.WorkerSharingMode="shared-host"` path uses a separate shared-worker
registry keyed by the normalized physical KB plus executable, GeneXus
installation, driver, and target major. A broker process owns the SDK Worker;
Gateways attach and detach through connection-scoped named pipes. A Gateway
shutdown closes only its attachment and never kills a Worker that still has
another attachment. The broker registry is discovery/recovery metadata, not a
sole liveness proof: PID start time, pipe handshake, connection heartbeat, and
generation must all agree before an attach is accepted.

When the child Worker exits unexpectedly, the broker keeps existing attachments,
fails only requests that were in flight with a retryable
`SHARED_WORKER_RESTARTED` envelope, and elects no second Gateway: the broker
increments the generation and respawns the compatible child itself. Three
crashes in one minute stop the host fail-closed. Each attachment has a bounded
output queue; a slow or disconnected Gateway cannot block another attachment.
The host remains alive while attached and reaps its child after the configured
idle timeout when the last attachment detaches.

The shared mode also propagates a Gateway-local write owner into the Worker. Every ordinary write with an owner takes a short operation-scoped lock for its target/part and releases it in a `finally`; an explicit lock held by the same owner is preserved, while foreign, corrupt, or unreadable locks fail closed unless the caller explicitly requests `force`. This prevents two attached clients from entering the SDK write path for the same target concurrently without changing the Gateway-local session/cache boundaries.

The shared mode is a supported `stdio-isolated` Worker-ownership option. Use
`Server.WorkerSharingMode: "shared-host"` when compatible Gateways should reuse
one SDK Worker; keep `"isolated"` when the agent intentionally needs one Worker
per Gateway or per operation. The configuration must resolve to the same
executable, installation, driver, target major, and physical KB. The normal
`stdio-isolated` mode with `WorkerSharingMode: "isolated"` remains available for
multi-Worker workflows.
