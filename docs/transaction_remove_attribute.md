# Removing an Attribute reference from a Transaction

`genexus_edit mode=ops` persistently removes one native `TransactionAttribute`
from a Transaction Structure. It does not delete the KB-global Attribute or
change its SubType Group membership.

```json
{
  "name": "Invoice",
  "type": "Transaction",
  "part": "Structure",
  "mode": "ops",
  "ops": [
    {
      "op": "remove_attribute",
      "args": {
        "name": "InvoiceLegacyCode"
      }
    }
  ],
  "dryRun": true,
  "baseVersion": "639221476170000000",
  "rollbackOnFailure": true
}
```

Use `level="root"` (the default), an unambiguous subordinate level name, or
`levelPath` inside the operation's `args` for nested levels. A removal request
must contain exactly one `remove_attribute` operation; mixed add/set batches
continue through the general Structure DSL writer.

`dryRun=true` returns the expected before/after attribute order and removal
diff without saving. `baseVersion` is the optimistic-concurrency token returned
by a prior preview/read response; a mismatch returns `StaleObject` before any
mutation.

An effective write snapshots every Transaction part, detaches the exact native
reference, saves the Transaction and performs a fresh read. Success requires all
of these checks:

- the reference is absent from the requested level;
- every other Structure identity, property, level and relative order is unchanged;
- the KB-global Attribute still has the same GUID and native serialized hash;
- all SubType Group memberships are identical;
- all user-authored non-default Transaction parts are unchanged.

With `rollbackOnFailure=true` (the default), any save or verification failure
restores the complete pre-write Transaction snapshot and reports
`rolledBack`/`rollbackVerified`. A success response returns `AttributeRemoved`,
`persistedVerified=true`, the snapshot path, a new `versionToken`, and the
verifiable diff under `result.diff`.
