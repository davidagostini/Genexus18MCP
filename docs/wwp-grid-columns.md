# Typed WorkWithPlus grid columns

`move_grid_column` moves an existing attribute/variable immediately before another
column in the same grid. Omit `before` to update only `caption`.
`add_grid_variable` creates a read-only presentation column, without adding a table
attribute. It accepts `Character` or `VarChar` with a length from 1 to 9999.

Both actions require `expectedVersion` from a complete PatternInstance read, even
for `dryRun`. Existing `baseVersion`/`versionToken` aliases remain accepted.
`gridPath` is an absolute path of element types; optional `[n]` indices are
zero-based among siblings of the same type. Ambiguous paths or column names fail
without guessing. Parent and host are resolved by SDK identity, not name stripping.

```json
{"action":"move_grid_column","name":"WorkWithPlusSamplePanel","gridPath":"/instance/level/selection/table/grid","attribute":"SampleProcessed","before":"SampleMachine","caption":"Processing","expectedVersion":"<read-token>","dryRun":true}
```

```json
{"action":"add_grid_variable","name":"WorkWithPlusSamplePanel","gridPath":"/instance/level/selection/table/grid","variable":"RestartDisplay","basicType":"VarChar","length":120,"caption":"Restart","expectedVersion":"<fresh-read-token>","dryRun":true}
```

Preview performs no SDK mutation or save. Saving captures PatternInstance,
WebForm, Events and Variables evidence and in-memory snapshots of all object
parts. Native element commands execute in one SDK transaction with normal save
validation and concurrency checks enabled. Automatic pattern application is not
disabled. No explicit apply/build callbacks, force-save fallback, generated-code
edit, or second write is performed.

The pre-commit checks compare pattern metadata, column order/caption/binding,
variable declarations and all unaffected content. Failure aborts the transaction.
After disposal, fresh SDK objects by GUID provide the final persistence evidence.
The receipt separates object-save return, commit completion, requested persistence,
known state and verified rollback. A post-commit divergence cannot safely be
compensated without an atomic version-conditional restore: it reports
`AtomicRollbackUnavailable` and retains snapshots instead of overwriting newer work.
`persisted` means that the requested state was confirmed; `saved` and
`commitCompleted` retain physical commit evidence even when that comparison fails.
The pre-commit check is an in-transaction projection gate, not persistence proof.

The projected WebForm verifier uses native `gxGrid`/`gxColumn`, `ColAttId` and
`ColTitle`. Unknown representations and nonempty title expressions fail closed.
Events are currently required to remain exactly unchanged, including generated
blocks. No Grid.Load code is authored by these actions. Multiple projected grids
require a verifiable anchor when adding a column.

These are synchronous actions. After an ambiguous result, recovery requires a
complete versioned PatternInstance read; reading Source alone cannot clear its
journal fence. Do not automatically repeat the write.

MCP-only tests do not prove that a particular installed WorkWithPlus version
projects synchronously inside the transaction or preserves baseline metadata.
Live acceptance on an authorized disposable KB remains necessary before operational
promotion. No real KB writes or GeneXus lifecycle tests were run for this change.

For a restart display, a legacy log without a timestamp means “restart recorded,
time unavailable”; absence of that message means no recorded restart. A current
completion timestamp that can be overwritten by another execution is not historical
restart evidence. Logging a timestamp for future events and populating the display
variable are separate application changes; these actions do not invent past times.

## Other typed WWP examples

These examples live here to keep discovery within its token budget.

```json
[
  {
    "action": "list",
    "name": "SampleOrderWW"
  },
  {
    "action": "add_action",
    "name": "SampleOrderWW",
    "group": "Operations",
    "actionName": "Retry",
    "procedure": "RetrySampleOrder",
    "selection": "multiple",
    "dryRun": true
  },
  {
    "action": "add_tab",
    "name": "SamplePanel",
    "controlName": "Details",
    "title": "Details",
    "children": [
      {
        "type": "variable",
        "name": "Status",
        "basicType": "VarChar",
        "length": 40
      },
      {
        "type": "userAction",
        "name": "Refresh",
        "caption": "Refresh"
      }
    ],
    "dryRun": true
  }
]
```
