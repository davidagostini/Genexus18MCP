# Capability release states

This document defines the design contract for reporting whether a Genexus18MCP
capability is merely described, observed in the installed SDK, proven against a
real isolated Knowledge Base, or safe to ship. It is a design/spike: it does
not change MCP response envelopes or promote any capability by itself.

## Goals and non-goals

The contract makes evidence inspectable by humans and machines while preserving
the existing `genexus_sdk_probe` and `genexus://kb/capabilities` contracts.
A release report may summarize those existing surfaces, but it must not claim a
stronger state than the evidence actually supports.

It does not:

- treat reflection, a mocked SDK, or a zero exit code as live-KB proof;
- embed KB paths, connection strings, credentials, object contents, or secrets;
- hide a tool from `tools/list` based on a per-KB state;
- change the current public capability payload in this spike.

The machine-readable report is defined by
`docs/schemas/capability-release-report.schema.json`; the example is in
`docs/examples/capability-release-report.example.json`.

## State vocabulary

States are ordered by evidence strength. `unsupported`, `unavailable`, and
`expired` are terminal observations for the current evidence set, not weaker
ways to claim support.

| State | Meaning | Minimum evidence |
|---|---|---|
| `contract-tested` | The capability's public request/response contract is covered by deterministic tests. | Contract fixture or unit/integration test, exact command, source commit. |
| `sdk-probed` | The installed SDK exposes the required surface and the probe completed. | `genexus_sdk_probe` result, SDK version, source commit, timestamp. This does not prove persistence or IDE parity. |
| `live-kb-verified` | The capability worked against the declared isolated synthetic fixture, including the relevant read-back/reopen assertion. | Fixture identity and revision, isolation attestation, SDK version, test command, terminal result. |
| `release-gated` | The capability is eligible for the target release after all required release checks passed. | Valid live evidence plus release-preflight result, source commit, artifact/report digest, and no failed mandatory gate. |
| `unsupported` | The product deliberately does not support this capability in the current scope. | Owner-approved reason and a re-evaluation condition; never inferred from a missing SDK type. |
| `unavailable` | Evidence could not be collected because a required environment or fixture was absent. | Explicit reason such as `sdk-not-installed` or `fixture-not-configured`; never counted as pass. |
| `expired` | Previously valid evidence exceeded its freshness window or its identity no longer matches. | Prior evidence reference, expiry timestamp, and demotion reason. |

A capability has exactly one `state` in a report. Its `evidence` array retains
all relevant observations, including failed or expired observations, so a
consumer can distinguish “not supported” from “not tested here”.

## Provenance contract

Every evidence record carries:

- `kind`: `contract-test`, `sdk-probe`, `live-kb`, or `release-gate`;
- `collectedAtUtc` and `sourceCommit`;
- `command`: the reproducible command or workflow name, with secrets removed;
- `result`: `pass`, `fail`, `skipped`, or `unavailable`;
- `expiresAtUtc` for evidence that is freshness-bound;
- `sdkVersion` when SDK code was involved;
- `fixtureId` and `fixtureRevision` for live-KB evidence;
- `reportDigest`/`artifactDigest` only when linking a release artifact.

The fixture identifier is an opaque revision label. It must not contain a KB
path or database identifier. The existing fixture manifest remains the source
for isolation attestation; its database IDs and credentials must never be
copied into the release report.

Evidence is valid only when all applicable identities match: source commit,
SDK version, fixture revision, operation set, and (for a release gate) the
artifact digest. A changed identity creates a new observation; it does not
extend an old one.

Default freshness windows for the proposal are:

- contract tests: 30 days, or until the contract/source changes;
- SDK probe: 14 days, or until the SDK/source changes;
- live-KB evidence: 7 days, or until the fixture, SDK, or source changes;
- release gate: target release only (never reusable for a later version).

The shorter of the time window and an identity change wins. Clock parsing is
UTC and fail-closed.

## Promotion and demotion rules

Promotion is monotonic within one report and requires the predecessor evidence:

```text
contract-tested → sdk-probed → live-kb-verified → release-gated
```

Rules:

1. A capability may enter `contract-tested` only from a passing deterministic
   contract test.
2. `sdk-probed` requires an unexpired `contract-tested` observation and a
   passing probe against the installed SDK. Type presence alone is insufficient.
3. `live-kb-verified` requires an unexpired SDK probe, an attested disposable
   fixture, and the capability-specific live assertion. For writes this
   includes persistence and reopen/read-back evidence.
4. `release-gated` requires current live evidence where the capability is
   release-critical, a passing `release-preflight` phase, and matching source
   and artifact/report digests. A skipped or unavailable phase cannot promote.
5. A failed observation never promotes. It is retained with `result=fail` and
   may demote an existing state when it is for the same identity and operation.
6. Expiry demotes to the strongest predecessor with still-valid evidence. If no
   predecessor remains valid, the state becomes `expired` (not `unavailable`).
7. A missing SDK or fixture produces `unavailable`, not `sdk-probed` or
   `live-kb-verified`. CI may continue only when the gate is explicitly
   optional, and the report must retain the reason.
8. `unsupported` is an explicit product decision. It cannot be promoted by a
   reflection match or by a passing generic smoke test.
9. Release-gated evidence is never carried across versions. A new release must
   run its own gate, even when the source commit is unchanged.

A consumer should fail closed for any capability required by the release whose
state is not `release-gated`. Optional capabilities may be shown as
“not verified in this environment” with the exact reason.

## CI and release integration proposal

This is the intended wiring; implementation is a follow-up, not part of this
spike.

### Pull requests and ordinary CI

- Run contract tests on hosted Windows CI and publish a report containing only
  `contract-tested` evidence.
- Run `genexus_sdk_probe action=capabilities` on the SDK-capable self-hosted
  lane. A missing SDK records `unavailable`; it never turns the job green as a
  release gate.
- Validate every report against the JSON Schema and reject unknown states,
  missing provenance, future timestamps, or secret-like fields.

### Live-KB workflow

Extend `.github/workflows/live-smoke.yml` with a report output. The existing
required `kb_path` and `fixture_manifest` inputs remain mandatory. The workflow
should invoke `scripts/test-live.ps1 -RequireBuildAll`, write a report under the
runner temp directory, and upload it as an artifact. The report records
`live-kb-verified` only after the existing terminal Build All criteria and the
capability-specific read-back assertions pass. Missing prerequisites remain
`unavailable`/failed according to the current harness behavior.

### Release preflight and artifact

`release-preflight.ps1` should consume the report after the solution, CLI,
contract, and live phases. It should:

1. verify the report schema and source commit;
2. require `release-gated` for the mandatory capability set;
3. reject stale evidence, skipped live phases, and mismatched fixture/SDK
   identities;
4. write the report digest into `gxmcp-manifest.json`; and
5. package the report beside the existing SBOM and manifest.

The release manifest remains authoritative for artifact hashes. The capability
report is evidence referenced by that manifest, not a replacement for it. The
normal release rule still applies: no `release.ps1`, tag, push, or publication
without explicit maintainer approval.

## User-facing examples

### SDK probe: honest but not live-verified

```json
{
  "schemaVersion": "genexus-sdk-capabilities/1",
  "capabilities": [
    {
      "capability": "authoring.transaction",
      "status": "available_unverified",
      "evidence": {
        "kind": "signature_probe",
        "persistenceVerified": false
      }
    }
  ],
  "note": "Signature availability is not proof of a successful save. Run the certified fixture before enabling an authoring path."
}
```

This existing payload is intentionally not rewritten by the spike. A release
report may classify the corresponding observation as `sdk-probed`, but only
when it adds the provenance required by this document.

### Capability resource for an agent

```text
Read `genexus://kb/capabilities`.
If authoring.transaction.state is live-kb-verified, a save/reopen workflow is
supported by the certified fixture. If it is sdk-probed, explain that the SDK
surface exists but persistence is not certified. If it is unavailable,
continue only with read-only work and report the missing fixture/SDK reason.
```

### Unsupported versus unavailable

```json
{
  "capability": "data.business_components",
  "state": "unsupported",
  "reason": "Generated application runtime is outside the design-time SDK scope."
}
```

```json
{
  "capability": "authoring.transaction",
  "state": "unavailable",
  "reason": "fixture-not-configured",
  "nextAction": "Configure GXMCP_TEST_KB and GXMCP_TEST_FIXTURE on the self-hosted Windows runner."
}
```

The first is a product boundary; the second is an environment gap. Neither is
a successful release gate.

## Acceptance checklist for a future implementation

- [ ] Report schema and example validate in hosted CI without the GeneXus SDK.
- [ ] Contract, SDK, live, and release transitions have regression tests.
- [ ] Live evidence includes fixture isolation and write/reopen proof where
      applicable.
- [ ] Expiry and identity mismatch demote states deterministically.
- [ ] Release artifacts carry the report digest and never carry secrets.
- [ ] `tools/list`, existing SDK probe payloads, and MCP envelopes remain
      backward-compatible.
