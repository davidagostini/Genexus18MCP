# Issue #225 — explicit KB calls keep the session lease alive

## Symptom

A session could continue making successful calls with `kb=<alias>` while its
session-owned KB lease silently expired after the idle TTL. The next stateful
operation (`genexus_sdk_probe`, `genexus_doc`, or lifecycle status/cancel/index)
then returned `KB_LEASE_EXPIRED`, even though the caller had supplied the KB on
each request.

## Contract

- A request with an explicit `kb` that resolves to the currently selected KB
  renews that session lease without changing the context generation.
- A stateful request with an explicit `kb` for another or missing session
  context adopts that resolved KB and receives a fresh owner-bound lease.
- A stateful request also recovers a matching expired lease by creating a new
  context generation.
- A stateless request targeting another explicit KB does not change the
  session's selected KB.
- The lease refresh is process-local and performs no Knowledge Base operation;
  it does not open, specify, generate, build, reorg, deploy, publish, or run a
  KB.

## Implementation

`Program.RequestLoop` now records whether the caller supplied the `kb` field,
resolves the target once, and refreshes/adopts the session context before
loading the ownership snapshot used by the worker fence. The helper in
`Program.KbContext` compares the canonical alias, normalized identity, owner,
and context generation before renewing, so an explicit argument cannot renew a
lease for a different KB or session.

The existing `alias` fallback remains available for KB selection compatibility;
the automatic refresh is intentionally keyed to the explicit `kb` field so
`genexus_kb action=select alias=...` keeps its existing single selection path.

## Validation

- Matching explicit target renews the expiry without changing token or
  context generation.
- A different explicit target is adopted only for a stateful operation.
- An expired explicit stateful target receives a fresh lease.
- A stateless request for another target leaves session selection unchanged.
- Full Gateway validation is required before PR submission; the known local
  `KbCreateHelperTests` limitation is documented separately when the Windows
  .NET Framework MSBuild workload is unavailable.
