# Typed WorkWithPlus form-level UserAction

## Problem

Editing a WorkWithPlus `PatternInstance` with a generic `native_genexus_edit`
patch can locate a form action container such as `TableActions`, but the generic
serializer rejects the structural insertion with `PatternStructureChangeUnsupported`.

## Supported operation

Use the typed WWP operation through either `genexus_wwp` or
`genexus_apply_pattern` with `mode: "actions"`:

```json
{
  "action": "add_user_action",
  "name": "WorkWithPlusImpressaoConfiguracao",
  "containerName": "TableActions",
  "actionName": "BaixarConfiguracao",
  "caption": "Baixar Configuração",
  "dryRun": true,
  "rollbackOnFailure": true
}
```

For `genexus_apply_pattern`, also pass `"pattern": "WorkWithPlus"` and
`"mode": "actions"`.

The operation writes a direct `<userAction name="BaixarConfiguracao"
caption="Baixar Configuração" />` child of the requested form container. The
GeneXus event is derived as `DoBaixarConfiguracao`; no invented `event` XML
attribute is written and no Procedure is required or accepted for this
form-level action.

## Persistence and safety contract

- `dryRun: true` parses the current `PatternInstance` and returns a typed
  before/after diff without writing.
- `dryRun: false` saves only the `PatternInstance` through the existing WWP
  write path, re-reads it, projects the form container, and verifies the
  requested action is present.
- `rollbackOnFailure` defaults to `true`. A failed save or post-save
  verification attempts to restore the exact pre-write XML and reports whether
  the restore was verified.
- `baseVersion`, `expectedVersion`, or `versionToken` can be supplied from a
  previous read or dry run to reject stale edits.
- The operation does not invoke Specify, Generate, Build, Rebuild, compilation,
  reorganization, publication, execution, or tests. It does not create
  security permissions.
- Existing `childrenOrderedList` metadata is left untouched; engine-managed
  ordering is not authored by the MCP.

After a successful save, call `genexus_wwp` with `action: "list"` to confirm
the form container and its action. Reapplying WorkWithPlus is a separate
explicit operation; the direct typed UserAction is kept in the PatternInstance
contract so it remains available to the pattern projection.

## Expected response fields

Successful writes include `saved: true`, `persisted`, `diff`, `containerName`,
and `event: "DoBaixarConfiguracao"`. Failed writes include `rollback` with
`attempted` and `rolledBack` fields. A missing container returns
`FormActionContainerNotFound` and lists the containers detected in the
PatternInstance.

## Test fixture scope

The automated tests use an in-memory PatternInstance containing `TableActions`;
they do not open or modify a customer KB. A live acceptance check may target
the requested KB only after reviewing the dry-run diff and should verify the
persisted PatternInstance with a subsequent read.
