# Moving an existing Transaction attribute

`genexus_structure action=move_attribute` reorders an existing native
`TransactionAttribute`. It does not delete or recreate the KB-global Attribute.

```json
{
  "action": "move_attribute",
  "name": "SampleTransaction",
  "attribute": "SampleSubtypeId",
  "after": "SampleReferenceId",
  "level": "root",
  "dryRun": true
}
```

Use exactly one of `before`, `after`, or zero-based `position`. `level` accepts
`root` or an unambiguous subordinate level name; use `levelPath`, for example
`["Item", "Operation"]`, for nested or repeated level names. `module` can
disambiguate Transactions, and `baseVersion` accepts a `versionToken` returned
by a prior read.

An effective call snapshots every Transaction part, moves the same SDK member,
saves the Transaction, re-reads it, and verifies the requested position, native
attribute identities, properties, level membership, the relative order of all
other members, and all user-authored non-Structure parts. A failed save or
verification restores the snapshot and reports the rollback result.

Default forms may be recalculated by GeneXus as projections of the reordered
Structure. User-authored (`IsDefault=false`) WebForm/WinForm content is verified
and cannot change silently.

The action never runs Specify, Generate, Build, Rebuild, reorganization, or
Pattern application. In particular, it ignores `validationMode`; run a separate
explicit lifecycle action only when desired.
