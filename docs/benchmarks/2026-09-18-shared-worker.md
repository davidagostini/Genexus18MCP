# Shared Worker measurements (2026-09-18)

These are local, bounded measurements against `C:\KBs\KBTeste` with the freshly built Debug Gateway/Worker. They are evidence for the opt-in transport only; they are not a release benchmark and do not include a second SDK executor.

## Warm RPC comparison

Operation: `genexus_whoami`, after opening the KB and warming the process.

| Mode | Clients | Samples | Worker PID | p50 | p95 | Max | Wall for batch |
|---|---:|---:|---:|---:|---:|---:|---:|
| `stdio-isolated` | 1 | 20 | 14956 | 5.33 ms | 5.82 ms | 6.96 ms | sequential |
| `shared-host` | 2 | 10/client | 12372 | A: 6.01 ms / B: 5.76 ms | A: 7.24 ms / B: 7.35 ms | A: 7.24 ms / B: 7.35 ms | 62.18 ms, concurrent |

The shared path adds a small pipe/multiplexing cost in this warm read-only probe, but it does not initialize the SDK twice. The SDK remains single-flight by design; concurrent clients are accepted independently and serialized at the STA boundary. The bounded opt-in acceptance used here is p95 <= `10 ms` and shared-vs-isolated p95 overhead < `30%`; the observed shared p95 was `7.35 ms` versus `5.82 ms` isolated (`+26.29%`).

Artifacts:

- Shared benchmark: `scratchpad/bench-shared-worker-e144a245726442b2a50b11074bfece70/summary.json`
- Isolated baseline: `scratchpad/bench-isolated-worker-85836a97371447cdbfff99f5cb9954e4/summary.json`

## Two-client KB smoke

The process smoke used two independent stdio Gateways, the same explicit KB and two distinct attachments. The final run observed:

- Host PID `20268` and child Worker PID `15740`, generation `1` at attach.
- Both Gateways reported the same child PID/generation and different `attachmentId` values.
- Two simultaneous MCP requests using the same client-facing JSON-RPC id (`duplicate-client-id`) returned to the correct independent Gateways.
- After killing the child, the same host stayed alive and published child PID `13992`, generation `2`; both existing attachments continued.
- Two disposable Procedures were created concurrently on the shared child. Total wall time was `374.3 ms`; client A took `345.7 ms`, client B `374.0 ms`. Both returned `ObjectCreated`.
- Client A acquired an explicit Gateway-local object lock. Client B's same-object Source edit was rejected with `TargetLockedByOtherAgent`; the holder was reported without source/credential data.
- Ordinary writes now also acquire a short operation-scoped lock automatically when the Gateway owner context is present; the explicit lock in this smoke intentionally held ownership across multiple calls.
- The lock was released, both disposable objects were read back independently from both Gateways (one bounded `IndexNotReady` retry) and deleted with `persisted=true` and `rereadConfirmed=true`; both Gateway processes exited with code `0`.
- The live `genexus_doctor` response reported `mode=shared-host`, identity key, pipe name, host/worker PIDs, generation, attachmentId and `connectionError=null`, with no warnings on a healthy attachment.
- After killing the host itself, both Gateways re-elected one new host (`hostPid=45144`) and one child (`workerPid=23444`) before shutdown. The Gateway log showed one host start/attach for each election, not duplicate brokers; the harness then stopped the re-elected host and removed its registry record.
- A bounded direct-host TTL probe with `--idle-ms 1500` exited without attachments, removed the registry record, and left zero shared Worker child processes.

Protocol unit coverage also verifies progress unicast, cancellation rewriting by attachment, malformed/oversized frame rejection, and identity validation. A slow-consumer probe queued 180 requests without reading that Gateway's stdout; the other attachment answered in `8.4 ms` on the same Worker PID, then continued on that PID after the slow Gateway detached; the surviving Gateway exited `0`.
