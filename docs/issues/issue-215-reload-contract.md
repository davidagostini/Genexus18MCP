# Issue #215 — worker reload scope and hard hot-swap contract

## Symptom

The `genexus_worker_reload` contract exposed `alias`/`kb` next to
`force=true`, although the direct force path resets every open Worker and does
not run the graceful drain-window copy hook. Consequently,
`force=true, mode=hard, sourceDir=...` could be accepted while the requested
directory was not applied.

## Decision

The current force implementation remains intentionally global. Its contract
now states that:

- `force=true` affects all open Workers and reports `scope=all-open-workers`;
  `alias`/`kb` do not make this path selective.
- `mode=hard` is supported only by the graceful path, where the old process is
  drained, `sourceDir` is copied after it exits, and the replacement is checked
  for SDK readiness.
- `force=true` combined with `mode=hard` is rejected before any Worker is
  stopped, with `ReloadForceHardUnsupported` and `sourceDirApplied=false`.
- `mode=soft` without `force` continues to honor `alias`/`kb` and reload only
  the selected Worker.

This is the smallest safe change for the existing global force design: it
prevents a false hot-swap success without introducing a new selective process
supervisor in the same patch.

## Implementation

The gateway validates the force/mode combination before `StopAll()`. Successful
force responses explicitly include `scope=all-open-workers` and
`selectorApplied=false`. Tool schema and help text now describe the global
scope, mark the operation as destructive, and distinguish the verified graceful
hot-swap from the emergency restart.

## Safety boundary

The rejection path is gateway-only and does not open, select, stop, respawn,
specify, generate, build, reorg, deploy, publish, or execute a Knowledge Base.
