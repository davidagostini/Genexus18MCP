# Pattern apply / reapply — pitfalls and diagnostics

`genexus_apply_pattern` attaches a pattern (typically `WorkWithPlus`)
to a parent KBObject. Subsequent calls with `reapply=true` re-run the
generator against the existing PatternInstance.

## Initial apply vs reapply

| Scenario                          | Call                                                              |
| --------------------------------- | ----------------------------------------------------------------- |
| First time on a Transaction       | `genexus_apply_pattern name=<Trn> pattern=WorkWithPlus`           |
| First time on a WebPanel/WebComponent/SDPanel | `... settings={"template":"EmptyWithTitle"}`             |
| Re-generate after PatternInstance edit | `... reapply=true`                                           |
| Diagnose without mutating         | `... mode=diagnose`                                               |
| Apply + verify build              | `... validate=true` (adds 60–180s build step)                     |

The host is named `WorkWithPlus<ParentName>` by convention. For
WebPanel parents you must pass `settings.template` — the default
template (`TransactionPopUp`) fails on non-transaction parents.

## Reapply silent-failure mode

Before v2.6.11 a reapply that produced src0265 / src0216 diagnostics
during Events regeneration returned `status=Success` while the
generated parent had compile errors. The current MCP runs
`SdkDiagnosticsHelper.GetDiagnostics(parent)` post-projection and flips
the envelope to `status=PartialFailure` + `patternValidationIssues[]`
when any are found.

**LLM behavior**: when you see `status=PartialFailure`, do NOT proceed
to build. Inspect `patternValidationIssues` first, fix the
PatternInstance, then reapply.

Common src codes after a reapply:

| Code      | Meaning                                            | Fix                                           |
| --------- | -------------------------------------------------- | --------------------------------------------- |
| `src0265` | Invalid attribute / variable reference             | Drop the dead reference from Events           |
| `src0216` | Reference to undefined event / sub                 | Generate the sub via PatternInstance reapply  |
| `src0035` | Type mismatch on assignment                        | Align variable type with the source           |

## Apply-on-save flag

WWP has a per-PatternInstance "Apply this pattern on save" IDE
checkbox. When unchecked, Ctrl+S persists PatternInstance but does NOT
regenerate the host's WebForm/Events. The MCP exposes the
`SDPlus_Editor_Apply_On_Save` property via `genexus_properties` but
the canonical IDE store for this flag is not reliably writable across
GeneXus 18 versions — set it once via the IDE if a reapply isn't
producing the expected host changes.

## Detecting a reapply that needs a host rebuild

After a successful reapply, the host's Events part is regenerated.
Always follow with:

```
genexus_lifecycle action=build target=WorkWithPlus<Name> wait_until_done=true
```

…to surface any latent binding errors. The reapply envelope alone does
not guarantee a working build.

## Template choices for WebPanel hosts

| `settings.template`   | Use case                                                       |
| --------------------- | -------------------------------------------------------------- |
| `EmptyWithTitle`      | Generic popup / standalone panel with title bar                |
| `TransactionPopUp`    | Default — Transaction parents only; fails on WebPanel          |
| `Selection`           | List-style selector popup                                      |
| `View`                | Read-only view panel                                           |

If unsure, start with `EmptyWithTitle` for popups and the default for
Transactions.

## Discarding a bad apply

```
genexus_versioning action=history_restore discard=true target=<ParentName>
```

Restores the pre-apply snapshot. Use this instead of trying to undo
the apply by editing PatternInstance back to empty.
