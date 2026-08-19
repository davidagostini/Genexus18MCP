# Native Data View authoring

`genexus_data_view` creates a root-only Transaction Business Component whose logical table is mapped by a native GeneXus Data View to an existing physical table. It is intended for narrow update contracts that must not load or save unrelated child levels.

## Safe workflow

Start with `action=dry_run`. The tool resolves the existing table metadata and data store, requires every global Attribute to exist in that table, verifies stored types by Attribute identity, and requires the complete physical primary key. It does not construct a GeneXus SDK object or call `Save`.

```json
{
  "action": "dry_run",
  "transaction": "LedgerEntryView",
  "dataViewName": "LedgerEntryDV",
  "dataStore": "Default",
  "schema": "APP",
  "table": "LEDGERENTRY",
  "updatable": true,
  "attributeMappings": [
    { "attribute": "LedgerEntryId", "column": "LedgerEntryId", "key": true },
    { "attribute": "LedgerEntryAmount", "column": "LedgerEntryAmount" }
  ],
  "rollbackOnFailure": true
}
```

The preview returns `persisted=false`, `mutationDetected=false`, `newTables=[]`, `reorgRequired=false`, an empty `ddl` array, and a `version` token. Pass that token as `expectedVersion` to `action=create`. A mismatched token returns `ConcurrentModification` without saving.

Creation saves the Transaction and Data View in one SDK transaction. The Transaction contains exactly the mapped root Attributes, its Business Component property is enabled, and the Data View is associated with the Transaction's logical table while its platform properties point at the requested physical schema/table. After commit, the service rereads both objects and verifies the root structure, BC property, association, column mappings, and physical mapping.

`action=update` changes Data View mappings/properties only when the persisted root Attribute sequence is unchanged. Structural replacement is refused so an update cannot silently widen the Business Component. `action=delete` first proves that the Data View belongs to the Transaction, then removes the pair atomically without deleting global Attributes or the existing physical table. For every action, `dryRun=true` is read-only; on delete it returns the exact deletion preview without removing anything.

No action calls Specify, Generate, Build, Rebuild, Reorg, compilation, publishing, execution, or tests. The reported reorganization preview is derived from the verified Data View association and never executes reorganization analysis or DDL.
