# Issue #221 — XPZ import preflight

## Problem

`genexus_transfer action=import` could receive a damaged or incomplete XPZ that
the GeneXus SDK still projected as an `IExportItem`. The projection was enough
for `ImportFile` to run, even when the package contained only an object record
and empty parts. The result was a hollow object created or overwritten in the
Knowledge Base, while the response could say that no import was attempted.

## Change

The destructive path now reads the XPZ package itself before calling
`IKnowledgeManagerService.ImportFile`:

- malformed, unreadable, unnamed, or object-less packages are rejected;
- every object record must have at least one non-empty part payload;
- the preflight is bounded to the same XML entry and total-size limits used by
  fidelity inspection and disables DTD resolution;
- rejection returns `TransferImportVerificationUnavailable` and explicitly
  states that no import was attempted;
- `action=inspect` and `action=import dryRun=true` expose package object names,
  types, part counts, payload presence, and a `preflight.validForImport` flag;
- SDK `ExploreExport` results remain available as a fallback, but no longer
  provide the object labels by themselves.

The check is performed after confirmation and import-option validation, but
before `PrepareImport`, fidelity capture, or `ImportFile`. It does not select,
open, modify, specify, generate, build, or publish a Knowledge Base.

## Validation

- `ReadExportPackageSummary_ReportsObjectNamesTypesAndPayload`
- `ValidateImportPackage_RejectsObjectWithoutPayloadBeforeMutation`
- `ValidateImportPackage_RejectsMalformedObjectRecordWithoutName`
- Existing XPZ WebForm source and namespace tests remain covered.

The preflight uses sanitized package metadata only; no customer object names or
Knowledge Base paths are included in this document.
