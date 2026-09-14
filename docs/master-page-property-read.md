# Native MasterPage property reads

`genexus_properties` resolves the GeneXus `MasterPage` property for Transaction
and WebPanel objects. The SDK exposes this value through a
`WebPanelReference`; its default `ToString()` output is only the CLR type name
and is not the selected Master Page.

The resolver reads the wrapper's native `ObjKey` and resolves it through the
active design model. That public SDK shape is the same in GeneXus 18 U11, U12,
and U16; no string parsing is involved.

## Individual read

```json
{
  "action": "get",
  "name": "OrderEntry",
  "type": "Transaction",
  "propertyName": "MasterPage"
}
```

The response keeps the existing property envelope and adds a direct
`masterPage` alias. `value`, `values.MasterPage`, `property.value`, and
`masterPage` all contain the same structured value:

```json
{
  "name": "OrderEntry",
  "type": "Transaction",
  "guid": "...",
  "path": "Sales/OrderEntry",
  "masterPage": {
    "propertyPresent": true,
    "resolved": true,
    "empty": false,
    "target": {
      "name": "ApplicationMasterPage",
      "type": "MasterPage",
      "guid": "...",
      "path": "UI/ApplicationMasterPage"
    },
    "name": "ApplicationMasterPage",
    "guid": "...",
    "path": "UI/ApplicationMasterPage",
    "verifiedByReread": true
  }
}
```

The canonical reference contract is `masterPage.target`. The direct `name`,
`guid`, and `path` fields remain as compatibility aliases for existing clients.
For a configured reference, `target.type` is `MasterPage`.

An object without a configured Master Page returns an explicit empty identity:

```json
{
  "masterPage": {
    "propertyPresent": true,
    "resolved": true,
    "empty": true,
    "target": null,
    "name": null,
    "guid": null,
    "path": null,
    "verifiedByReread": true
  }
}
```

An empty property therefore has `target: null`; it is not inferred from a
reverse dependency search.

If a non-empty SDK wrapper cannot be resolved, `resolved` is `false` and
`rawType` reports the wrapper type. A reread mismatch is not hidden:
`verifiedByReread` is `false`, and `reread` contains the second observation.

## Filtered, paged inventory

```json
{
  "action": "list",
  "type": "Transaction",
  "query": "Order*",
  "offset": 0,
  "limit": 25
}
```

`type` accepts `Transaction`, `WebPanel`, or a comma-separated combination;
when omitted, both types are included. `query` matches the object name or
qualified path as a case-insensitive substring, with `*` and `?` wildcards.
The response includes `count`, `total`, `hasMore`, `nextOffset`, `pagination`,
and `items`.
Items are ordered by type, qualified path, name, and GUID before pagination.
`catalogSource` is `index` when an already-complete in-memory catalog can page
candidates efficiently, otherwise `native`; the action never starts or rebuilds
the index. In either case, each returned `MasterPage` value is read from the
native object.

## Safety contract

Both `get` and `list` are SDK reads. They do not save any object and do not run
Specify, Generate, Build, Rebuild, compilation, reorganization, publication,
application execution, or tests. The caller's selected KB is used unchanged.
