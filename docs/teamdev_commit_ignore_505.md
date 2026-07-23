# Team Development "Ignored Objects" (commit) — how it's stored and how the MCP reads it

**Status:** SOLVED & implemented (2026-07-23, GeneXus 18.0.7). Feature: `genexus_gxserver action=ignored` + the `ignoredForCommit` flag on `action=pending`.

## The question

The GeneXus IDE's **Team Development → Commit** window has two tabs: **Pending Commits** and
**Ignored Objects**. Right-clicking an object → *"Add to 'Ignored Objects'"* moves it to the
Ignored tab, and a full commit then skips it. We wanted the MCP (which runs headless, no IDE) to
report exactly that Ignored-Objects set.

## The answer

An object is **commit-ignored** ⇔ it has a **`ModelEntityOutput` row of `OutputTypeId = 505`** in
the **design model** (`ModelId = 1`) of the KB's metadata DB. The row's *presence* is the flag
(its `OutputData` is empty). "Add to 'Ignored Objects'" writes this row; "Remove" deletes it.

Verified live on `AcademicoHomolog1`: `SELECT ... WHERE ModelId=1 AND OutputTypeId=505` returns
**exactly** the objects in the IDE's Ignored-Objects tab and nothing else (including the
`Environment` object "Desenv", `EntityTypeId=2`), and a controlled toggle of one object added/
removed precisely that one 505 row.

## How the MCP reads it (headless, no SQL, no server round-trip)

`Artech.Architecture.Common.Objects.KBModel` derives from `Artech.Udm.Framework.Model`, which
exposes:

```
bool LoadLastEntityOutput(EntityKey key, int outputTypeId, out DateTime ts, out byte[] data)
```

So for each object returned by `ITeamDevClientService.GetLocalChanges(designModel)` (the Common
service, already wired), call `LoadLastEntityOutput(key, 505, …)`; `true` ⇒ commit-ignored,
`false` ⇒ committable. Implemented in `GxServerSyncService.IsCommitIgnored` (reflection, cached
`MethodInfo`, `const int CommitIgnoreOutputTypeId = 505`).

### Why not the "clean" API

The **UI.Framework** `Artech.Architecture.UI.Framework.Services.ITeamDevClientService` exposes the
ideal reads — `GetIgnoredForCommit() : IEnumerable<EntityKey>`, `GetPendingForCommit()`,
`IsIgnoredForCommit(key)`, `IgnoreForCommit`/`EnableForCommit`, `GetActiveChangelists()`. But that
service **does not resolve in the headless worker** (live-checked: the type loads,
`TryGetService` returns null). It's the UI-package service, not registered without the IDE shell.
The **Common** `ITeamDevClientService` (the one we already use) has *no* commit-ignore read — only
the `IgnoreForCommit`/`EnableForCommit`/`IsInsertedForCommit` members, none of which reads the
ignore state (`IsInsertedForCommit` tracks `operation==Inserted`, not ignore). That absence is why
the state was hard to find. If a future SDK build registers the UI.Framework service headless
(or `SdkServiceLocator.ConstructOrResolve` can build its concrete impl), prefer
`GetIgnoredForCommit()` — it owns the 505 constant and is version-proof.

## Dead ends (do not re-investigate)

All measured live; none distinguishes ignored from committable:

- **Common-service reads:** `IsInsertedForCommit` (= operation Inserted), `ShouldCommitObject`
  (true for all, both `useComparer` values), `GetChangelists` (empty), `GetLockedObjects`/
  `GetForcedObjects`/`IsUnlockKbObject`/`ShouldLockObject` (empty/false),
  `GetLastSynchedObject` (present for all — every local change has `curVer > synVer`).
- **`PrepareCommit(model, td).Items`** returns **all** local changes even with an authenticated
  `TeamDevelopmentData` (OAuth token acquired, server reached) — it never applies the ignore
  filter, and `Items` has no per-item ignore flag.
- **`GetServerObject` per-object server compare** — **hangs** (wedged the worker ~10 min).
- **Version-table skew** (design `ModelEntityVersion` vs the `.Net`/Desenv environment model
  version) briefly split the set but is *generation-lag* (design edited but not yet specified
  into the environment; the `Desenv/state/state15_2_*.ari` files) — transient, flips on build.
- **Metadata-DB scans:** broad exact-match of every guid/string column for the ignored objects'
  GUIDs found them only in the `Entity` catalog; substring/blob scan of `KnowledgeBaseInformation`
  found nothing. (The 505 flag is in `ModelEntityOutput`, keyed by integer `EntityId` +
  `OutputTypeId`, which those scans didn't key on.)
- **KB files / user profile:** nothing in `.gx`, `*.workspace`, `model.ini`, `nav_objs.xml`,
  `%APPDATA%/GeneXus`, `%LOCALAPPDATA%`.

## How it was reverse-engineered (reproducible recipe)

The toggle writes nothing to the .mdf *at toggle time that a naive check catches* and makes **no
server call**, so two techniques cracked it:

### 1. Prove it's local (HTTPS capture of the IDE ↔ GXserver traffic)

`pip install mitmproxy` (a user Python is enough — no admin). Then:

```
mitmdump -p 8080 --ssl-insecure -w flows.mitm "~d <gxserver-host>"
```

Trust the mitm CA in the **CurrentUser** root store (no admin; the user clicks the one
confirmation dialog):

```
certutil -user -addstore -f Root %USERPROFILE%\.mitmproxy\mitmproxy-ca-cert.cer
```

Point the IDE's .NET HTTP through the proxy via the per-user WinINET proxy
(`HKCU\…\Internet Settings` → `ProxyServer=127.0.0.1:8080`, `ProxyEnable=1`); GeneXus (.NET
Framework) honors it on new connections. Trigger the action in the IDE, then parse:

```python
from mitmproxy.io import FlowReader
for fl in FlowReader(open("flows.mitm","rb")).stream(): ...
```

Findings: GXserver endpoint is `POST /<inst>/TeamWorkService2.svc/secure` (plaintext MTOM/SOAP
1.2; ops `GetVersions`/`GetRevisions`/`WhatsUp`/…). The **ignore toggle produced zero traffic** ⇒
local. **Tear down afterward** (security): revert the proxy keys, `certutil -user -delstore Root
<thumbprint>`, and delete `%USERPROFILE%\.mitmproxy`.

### 2. Locate the local store (metadata-DB before/after diff)

Connect to the KB's SQL Server (`knowledgebase.connection` → `ServerInstance` / `DBName`,
integrated security). Fingerprint every table with a checksum, toggle the flag in the IDE + close
the Team Development tab (flush), re-checksum, diff:

```sql
DECLARE @s nvarchar(max)=N'';
SELECT @s=@s+'SELECT '''+name+''' t, CAST(CHECKSUM_AGG(BINARY_CHECKSUM(*)) AS bigint) ck, COUNT(*) n FROM ['+name+'] UNION ALL ' FROM sys.tables;
EXEC sp_executesql (LEFT(@s,LEN(@s)-10));
```

Only `ModelEntityOutput` changed (+1 row); isolating it gave `OutputTypeId=505`. Confirm:

```sql
SELECT o.EntityTypeId, o.EntityId, p.ModelEntityPropertyValueShort AS name
FROM ModelEntityOutput o
LEFT JOIN ModelEntityProperty p
  ON p.ModelId=1 AND p.ModelEntityPropertyId=1 AND p.EntityTypeId=o.EntityTypeId AND p.EntityId=o.EntityId
WHERE o.ModelId=1 AND o.OutputTypeId=505;
```

## Implementation

- `src/GxMcp.Worker/Services/GxServerSyncService.cs` — `action=ignored`, `ignoredForCommit` on
  `action=pending`, `IsCommitIgnored` / `LocalChangeType` / `EnumIgnoredForUpdate`,
  `IgnoredEnvelope` fallback.
- `src/GxMcp.Gateway/tool_definitions.json` + discovery golden fixture — `ignored` action.
- Tests: `GxServerSyncServiceTests.{Ignored_NoMetadata_…, Ignored_WithDotGxState_…, Run_IgnoredAction_…}`.

The **Update-ignore** list (a separate concept — IDE Update tab) *is* on the Common service
(`GetIgnoredObjectsForUpdate(model)`), surfaced as `updateIgnored` in the same response.
