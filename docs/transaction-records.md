# Typed Transaction records

The `genexus_db` umbrella exposes three root-level Transaction record actions:

- `records_query` reads rows using Transaction attributes and equality filters.
- `records_insert` previews or inserts one row.
- `records_update` previews or updates one primary-keyed row.

The adapter obtains the table name, root attributes, types, lengths, decimals,
and primary key from the SDK Transaction structure. Attribute names are resolved
against that metadata and values are sent as database parameters; callers cannot
provide SQL identifiers or predicates.

Writes are read-only by default. A persisted write requires a `versionToken`
returned by a matching read or dry-run. The write path rechecks the token inside
a serializable database transaction, captures the complete affected-row
snapshot, writes, rereads before commit, commits, and rereads again through a
new connection. If the committed state diverges, the adapter attempts a
compensating restore only while the affected rows still match the requested
mutation, then verifies the restored snapshot. A concurrent change is never
silently overwritten.

Example preview:

```json
{
  "action": "records_update",
  "transaction": "SampleIntegration",
  "where": { "ProcessCode": "DEMO001", "Provider": "ProviderV3" },
  "values": { "CommunicationId": 42 },
  "dryRun": true,
  "rollbackOnFailure": true
}
```

The response contains `persisted=false`, the typed diff, the matched keys, and
the token to use for an explicitly authorized write. Insert and update results
return reread records and their key values, which supports chaining a generated
communication key into a related integration row.

This capability does not call Specify, Generate, Build, Rebuild, Compile,
Reorg, publication, execution, or tests. It operates on existing physical data
only; it does not create or alter GeneXus objects or database schema.
