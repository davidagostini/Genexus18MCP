# Issue #219 — stdio EOF shutdown

## Symptom

When an MCP client closed the gateway's standard input, a gateway configured
with an HTTP port stayed alive indefinitely. Its worker and ownership lease
also remained active, so the next session could observe an orphaned owner and
misleading startup/index diagnostics.

## Contract

- `Server.McpStdio=true`: EOF is the end of the gateway session, regardless of
  whether an HTTP port is configured.
- In-flight stdio requests receive a bounded drain window of two seconds.
- After the drain, the gateway cancels its lifetime, stops the worker, releases
  its shared lease, and exits.
- `Server.McpStdio=false`: the process is HTTP-only and remains long-lived.

## Implementation notes

The stdio loop now tracks dispatched requests and handles EOF explicitly. The
HTTP server and heartbeat use the gateway lifetime cancellation token, so the
HTTP task does not restart or recover a port during intentional shutdown.
Shutdown is idempotent because EOF and process-exit cleanup can overlap.

## Validation

- `GatewayProcessLeaseTests`: 7 passed, including stdio-with-HTTP and
  HTTP-only lifecycle contracts.
- Full Gateway suite: 1,704 passed and 15 skipped. Two unrelated
  `KbCreateHelperTests` require the Windows .NET Framework MSBuild installation
  that is not available in this checkout; they fail before the tested gateway
  lifecycle code is exercised.
- `git diff --check` passed.

The change is lifecycle-only: it does not select or modify a Knowledge Base and
does not trigger Specify, Generate, Build, Rebuild, deployment, publication, or
execution.
