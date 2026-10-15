# GxWire: native KB roots and per-object XML exports

`genexus_wire` needs an exported source tree. Object sources in a native KB
database cannot be read by the file indexer, including when SQL Server stores
the database outside the KB directory.

The native-root check excludes the same known root metadata files as the
indexer (`CategoryViewConfig`, `iar_objs`, `nav_objs`). These XML files do not
make a root containing `GXLOCK` or a workspace file into a source export.
An actual source file at the root still permits an export with copied soft
markers. Detection remains shallow to avoid walking generated environments.

For per-object XML exports:

- `CustomerDetails.Link(trnmode.Update, CustomerId)` targets `CustomerDetails`
  with the Link relationship. `Link(CustomerDetails, CustomerId)` stays supported;
  method arguments do not become navigation targets.
- A generic `<Object type="GUID">` uses the native SDK's `ObjClass` GUIDs
  for Procedure, Transaction, WebPanel, SDPanel, Attribute, Domain, SDT, Table
  and DataSelector. The GUID is authoritative over a filename guess or folder.
  Unknown GUIDs can use a recognized immediate type folder; otherwise existing
  filename fallback applies. There is no guess from arbitrary GUID values.
- `<Property><Name>Formula</Name><Value>computeTotal(CustomerId)</Value></Property>`
  produces `DefinesFormula` dependencies, including multiline CDATA values.
  Other properties do not count as formulas. Formula dependencies are not also
  reported as ordinary Calls at the same source location.

The index format is now version 5, forcing old cached classifications and
references to be reparsed once. This does not change the external tool schema.

Regression tests use synthetic source trees and require no native KB access.
Run `cargo test --manifest-path src/gxwire/Cargo.toml` and the Gateway's
`NativeKnowledgeBaseEvidenceTests`. Mutation validation disables the added
Link, XML formula, GUID and folder rules and verifies that the new tests fail.

Related issues: [#529](https://github.com/lennix1337/Genexus18MCP/issues/529),
[#530](https://github.com/lennix1337/Genexus18MCP/issues/530),
[#531](https://github.com/lennix1337/Genexus18MCP/issues/531),
[#532](https://github.com/lennix1337/Genexus18MCP/issues/532).
