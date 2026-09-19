# Live-KB validation matrix

This matrix is the acceptance surface for mutating MCP tools. Contract and Gateway tests are necessary but do not prove GeneXus SDK persistence; rows marked Live require a real fixture KB and a fresh read-back.

| Capability | Primary tool/actions | Required live assertion | Rollback/cleanup |
| --- | --- | --- | --- |
| Create | `genexus_create` object/object_atomic | Object exists with requested type and initial parts | Delete fixture object or restore snapshot |
| Edit source | `genexus_edit` Source/Rules/Events/Variables | Exact source persists after fresh `genexus_read` | Restore original part |
| Properties | `genexus_properties` set/move | Typed value and version token survive reopen | Restore previous value |
| Structure | `genexus_structure` update_visual/set_attribute/set_level | Structure and attribute identity are preserved | Restore structure snapshot |
| Layout | `genexus_layout` set_property/add/delete printblock | Visual part read-back matches intended control/property change | Restore layout snapshot |
| Refactor | `genexus_refactor` rename/extract | Target and references resolve under new identity | Restore from version snapshot |
| Versioning | `genexus_versioning` save/restore/undo | History entry and restored source are consistent | Remove temporary history branch |
| Lifecycle | `genexus_lifecycle` validate/build/rebuild/index | Terminal operation state, SDK exit evidence, and index state are truthful | Reopen KB and clear operation state |

## Mandatory edge cases

- Homonymous object names must require `type` or a unique identity.
- A stale `versionToken` must fail closed without mutating the KB.
- A timeout during mutation must return an operation handle and block blind replay until read-back.
- A failed write must leave the original source/structure unchanged.
- A Gateway/Worker restart must not leak selection, cache, journal, or operation state across KB aliases.
- Two independent KBs with the same object name must never cross-resolve.

## Execution protocol

1. Start the Gateway with an isolated scratch configuration and the declared fixture KB.
2. Run the read-only preflight: `genexus_whoami`, `genexus_kb action=list`, health, index status, and object identity checks.
3. Capture the original authoritative part before each mutation.
4. Run the smallest mutation with an explicit idempotency key and, where supported, dry-run first.
5. Read back through a fresh MCP request; do not trust the mutation response alone.
6. Exercise reopen/reload when persistence is the assertion.
7. Restore the fixture in a `finally` path and record terminal evidence.

The automated live suite must skip with an explicit unavailable-fixture result when GeneXus or the fixture KB is not present; it must never claim a live pass from contract-only coverage.
