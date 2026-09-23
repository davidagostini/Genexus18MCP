# WorkWithPlus WebPanel authoring

Declare variables with `genexus_variable` before adding their controls. Use the
PatternInstance as the layout source; generated WebForm edits alone are overwritten
by pattern application.

Read the instance with `genexus_read` and retain the complete `versionToken`:

```json
{"name":"WorkWithPlusSamplePanel","part":"PatternInstance","limit":0}
```

Preview native additions with `genexus_wwp`:

```json
{
  "action":"add_layout",
  "name":"WorkWithPlusSamplePanel",
  "tablePath":"TableMain > TableContent",
  "expectedVersion":"<read versionToken>",
  "dryRun":true,
  "children":[
    {"type":"table","name":"TableDetails","tableType":"Responsive","children":[
      {"type":"variable","name":"Application"},
      {"type":"userAction","name":"Consult","caption":"Consult"}
    ]}
  ]
}
```

After reviewing, repeat with `dryRun:false` and the same version. Reread both the
instance and parent `WebForm`; repeat the reads after restarting the test worker.
The variable must already exist. The operation derives its primitive or Domain
binding from the native declaration and rejects retyping parameters. SDT variables
are supported by `genexus_variable`; rendering SDT controls is outside this action.

Tables and actions can be added only to a container without a nonempty
`childrenOrderedList`. The native Empty template's `TableContent` satisfies this
condition. Existing ordered containers return `WwpOrderedContainerUnsupported`
before mutation until native IDE ordering updates are independently verified.
Variable additions remain supported because they are excluded from that native
ordering list. The action never edits the list manually.

`add_layout` preserves baseline metadata and existing declarations, checks native
variable IDs in the projected controls, and rejects stale versions before saving.
It snapshots the instance, WebForm, Events and Variables. A failed pre-commit
verification aborts the transaction, then independently checks restoration. A
post-commit failure does not perform an unsafe compensating write. Inspect the
receipt rather than treating an error as evidence that nothing changed.

Raw PatternInstance XML edits remain attribute-only. Structural edits through
`genexus_edit` still return `PatternStructureChangeUnsupported`. This guard is not
removed by the typed authoring route.

## Persistence receipts

Pattern XML writes expose `saved`/`sdkSaveCompleted`, `persistedStateKnown`,
`verified`, `persistenceState` (`requested`, `partial`, `unchanged`, `unknown`),
hashes and snapshot location. `persisted` records whether the observed state
changed from its baseline; use `verified` for requested-content equality.
Unknown evidence is null, not false. Unsupported properties are rejected before
save when the installed native specification identifies them unambiguously;
dynamic or unavailable schemas still require post-save verification.

## Native regression

Run `scripts/Test-WebPanelSdtAuthoring.ps1` only with an explicitly authorized
disposable KB under `.test-kbs`, `-WorkerExe`, `-GeneXusPath`, `-KbPath`,
`-ConfirmDisposableKb`, and optional new `-EvidenceFile`. It tests individual and
batched native SDT references on WebPanel, WebComponent and Procedure, domain
preservation, rejected invalid batches, and two worker restarts. It does not run
Specify, Generate, Build, Reorg or application code.

## SDT declarations

Use qualified SDT names when resolution would otherwise be ambiguous:

```json
{"action":"add","name":"SamplePanel","variables":[{"varName":"Record","typeName":"Sample.RecordData"},{"varName":"Details","typeName":"Sample.DetailData"},{"varName":"Result","typeName":"Sample.OperationResult"}]}
```

The verifier decodes the native `ATTCUSTOMTYPE` entity type and ID and resolves
that identity in the model. A stale display label does not override a valid
native reference. An invalid native identity is rejected; matching text alone
does not certify persistence. Invalid entries abort a batch before attachment,
including variable phases of atomic object authoring.

## Templates and generated children

Pass the intended template explicitly to `genexus_apply_pattern` when first applying the pattern:

```json
{"name":"SamplePanel","pattern":"WorkWithPlus","settings":{"template":"Empty"}}
```

An unavailable template or unsupported template change is an error. The operation
must not silently substitute Empty. Changing templates on an existing instance
is not supported by this correction.

For an existing Transaction, reapplication invokes the native pattern engine for
the generated family. `PatternAppliedVerificationPending` means the native call
succeeded; `generatedChildrenVerified:false` explicitly requires fresh reads of
the affected children. It is not evidence that their requested Events or export
settings already match. If the gateway times out, retain its operation ID and
query `genexus_lifecycle` with `action:status`, then `action:result` and
`target:op:<id>` before considering another mutation. Independently reread each
expected child after terminal completion and after reopening the worker.

The layout regression also runs `scripts/Test-WwpLayoutAuthoring.ps1` against an
authorized disposable KB with compatible WorkWithPlus resources. It checks native
domain identities, lengths, visual bindings, table/button projection, user Events,
version rejection, dry-run preservation, reapplication and a new worker session.
It seeds synthetic user Events while the object is a WebPanel, then converts the
WebComponent fixture and verifies that Events remained unchanged before testing
layout authoring. Unknown setup/save outcomes stop the test without retry or cleanup.
`scripts/Test-WwpTransactionReapply.ps1` requires the installed WorkWithPlus
Transaction templates and Settings dependencies. These integration checks do not
run application Specify, Generate, Build, Reorg or real business operations.
