# Typed visual authoring contract

Status: **DESIGN_SPIKE — not an executable MCP capability**

This document defines the boundary for safe visual authoring without claiming
that the GeneXus SDK can perform any operation that has not been proven against
a disposable live KB. The acceptance corpus is
[`plans/108-typed-visual-authoring.acceptance.json`](../plans/108-typed-visual-authoring.acceptance.json).

## Boundary

The authoring pipeline accepts a versioned, typed intent and operates only on
an authoritative visual surface selected by object identity, part identity, and
surface kind. It must not treat arbitrary layout XML as an authoritative
write API.

```text
detect authoritative surface
  -> read typed baseline + opaque revision
  -> validate typed intent and capability
  -> route mutation through the typed SDK adapter
  -> validate schema/gotchas and generated output
  -> optional preview evidence
  -> persist only after all required gates pass
```

Raw XML is an observation/diagnostic format in this spike. A future adapter may
use XML internally, but it must not expose XML replacement as a successful typed
mutation when the SDK route is unavailable. Structural create/remove remains
unsupported until the SDK probe proves those operations and their rollback
semantics.

## Contracts

The wire contracts below are normative names and fields. They are intentionally
language-neutral; a later implementation can project them into C# records and
JSON Schema without changing the MCP envelope.

### `VisualAuthoringRequest`

Required fields: `contractVersion`, `target`, `surface`, `baseline`, and
`operations`.

- `contractVersion`: exact value `visual-authoring/1`.
- `target`: `{ kbAlias, objectId, objectType, partId }`; `objectId` is the
  stable SDK identity, not a display name.
- `surface`: `{ kind, authoritative, capability }`.
- `baseline`: `{ revision, sourceHash, capturedAtUtc }`; the revision is opaque
  and must be echoed by the SDK read-back.
- `operations`: typed operations only: `setProperty`, `bindEvent`,
  `addControl`, `removeControl`, or `applyPattern`.
- `mode`: `preview` or `persist`; `persist` requires a successful preview
  receipt for the same baseline and intent hash.
- `rollbackOnFailure`: must be explicit; defaulting is forbidden.

`setProperty` values carry a declared `valueType` (`string`, `boolean`, `int`,
`enum`, `color`, or `expression`) and a typed `value`. `bindEvent` carries an
SDK event identity; a display attribute such as `OnClickEvent` is not evidence
that the internal event table was updated.

### `VisualAuthoringReceipt`

A receipt contains `contractVersion`, `status`, `target`, `baselineRevision`,
`intentHash`, `mutationRoute`, `validation`, `persistence`, and `evidence`.

- `mutationRoute` is `sdk-typed` or `rejected`; `raw-xml` is never a success
  route.
- `validation` reports stage results for authoritative surface, typed schema,
  gotchas, generated output, and optional browser preview.
- `persistence` explicitly reports `attempted`, `committed`, `verified`, and
  `rolledBack`; all four are booleans.
- `evidence` is either a complete `PreviewEvidence` value or `null` when the
  preview stage was not requested.
- a failed or skipped stage includes a stable `code` and a remediation hint;
  free-form logs do not replace these fields.

### `PreviewEvidence`

Preview evidence is an observation, not an authorization to persist. It must
include `capturedAtUtc`, `url`, `screenshot`, `accessibilityTree`, `renderHash`,
`baselineRenderHash`, `browser`, `build`, and `sourceRevision`.

`screenshot` is `{ path, sha256, mediaType }`, and
`accessibilityTree` is `{ path, sha256, nodeCount }`. Paths must be inside the
fixture evidence directory and hashes are calculated over the stored bytes.
A missing browser driver is `skipped` with `BrowserDriverUnavailable`; it is
never reported as passed.

## Validation and safety stages

1. **Authoritative surface detection** — resolve stable target identity and
   prove the part is a supported visual surface. Reject ambiguous names,
   non-authoritative projections, and unsupported structural operations.
2. **Typed intent validation** — validate operation/value types, event identity,
   target capability, and baseline presence. Reject unknown properties rather
   than silently forwarding them.
3. **SDK mutation** — invoke the typed SDK adapter on an STA worker. The adapter
   returns a mutation receipt; reflection may discover a candidate API but does
   not make it supported.
4. **Schema and gotcha validation** — validate legal element/property
   combinations, form-kind constraints, event-table binding, and pattern
   constraints. A known non-functional result is an error, not a warning.
5. **Generate/build verification** — verify generated source or runtime output
   against the receipt. Compile success alone is insufficient for event wiring
   or disabled controls.
6. **Optional browser evidence** — capture screenshot and accessibility tree,
   compare to the baseline, and retain hashes. Browser evidence can block a
   persist request when the caller requires it but cannot turn an unverified
   mutation into success.
7. **Persist and read-back** — commit only after required gates pass, reread the
   stable target, and compare the expected revision/hash. Any mismatch invokes
   rollback and a second read-back.

## Baseline, rollback, and drift

The baseline is immutable for one request and includes the source revision,
normalized typed tree hash, and (when requested) render hash. A request with a
stale or missing baseline is rejected before SDK mutation. External changes
between preview and persist produce `BaselineConflict`; the caller must reread
and preview again.

Rollback is a compensating SDK mutation using the captured typed baseline, not a
raw file copy. The receipt must distinguish `rollbackAttempted`,
`rollbackVerified`, and `rollbackFailed`. If rollback cannot be verified, the
result is terminal `RollbackUnverified` and the capability remains blocked for
that target; no success envelope is allowed.

Untouched controls, rules, event bindings, theme references, and pattern-owned
children must be present in the post-write read-back. A diff that cannot prove
preservation is a failed validation, even when the requested property changed.

## Pattern-license variability

Pattern application is a separate capability from generic visual mutation.
Pattern identity, version, license/availability state, and template revision
must be recorded. A missing or unlicensed pattern engine returns
`PatternUnavailable` and does not fall back to hand-authored XML. Acceptance
must run with at least one available pattern and one unavailable/unlicensed
case; byte-for-byte equality is not required for GUIDs or attribute ordering,
but semantic structure and owned children are required to match the oracle.

## Live-KB gate

The corpus is executable only with an attested disposable KB, matching SDK
build, isolated datastore, authorized browser/test endpoint, and (for pattern
cases) the installed pattern engine. Reflection dumps, fake adapters, compile
success, and unit tests prove contract shape only; they do not satisfy the live
KB gate. Missing prerequisites produce `SKIPPED` with a reason and are excluded
from pass counts.

No new MCP tool, router entry, schema, or worker mutation is introduced by this
spike. Implementation may begin only after the corpus is run against the live
fixture and every required scenario is either passed with evidence or explicitly
rejected as unsupported.
