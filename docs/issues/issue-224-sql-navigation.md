# Issue #224 — GX17 SQL navigation parsing

## Symptom

On GX17 navigation reports, `genexus_db action=sql_navigation` used the inner
text of each XML node as if it were a predicate. A node containing an
`Attribute`, description, token, and variable therefore became a concatenated
string, and the `LoopWhile/NotEndOfTable` marker could be emitted as a second
SQL condition.

## Contract

- Read the attribute name from `Attribute/AttriName` (with compatibility
  fallbacks), never from the container's full inner text.
- Read the operator from `Operator`/`Op` or a recognized `Token`.
- Read bind values from `Value` or `Variable/VarName`.
- Ignore control-flow-only conditions that do not contain a SQL predicate.
- Keep the existing read-only response shape, level filtering, and bind
  parameter reporting.

## Implementation

`NavigationService` now parses `OptimizedWhere` descendant `Condition` nodes
through a small semantic-field parser. The resulting `NavigationFilter` is
then consumed by the existing `NavigationReport.GenerateSql` path, so SQL
generation remains deterministic and no database or Knowledge Base mutation
is involved.

## Validation

- GX17-shaped parser regression: passed.
- SQL projection regression: passed with one predicate and one expected bind.
- Full Worker suite: 2,822 passed / 4 skipped.
- SDK compatibility validation used `C:\Genexus\GeneXus18U16`; the reported
  fingerprint drift is patch/build drift and the major remained compatible.
- No Knowledge Base was selected, changed, specified, generated, built,
  rebuilt, deployed, published, executed, or tested through a live KB.
