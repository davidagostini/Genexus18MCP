# Transaction semantic ops and native WorkWith actions

## Transaction Structure

`genexus_edit mode=ops` forwards `verifyMode` to the Worker for both Structure
DSL and XML persistence. An omitted mode on Structure uses semantic comparison,
which tolerates only SDK-generated attribute descriptions derived from names;
custom descriptions, missing attributes and changed formulas remain mismatches.
Explicit `exact` and `normalized` keep their existing contracts.

Attribute operations group wrapped formulas into complete logical declarations
before inserting, replacing or removing attributes. Formula text and nested-level
indentation survive unchanged when unrelated fields are updated. Replacing a
formula removes the old continuation and preserves its description and metadata.
The SDK's attribute ordering behavior remains a separate limitation: these ops do
not claim that SDK persistence honors an `after` ordering hint.

## Standard WorkWith for Web

`genexus_workwith` targets the native WorkWith pattern. WorkWithPlus remains under
`genexus_wwp`. Raw structural PatternInstance edits remain rejected; full and patch
rejections direct native WorkWith callers to `genexus_workwith action=add_action`.

`add_action` rejects a known Procedure parameter-count mismatch before mutation,
including dry-run. The error retains the plan and expected/supplied counts.
Unknown arity is not presented as validated; parameter types and expressions are
not compiled by this preflight.

`remove_action` requires an unambiguous instance plus explicit `tab`, `actionName`
and current PatternInstance `baseVersion`, including dry-run. Read the complete
PatternInstance first, then preview:

```json
{
  "name": "WorkWithWebExample",
  "action": "remove_action",
  "tab": "General",
  "actionName": "PrintStatement",
  "baseVersion": "<token from the current PatternInstance read>",
  "dryRun": true
}
```

The preview invokes the typed SDK collection removal in a detached model and
returns all tabs/actions before and after, without Save. Persistence requires a
recovery snapshot, checks the token again inside the SDK transaction and uses
native `SaveTo`/Save. No structural XML fallback exists for removal. The
`SkipApplyPattern` flag is set and restored around Save using the existing
isolation scope; this requests suppression, but does not substitute for live
proof of every installed callback's behavior. Native serialization may turn
pattern defaults into explicit overrides.

Verification reloads an independent persisted instance and compares every tab
and action. On failure, the original typed model is restored only if the current
token still matches this operation's Save, avoiding overwriting newer edits.
Rollback itself is verified with another fresh read. An unverified result must
be inspected before another edit; automatic retry is unsafe. Open-writer profiles
still allow writers outside the per-target process lock.

## Validation evidence and limit

Pure regression tests cover logical declarations, generated/custom descriptions,
verification-mode routing, native rejection guidance, parameter arity, ambiguous
selectors, complete removal plans, typed collection removal, concurrency wiring,
SDK Apply suppression and rollback verification wiring. Mutation checks revert
logical grouping, semantic default, mode forwarding, guidance, arity refusal,
typed removal and the selected removal index; each must make its guard fail.

Real native removal, persisted-neighbor preservation and Save callback isolation
need a disposable authorized KB. They are unavailable when no such fixture is
present; pure tests and SDK compilation are not labeled as live persistence proof.
