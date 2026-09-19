# Deterministic conversion bundle (Plan 107)

Status: **design spike — not a shipping conversion engine**.

This contract makes a conversion run reviewable and repeatable. It is deliberately
narrowed to the deferred Business Component path. A bundle is evidence about one
run; it is not permission to call a Business Component and it must not be inferred
from the existing typed-SQL adapter.

## Bundle contract

The normative schema is [`schemas/conversion-bundle.schema.json`](../schemas/conversion-bundle.schema.json), version `conversion-bundle/1.0`.

A bundle contains:

- **source** — KB alias, object identity/type, stable source snapshot hash, source
  revision, and the ordered set of parts used as input. No credentials or database
  identifiers belong here.
- **target** — `business-component`, language, generator version, SDK compatibility
  profile, and explicit assumptions. Assumptions are data, not hidden defaults.
- **artifacts** — relative POSIX paths, media types, byte counts, and SHA-256 hashes.
  Paths cannot escape the artifact root; generated output is never addressed by an
  absolute machine path.
- **diagnostics** — stable severity/code/message records and an overall state.
- **acceptance** — human approval (`pending` by default), fixture identity, and
  named checks with evidence references. `pending` is the only valid initial state.
- **determinism** — the canonicalization oracle version and digest of the bundle.

The schema intentionally does not include request credentials, connection strings,
process IDs, wall-clock timestamps, or machine-local paths. If operational timing
is needed for debugging, store it outside the signed/reviewed bundle.

## Determinism oracle

Run the prototype with:

```text
python scripts/validate-conversion-bundle.py --self-test
python scripts/validate-conversion-bundle.py <bundle.json> --artifacts-root <artifact-root>
```

The oracle normalizes JSON with sorted object keys, compact separators, UTF-8, and a
terminal newline before hashing. Array order is meaningful and therefore must be
produced by an explicit ordering rule (source parts, diagnostics, checks, and
artifacts). Artifact hashes are computed from bytes with no newline conversion.
A second run is deterministic only when the canonical digest, every artifact hash,
artifact byte count, and diagnostics are identical. The oracle must reject absolute
paths, traversal, missing artifacts, uppercase/non-hex SHA-256 values, and a wrong
oracle version. It does not claim that generated code is semantically correct.

## Disposable fixture and licensing gate

The BC spike may run only after a human approves a fixture manifest identifying:

1. a disposable, isolated KB/database and a generated .NET application owned by the
   test operator;
2. one synthetic Transaction with a controlled key, default value, negative-
   quantity `Error` rule, and one second-level row;
3. the existing application endpoint and authentication boundary, including an
   explicit statement that no new host or implicit publication was created;
4. the SDK/runtime version and license entitlement permitting generation and test
   execution; and
5. reset/cleanup steps, expected data retention, and a manifest hash.

The fixture must be recreated or reset between runs. Secrets, tokens, connection
strings, customer data, and proprietary generated sources must not be committed.
A redacted fixture manifest and hashes are sufficient for review. The absence of
this fixture or an authorized endpoint is a **blocked** state, never a skipped
success and never live evidence.

## Acceptance sequence

Once the fixture exists and approval is recorded, the only proposed experiment is:

1. preview the source and capture the bundle;
2. call invalid `Save` (negative quantity), record rule messages and unchanged data;
3. call valid `Save`, record generated key, default, second-level row, commit, and
   reread;
4. force/reproduce rollback and record the post-rollback read;
5. change preview/environment values and verify stale target rejection; and
6. repeat the same input twice and compare the oracle outputs byte-for-byte.

A receipt is accepted only if all checks have evidence, approval is `approved`, and
no diagnostic is `error` or `blocked`. This spike does not implement those calls.

## Failure states

| State/code | Meaning | Required action |
|---|---|---|
| `blocked/FIXTURE_REQUIRED` | Disposable generated app or authorized endpoint is absent | Stop; do not call an endpoint |
| `blocked/HUMAN_APPROVAL_REQUIRED` | Approval is pending/rejected | Stop and request explicit approval |
| `failed/SOURCE_CHANGED` | Source snapshot differs from the reviewed hash | Re-preview and obtain approval |
| `failed/TARGET_MISMATCH` | SDK/runtime/language assumptions differ | Rebuild the target profile |
| `failed/ARTIFACT_NONDETERMINISTIC` | Hash, ordering, or bytes differ between equal inputs | Reject output and investigate |
| `failed/VALIDATION_ERROR` | Schema, path, or artifact integrity check failed | Correct the bundle; do not execute |
| `warning/RUNTIME_UNPROVEN` | Fixture result does not prove SDK semantics | Keep capability deferred |

The existing typed-SQL response remains typed SQL (`businessRulesExecuted=false`);
it cannot satisfy any BC acceptance check.

## Unresolved SDK questions

- Which generated runtime endpoint and authentication boundary are authorized for
  this repository's fixture?
- Which generated assembly/API version exposes preview, `Save`, messages, commit,
  rollback, reread, and stale-target detection without relying on reflection alone?
- How are generated keys, defaults, second-level rows, and transaction messages
  represented across the supported SDK/runtime versions?
- What is the authoritative source revision and serialization for a stable snapshot?
- Which SDK compatibility profile must be embedded in `target` and how is it
  independently verified against Plan 105's reproducibility artifacts?
- Can the runtime guarantee idempotent replay, or must the bundle carry an explicit
  reconciliation state after ambiguous commit/timeout?

Until these questions and the fixture gate are answered, the recommended real-build
boundary is a read-only bundle producer plus human review—not a public BC tool.
