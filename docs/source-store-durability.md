# Durable source-store cache

The source cache and its backfill cursor use a durable identity derived from the
KB ID, normalized physical path and context generation. Restarting an isolated
Gateway or Worker no longer adds the Gateway process scope to this cache key.
Changing the KB identity, path or generation selects a different cache. Jobs,
journals and mutation receipts keep their existing operational scope.

The default location is `%LOCALAPPDATA%/GenexusMCP/source-store/<identity-hash>`.
With `GXMCP_STATE_DIR`, the location is `<configured-root>/source-store/<identity-hash>`;
the override still separates KBs. A Worker without a complete KB identity keeps
an isolated operational key rather than sharing a standalone durable store.

One Worker owns each durable directory through an exclusive OS file handle.
A competing isolated Worker uses SDK reads without writing that cache or its
cursor; `sourceStore.notStartedReason=storeOwnedOrUnavailable` explains this
condition. Restart the competing Worker after the owner exits to reuse the
cache. The OS releases ownership if a process crashes. Store shutdown flushes
the catalog before releasing ownership. This cache lock does not authorize or
serialize KB writes.

Record, catalog and checkpoint replacements keep the previous file until the
new file is ready. Checkpoints are written under the same ownership gate after
flushing the catalog; a catalog flush failure refuses the advanced checkpoint
so a restart can replay from the prior cursor. Writes already in flight finish
before shutdown releases the owner handle, and late timer callbacks cannot
write under the next owner's lock.

Backfill is scheduled when the index becomes Ready. SDK source reads remain
bounded STA slices. `state=off` is preserved for compatibility and the additive
`notStartedReason` distinguishes configuration, readiness and unavailable
storage. A current refreshed index can confirm that a queued GUID was deleted:
the cache drops its parts, increments `removedObjects` and advances the cursor.
An unresolved SDK lookup, stale index or index refresh cannot certify deletion
and continues to return an error without advancing the cursor.

## Retiring old process-scoped stores

Old `%LOCALAPPDATA%/GenexusMCP/state/<operational-hash>/source-store` caches are
not automatically imported or removed: their process scope is not evidence of
the KB identity. They can be removed manually after stopping all Gateways and
Workers that used the selected scope. Preserve any evidence needed first,
verify the exact absolute directory and delete only its `source-store`
subdirectory. Keep the sibling jobs, journals and recovery files. Do not remove
an active store, guess identity from folder age, or delete the complete state
root. Removing an unused cache does not change the KB; an older Worker would
rebuild it on its next start.

## Validation

`SourceStoreDurabilityTests` checks identity changes and root overrides,
exclusive ownership, persisted records/cursor across independent store
instances, confirmed deletion and unresolved-read refusal. Ready notification
tests verify state/catalog publication before the callback. Process-level
tests additionally check exclusive ownership and preserved records/cursor after
an abrupt owner-process exit. Concurrency regressions cover lock order,
in-flight writes, late callbacks and failed checkpoint/catalog persistence.
Any unavailable native-SDK lane is recorded separately in the PR.
