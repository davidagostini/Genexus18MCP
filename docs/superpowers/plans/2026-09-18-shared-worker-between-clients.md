# Shared Worker Between Independent MCP Clients Implementation Plan

> **For agentic workers:** Execute this plan task-by-task in the current checkout. Keep the default `stdio-isolated` path unchanged until the shared path has a process-level and live-KB gate.

**Goal:** Allow independent MCP Gateways to attach to one SDK Worker for the same physical Knowledge Base, while keeping Gateway sessions, authorization, caches, jobs, progress, and client notifications isolated.

**Architecture:** Add an opt-in `Server.WorkerSharingMode: "shared-host"` path. The first Gateway starts a broker mode of the existing `GxMcp.Worker.exe`; that broker owns one normal Worker child and exposes one named-pipe connection per Gateway. The child keeps its current single stdin/stdout channel and STA SDK executor. The broker rewrites child request IDs internally, restores them per attachment, routes progress by operation identity, broadcasts only explicitly shared KB notifications, and maintains attach heartbeats/refcounts. The default `WorkerSharingMode: "isolated"` continues to spawn and own one Worker per Gateway.

**Tech Stack:** C#/.NET 10 Windows Gateway, C#/.NET Framework 4.8 x86 Worker, Windows named pipes, Newtonsoft.Json, xUnit, existing PowerShell live harness, Node CLI configuration.

## Current constraints and invariants

- The GeneXus SDK remains process-bound, x86, STA, and single-flight per Worker. Sharing must not introduce a second SDK thread or a second KB in one SDK process.
- A shared Worker key includes normalized physical KB path, Worker executable compatibility identity, GeneXus installation path, driver, and target major. A different major, driver, installation, or executable never attaches silently.
- MCP sessions, session KB selection, `KbUseLeaseRegistry`, `SemanticCacheStore`, `OperationTracker`, `BackgroundJobRegistry`, `StateScope.ProcessScopeId`, and client-facing notification streams remain Gateway-local.
- Every attachment has a unique `attachId`. Worker request IDs are rewritten by the broker so equal IDs from different Gateways cannot collide.
- A slow or disconnected attachment cannot block another attachment. Input and output queues are bounded and failure is an explicit busy/transport envelope.
- Detaching a Gateway never kills a shared Worker while another attachment is alive. The broker reaps its child only after the last attachment disappears and the configured idle TTL elapses.
- The live acceptance target is two independent Gateway/client processes, one shared SDK Worker PID, and two distinct disposable objects changed concurrently in `C:\KBs\KBTeste`. Two Workers for one KB is a failure, not the target behavior.

## Phase 0 — durable contract and opt-in configuration

**Files:**
- Create: `docs/superpowers/plans/2026-09-18-shared-worker-between-clients.md`
- Modify: `src/GxMcp.Gateway/Configuration.cs`
- Modify: `cli/lib/config.js`
- Modify: `config.neutral.sample.json`
- Modify: `docs/kb-isolation-contract.md`
- Modify: `docs/worker-ownership.md`
- Test: `src/GxMcp.Gateway.Tests/ConfigurationParsingTests.cs`
- Test: `src/GxMcp.Gateway.Tests/Issue146AcceptanceMatrixContractTests.cs`

- [x] Add `Server.WorkerSharingMode` with the exact values `isolated` and `shared-host`; default to `isolated` when the property is absent in legacy configuration.
- [x] In strict configuration parsing, reject unknown sharing values, reject `shared-host` with `GatewayMode=http-shared`, and require `stdio-isolated` for the initial shared-host implementation.
- [x] Make the neutral CLI/sample configuration explicit about the isolated default without adding a KB path or changing client registrations.
- [x] Document that `shared-host` changes Worker ownership only; it does not share Gateway sessions or authorization.
- [x] Add red tests for invalid values, incompatible transport combinations, and preservation of the isolated default; run them before implementation and record the expected failures.
- [x] Run focused configuration tests and the project build after the green implementation.

## Phase 1 — host registry and broker process

**Files:**
- Create: `src/GxMcp.Gateway/SharedWorkerIdentity.cs`
- Create: `src/GxMcp.Gateway/SharedWorkerRegistry.cs`
- Create: `src/GxMcp.Worker/SharedWorkerHost.cs`
- Modify: `src/GxMcp.Worker/Program.cs`
- Test: `src/GxMcp.Gateway.Tests/SharedWorkerRegistryTests.cs`
- Test: `src/GxMcp.Worker.Tests/SharedWorkerHostProtocolTests.cs`

- [x] Define a canonical identity containing normalized KB path, Worker executable, GeneXus installation, driver, and major; compute a stable SHA-256 key for mutex/registry/pipe discovery.
- [x] Store an atomic registry record under `%LOCALAPPDATA%\GxMcp\shared-workers\<key>.json` with key, host PID/start time, child Worker PID/start time, pipe name, generation, updated UTC, and state.
- [x] Protect registry reservation with a named mutex. Validate PID plus process start time before treating a record as live. Never reap a process whose start time does not match.
- [x] Add `--shared-host` early startup to the existing Worker executable. Host mode runs before SDK initialization and does not acquire `SingleInstanceLock` itself.
- [x] Make the host create per-attachment named pipes, spawn the normal Worker child with stdio plus `--kb`, driver/major/install environment, and maintain the registry record for the child.
- [x] Add bounded startup/attach timeouts and explicit host failure diagnostics; attach requires a live identity record and a validated handshake.
- [x] Add registry/identity tests for normalization, incompatible identities, process start-time validation, concurrent reservation, and atomic record replacement; live runs also verified generation changes and stale-record cleanup.

## Phase 2 — multi-client named-pipe protocol

**Files:**
- Create: `src/GxMcp.Gateway/SharedWorkerConnection.cs`
- Create: `src/GxMcp.Worker/SharedWorkerHostProtocol.cs`
- Modify: `src/GxMcp.Worker/SharedWorkerHost.cs`
- Test: `src/GxMcp.Gateway.Tests/SharedWorkerConnectionTests.cs`
- Test: `src/GxMcp.Worker.Tests/SharedWorkerHostProtocolTests.cs`

- [x] Define newline-delimited internal envelopes for `attach`, `attach_ack`, `heartbeat`, `heartbeat_ack`, `detach`, `busy`, and `host_error`. Regular JSON-RPC requests remain opaque payloads inside the connection after attach.
- [x] Require the attach handshake to include protocol version, identity key, Gateway PID/start time, and a random attach nonce. Return `attachId`, host PID/start time, child Worker PID/start time, generation, and readiness.
- [x] Give each Gateway connection an independent bounded input queue, output queue, reader loop, writer loop, and cancellation source.
- [x] Rewrite every request-bearing JSON-RPC id to a broker-unique child id and keep an in-memory map to restore the original id and attachment. Remove the map on response, disconnect, or child crash.
- [x] Track operation/progress identities from `_meta.progressToken` and route `notifications/progress` only to the attachment that started that operation. Broadcast `resources/updated` only as an explicit shared KB event; never broadcast request responses or client messages.
- [x] Reject malformed, oversized, unknown, or unauthenticated frames without killing other connections. A full per-attachment queue returns a typed backpressure envelope to that attachment.
- [x] Send heartbeat frames at a bounded interval and remove a connection after pipe EOF. Update registry liveness from the host, not from a Gateway PID alone.
- [x] Add pure protocol tests for equal IDs/tokens across attachments, progress unicast, broadcast resource events, malformed frames, identity mismatch, and pipe-name validation.

## Phase 3 — Gateway spawn-or-attach lifecycle

**Files:**
- Create: `src/GxMcp.Gateway/IWorkerTransport.cs`
- Create: `src/GxMcp.Gateway/SharedWorkerTransport.cs`
- Modify: `src/GxMcp.Gateway/WorkerProcess.cs`
- Modify: `src/GxMcp.Gateway/WorkerPool.cs`
- Modify: `src/GxMcp.Gateway/Program.WorkerLifecycle.cs`
- Modify: `src/GxMcp.Gateway/Program.cs`
- Modify: `src/GxMcp.Gateway/Program.Whoami.cs`
- Test: `src/GxMcp.Gateway.Tests/WorkerPoolTests.cs`
- Test: `src/GxMcp.Gateway.Tests/SharedWorkerLifecycleTests.cs`
- Test: `src/GxMcp.Gateway.Tests/WorkerProcessTests.cs`

- [ ] Extract the current stdio transport behind the smallest internal transport seam without changing its framing or event behavior. The current implementation preserves the existing path through a shared-mode branch; a later cleanup can isolate the seam without changing the opt-in contract.
- [x] Add shared transport selection from `WorkerSharingMode`. Isolated mode continues to use the current direct `Process` path; shared-host mode uses registry discovery, host spawn, named-pipe attach, and child identity from the handshake.
- [x] Make `WorkerProcess.Start` use spawn-or-attach in shared mode. Only the elected Gateway starts the host; later Gateways attach to the exact identity record.
- [x] Make `StopProcess` detach in shared mode. It closes only the Gateway connection and does not kill a host that still serves another attachment.
- [x] Keep `OnWorkerExited` semantics local: a pipe/host failure aborts only this Gateway's pending requests and allows one respawn election. A normal Gateway shutdown does not trigger a shared Worker respawn.
- [x] Make planned reload explicit: the shared path does not kill the child from one Gateway; coordinated reload remains rejected/unsupported until a host-wide action is defined.
- [x] Surface `hostPid`, `workerPid`, `workerGeneration`, `attachmentId`, `sharingMode`, and attach state through `genexus_whoami`/health without exposing secrets.
- [x] Add lifecycle evidence for two Gateways/attachments sharing one child PID, close-one/continue-one, stale record recovery, one child respawn election, host crash re-election, and planned detach versus crash exit.

## Phase 4 — request isolation and notification correctness

**Files:**
- Modify: `src/GxMcp.Gateway/Program.WorkerLifecycle.cs`
- Modify: `src/GxMcp.Gateway/Program.RequestLoop.cs`
- Modify: `src/GxMcp.Gateway/Program.Notifications.cs`
- Modify: `src/GxMcp.Gateway/Program.ToolDispatch.cs`
- Modify: `src/GxMcp.Gateway/StateScope.cs`
- Modify: `src/GxMcp.Worker/Program.cs` only if the host handshake requires a child-side marker
- Test: `src/GxMcp.Gateway.Tests/RequestIsolationTests.cs`
- Test: `src/GxMcp.Gateway.Tests/SharedWorkerNotificationTests.cs`

- [x] Preserve session-bound cancellation matching on `(Gateway session, exact JSON-RPC id token)` after broker ID rewriting and Worker retries.
- [x] Preserve client progress tokens on the client-facing wire; internal operation IDs and attach IDs must never leak as client tokens.
- [x] Ensure a Worker crash aborts only requests attached to the affected Gateway connection or, when the child dies, returns a typed crash envelope to every affected attachment exactly once.
- [x] Keep `StateScope.ProcessScopeId` and artifact/job paths Gateway-local. Any Worker-generated notification carrying scope data is still validated before forwarding.
- [x] Add protocol/live tests for equal request IDs/tokens, attachment-scoped cancellation rewriting, progress unicast, resource broadcast rules, child/host disconnect recovery, and no cross-attachment response delivery.

## Phase 5 — object-conflict protection and practical usage

**Files:**
- Modify: `src/GxMcp.Worker/Helpers/WritePipeline.cs`
- Modify: relevant write services that accept target/part (`src/GxMcp.Worker/Services/WriteService*.cs`, `ObjectService.cs`, visual/pattern/edit services as identified by call graph)
- Modify: `src/GxMcp.Gateway/Program.ToolDispatch.cs`
- Modify: `src/GxMcp.Gateway/Routers/OperationsRouter.cs` only if internal owner metadata must be forwarded
- Test: `src/GxMcp.Worker.Tests/WritePipelineTests.cs`
- Test: focused write-service tests for each shared write path
- Docs: `docs/agent_playbook.md`, `docs/envelope.md`

- [x] Reuse the existing `genexus_multi_agent_lock` file contract instead of creating a second lock tool. Atomic acquisition/release now use a named mutex and lock parsing fails closed.
- [x] Wire `WritePipeline.AdvisoryLockCheck` into the canonical `WriteService.WriteObject` pipeline, which is shared by full edits/patches/bulk writes; add the cross-Gateway conflict regression.
- [x] Propagate an internal owner identity from the Gateway process to Worker write parameters without advertising a new public MCP field. Keep `force=true` explicit and auditable.
- [ ] Define practical semantics: ordinary single-call writes acquire an operation-scoped lock; multi-call read/modify/write workflows use the existing explicit lock with automatic heartbeat/renewal and release guidance. Do not silently allow an overwrite after a foreign lock.
- [ ] Add optimistic content/version checks where a write service already returns a version/snapshot token. A lock must prevent concurrent writes; a version check must detect stale reads between separate calls.
- [x] Return a stable conflict envelope with holder, expiry, target, part, and next steps. Never disclose unrelated source content or credentials.
- [ ] Test same-object conflict, different parts, different objects, same owner renewal, expiry takeover, force override, crashed owner, and two Gateways using identical target names.

## Phase 6 — performance, fairness, and usability

**Files:**
- Modify: shared host/transport queue classes from Phases 1–3
- Modify: `src/GxMcp.Gateway/Program.Whoami.cs`
- Modify: `src/GxMcp.Gateway/Configuration.cs` only for bounded queue/fairness settings if measurement proves a setting is needed
- Create/modify: `scripts/bench-shared-worker.ps1` or the smallest existing benchmark harness that can run two Gateway processes
- Docs: `docs/benchmarks/2026-09-18-shared-worker.md`
- Test: `src/GxMcp.Gateway.Tests/SharedWorkerPerformanceTests.cs`

- [x] Measure cold start separately from warm RPC latency. The shared path does not pay SDK cold start twice for one KB.
- [x] Measure one isolated client and two shared clients with warm read-only probes; record p50/p95/max, wall time, Worker PID, and artifacts in `docs/benchmarks/2026-09-18-shared-worker.md`.
- [x] Enforce bounded per-attachment queues so one Gateway cannot block another indefinitely.
- [x] Keep the SDK single-flight; parallelism is independent Gateway intake plus explicit queueing, not concurrent SDK access.
- [x] Add diagnostics showing attach state and queue/startup wait without exposing payloads.
- [x] Establish a measured acceptance threshold relative to the isolated single-client baseline: p95 <= 10 ms and shared-vs-isolated p95 overhead < 30%; the recorded run was 7.35 ms vs 5.82 ms (+26.29%).

## Phase 7 — process-level and live validation

**Files:**
- Create: `scripts/tests/test-shared-worker-process.ps1` or extend the existing process harness without changing unrelated cleanup behavior
- Create: `src/GxMcp.Gateway.Tests/SharedWorkerProcessHarnessTests.cs` if the repository can run a deterministic fake process harness
- Create ignored run artifacts under `scratchpad/shared-worker-*`
- Modify: `CHANGELOG.md`
- Modify: `docs/live-kb-test-harness.md`

- [x] Run a fake/controlled process matrix before touching the real KB: one host/one child for two attachments, distinct identity/KB isolation, same-id isolation, protocol cancellation/progress routing, close-one/continue-one, stale registry cleanup, child crash/respawn, host crash/re-election, slow output, last-detach TTL, and reload-adjacent detach behavior. Live evidence is recorded in `docs/benchmarks/2026-09-18-shared-worker.md`; planned coordinated reload remains an explicit unsupported action.
- [x] Build Gateway and Worker, verify the fresh Debug Worker binary, and use the freshly built binaries for live tests.
- [x] Validate `C:\KBs\KBTeste` opens successfully with GeneXus 18 and exercise both cold/current index states explicitly.
- [x] Start two independent Gateway/client processes in shared-host mode with the same explicit KB path. Read back `genexus_whoami` from both and require the same child Worker PID/generation plus distinct attachment IDs.
- [x] Create two unique disposable objects in the authorized `KBTeste` fixture. Apply independent changes concurrently, read them back from both clients, and verify cleanup.
- [x] Run same-object conflict, child crash/respawn, host crash/re-election, and duplicate-client-id scenarios. The second client receives the typed conflict envelope.
- [x] Record Gateway/host/Worker PIDs, attach IDs, generation, phase timings, warm RPC latency, queue/SDK latency, isolated logs, and cleanup artifacts.
- [ ] Mark the live gate unavailable, not passed, if the SDK/license/KB cannot exercise the scenario.

## Final repository gates

- [ ] Run focused tests after every vertical slice and review `git diff`/`git diff --check` before moving to the next slice.
- [ ] Run `dotnet build src/GxMcp.Gateway/GxMcp.Gateway.csproj -v:minimal`.
- [ ] Run Worker builds with explicit `GX_PATH` for installed compatible majors; at minimum GeneXus 18 for the requested live fixture.
- [ ] Run affected Gateway and Worker tests with visible test-run banners.
- [ ] Run `dotnet test Genexus18MCP.sln --no-restore -v:minimal -m:1` serially.
- [ ] Run `npm test` and `npm run lint`.
- [ ] Run `npm run test:integration` or record the exact bounded blocker if the coordinator cannot run.
- [ ] Run `python scripts/validate-tool-contracts.py` and operation inventory checks if public schema/help/contract files changed.
- [ ] Review the complete diff and status; preserve unrelated changes and generated scratch artifacts outside version control.
- [x] Add a `CHANGELOG.md` entry under `## Unreleased` referencing issue `https://github.com/lennix1337/Genexus18MCP/issues/228` after the implementation and live smoke were verified.
- [ ] Report under `Feito`, `Validado`, and `Riscos/pendências`, separating unit, process, live-KB, and performance evidence.
