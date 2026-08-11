# Plan 071: Add issue-#59 post-save verification to `GroupStructureService`

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat e756dd2..HEAD -- src/GxMcp.Worker/Services/Structure/GroupStructureService.cs src/GxMcp.Worker/Services/Structure/DomainWriteService.cs src/GxMcp.Worker.Tests/GroupStructureServiceTests.cs src/GxMcp.Worker.Tests/Issue47To50SdtAndPlacementTests.cs`
> If any changed since this plan was written, compare the "Current state"
> excerpts against live code before proceeding; on a mismatch, STOP.

## Status

- **Priority**: P3
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: correctness (false-success write confirmation)
- **Planned at**: commit `e756dd2`, 2026-08-10

## Why this matters

Issue #59 established the project's post-write verification standard: after any
mutating write, re-read the affected value and compare what was REQUESTED against
what PERSISTED, and on mismatch return a structured `*NotPersisted` error instead of
a false success. The new write paths added in v2.38–v2.39 all follow it —
`DomainWriteService.VerifyEnumValuesPersisted`, `WwpActionService`'s re-read +
`WwpActionNotPersisted`, `WriteService.Variables` verify helpers, `PersistenceVerifier`.

`GroupStructureService.UpdateGroupStructure` (v2.37.0, issue #54) is the exception:
it calls `group.EnsureSave()` + `sdkTrans.Commit()` and returns `GroupUpdated`
without re-reading `GroupStructurePart.Members`. If the SDK save silently drops a
membership write (the same failure class `DomainWriteService` guards against — the
SDK can persist without throwing), the tool reports success for a change that never
landed. The Group member-bind path is exactly the kind of SDK behavior that can
"apply" in memory but not persist (see the issue #55/#79 notes on enum/SDT writes
needing dirty-forcing + verification).

## Current state

`src/GxMcp.Worker/Services/Structure/GroupStructureService.cs:131-190` (excerpts):

```csharp
using (var sdkTrans = group.Model.KB.BeginTransaction())
{
    try
    {
        if (members != null)
        {
            foreach (var m in members)
            {
                // ... validate name/subtypeOf, resolve Attribute objects ...
                var existing = part.Members.FirstOrDefault(mm => mm.Subtype != null
                    && string.Equals(mm.Subtype.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    existing = new GroupMember(part) { Subtype = subtype };
                    part.Members.Add(existing);
                }
                existing.Supertype = super; // writes Subtype.SuperType
                applied.Add(new JObject { ["name"] = name, ["subtypeOf"] = superName });
            }
        }
        if (remove != null)
        {
            foreach (string rn in remove.Select(r => r.ToString()))
            {
                var victim = part.Members.FirstOrDefault(...);
                if (victim == null) { sdkTrans.Rollback(); return ...; }
                part.Members.Remove(victim);
                removed.Add(rn);
            }
        }

        group.EnsureSave();
        sdkTrans.Commit();                       // <-- no re-read of Members

        try { _objectService.GetKbService().GetIndexCache().UpdateEntry(group); }
        catch (Exception iex) { ... }

        return Models.McpResponse.Ok(target: groupName, code: "GroupUpdated", result: new JObject
        {
            ["group"] = group.Name,
            ["members"] = applied,
            ["removed"] = removed
        });
    }
    ...
}
```

The read path already exists to verify against — `GetGroupStructure`
(`GroupStructureService.cs:192-231`) walks `part.Members` and projects
`{ name = member.Subtype.Name, subtypeOf = member.Supertype?.Name }`.

The reference implementation to mirror: `DomainWriteService.SetDomainProperties`
(`src/GxMcp.Worker/Services/Structure/DomainWriteService.cs:99-186`) —
`VerifyEnumValuesPersisted` re-finds the object (`_objectService.FindObject`), walks
the persisted set, compares against requested with a normalization-aware match, and
returns the structured `DomainUpdateNotPersisted` envelope on mismatch.

### Convention

C# (.NET Framework 4.8, x86), match the surrounding file style. The verification
helper should be a pure comparison (testable without an SDK) like
`PersistenceVerifier` — feed it plain lists of `(name, subtypeOf)` tuples, not SDK
objects. Tests: xUnit, reflection into `internal`/`private` statics is the
established pattern (see `Issues70To72RegressionTests`).

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build worker | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Run one test file | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~GroupStructureServiceTests"` | all pass |
| Full worker tests | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj` | ~1,790 pass (known flaky pair as in plan 068 — treat isolated green as pass) |

## Scope

**In scope**:
- `src/GxMcp.Worker/Services/Structure/GroupStructureService.cs` — post-commit
  verification of `GroupStructurePart.Members` against the requested
  members/removed sets; `GroupUpdateNotPersisted` error envelope on mismatch.
- `src/GxMcp.Worker.Tests/GroupStructureServiceTests.cs` — new test file for the
  pure verification helper (create it; there is no existing one).

**Out of scope**:
- Changing how the write itself happens (`EnsureSave`, transaction, dirty-forcing) —
  only add verification. If verification *reveals* a persistence bug, report it,
  don't fix the SDK write path in this plan.
- `GetGroupStructure` — unchanged (it's the read path the verifier reuses).
- `DomainWriteService` — reference only.
- Any schema/tool-definition change.

## Git workflow

- Branch: `advisor/071-group-structure-persistence-verification`
- Commit style: `fix(worker): verify group-membership writes persist (issue-#59 standard)`
- Do NOT push or open a PR unless instructed.

## Steps

### Step 1: Extract a pure member-diff helper

In `GroupStructureService.cs`, add an `internal static` helper (no SDK types in the
signature) that compares requested vs persisted members and returns the drift, so it
can be unit-tested without a live KB:

```csharp
// Issue-#59 post-save verification. Pure comparison: `persisted` comes from a
// re-read of GroupStructurePart.Members, `requested` from the parsed payload.
// Returns a JArray of drift entries (one per missing/extra/mismatched member),
// empty when the write is confirmed. Mirrors DomainWriteService.BuildDiff.
internal static JArray BuildMemberDiff(
    System.Collections.Generic.IEnumerable<(string Name, string SubtypeOf)> requested,
    System.Collections.Generic.IEnumerable<(string Name, string SubtypeOf)> persisted)
{
    var diff = new JArray();
    var persistedList = persisted.ToList();
    foreach (var r in requested)
    {
        var match = persistedList.FirstOrDefault(p =>
            string.Equals(p.Name, r.Name, StringComparison.OrdinalIgnoreCase));
        if (match.Name == null)
        {
            diff.Add(new JObject { ["path"] = "/members/" + r.Name, ["kind"] = "missing",
                ["requestedSubtypeOf"] = r.SubtypeOf ?? "" });
        }
        else if (!string.Equals(match.SubtypeOf ?? "", r.SubtypeOf ?? "", StringComparison.OrdinalIgnoreCase))
        {
            diff.Add(new JObject { ["path"] = "/members/" + r.Name, ["kind"] = "subtypeMismatch",
                ["requestedSubtypeOf"] = r.SubtypeOf ?? "", ["persistedSubtypeOf"] = match.SubtypeOf ?? "" });
        }
    }
    var requestedNames = new System.Collections.Generic.HashSet<string>(
        requested.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
    foreach (var p in persistedList)
    {
        if (!requestedNames.Contains(p.Name))
            diff.Add(new JObject { ["path"] = "/members/" + p.Name, ["kind"] = "unexpected",
                ["persistedSubtypeOf"] = p.SubtypeOf ?? "" });
    }
    return diff;
}
```

Note: `(string, string)` tuple's `Name == null` check works because a requested tuple
never has a null name (validated earlier); use `default`/`IsDefault` or a null-name
sentinel if the compiler complains about comparing `Name` (a non-nullable string
under nullable annotations) — keep the semantics: "no persisted member matched this
requested name".

**Verify**: build → exit 0.

### Step 2: Re-read and verify after commit

In `UpdateGroupStructure`, after `sdkTrans.Commit()` and before the
`return Ok(...)`:

1. Capture the requested sets *before* the transaction mutates `part` — or better,
   rebuild them from the already-populated `applied`/`removed` JArrays after the
   loop (they carry exactly what was requested):
   - requested members: `applied.Select(a => (a["name"].ToString(), a["subtypeOf"]?.ToString() ?? ""))`
   - requested removals: `removed.Select(r => r.ToString())`
2. Re-find the Group fresh (avoid the in-memory instance — same circularity guard
   `DomainWriteService.VerifyEnumValuesPersisted` uses:
   `object.ReferenceEquals(fresh, original)` → treat as unverifiable, return null):

```csharp
var fresh = _objectService.FindObject(groupName) as Group;
if (fresh == null) return null; // unverifiable — never falsely accuse

var freshPart = fresh.Parts.Get<GroupStructurePart>();
var persisted = freshPart != null
    ? freshPart.Members
        .Where(m => m?.Subtype != null)
        .Select(m => (m.Subtype.Name, m.Supertype?.Name ?? string.Empty))
        .ToList()
    : new System.Collections.Generic.List<(string, string)>();

// Members that should NOT be present (removed set) must also be checked.
```

3. Build the diff: run `BuildMemberDiff(requestedMembers, persisted)` for the
   upserts, and for the removals check each removed name is absent from `persisted`.
4. If `diff.Count > 0` (or a removed name is still present), return the structured
   error envelope (model on `DomainWriteService.VerifyEnumValuesPersisted`'s shape —
   `errorExtra` with before/requested/persisted/diff and `saved:false`):

```csharp
return Models.McpResponse.Err(
    code: "GroupUpdateNotPersisted",
    message: "The SDK saved the Group but the re-read did not confirm the requested member set.",
    hint: "On this GeneXus build the membership write may not have survived the save. Re-read with genexus_structure action=get_visual, and if the members are missing set them in the GeneXus IDE Group editor.",
    nextSteps: new JArray(Models.McpResponse.NextStep(
        tool: "genexus_structure",
        args: new JObject { ["name"] = groupName, ["action"] = "get_visual" },
        why: "Shows the persisted Group members so you can confirm what landed.")),
    target: groupName,
    extra: new JObject
    {
        ["group"] = groupName,
        ["requested"] = <JArray of requested members>,
        ["persisted"] = <JArray of persisted members>,
        ["diff"] = diff,
        ["saved"] = false
    });
```

Wire it so the *success* path only returns `GroupUpdated` when the diff is empty.

**Verify**: build → exit 0.

### Step 3: Tests

Create `src/GxMcp.Worker.Tests/GroupStructureServiceTests.cs` (model on
`Issues70To72RegressionTests` reflection style) testing `BuildMemberDiff` directly:

- **Identical sets → empty diff**: requested `[(A, Sup)]`, persisted `[(A, Sup)]` → 0.
- **Missing member → `missing` entry** with requestedSubtypeOf.
- **Subtype mismatch → `subtypeMismatch` entry** with both sides.
- **Unexpected extra member → `unexpected` entry**.
- **Case-insensitive name matching**: requested `(a, Sup)` vs persisted `(A, Sup)` → 0.
- **Removal verification** (if you implement it as a helper `VerifyRemovedAbsent`, test
  it: removed name present in persisted → drift entry).

**Verify**: `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~GroupStructureServiceTests"` → all pass.

### Step 4: Full worker test suite

**Verify**: `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj` → all pass.

## Test plan

- `GroupStructureServiceTests.cs`: pure `BuildMemberDiff` cases (equal/missing/mismatch/unexpected/case-insensitive) + removal-absence check.
- Pattern: `Issues70To72RegressionTests` reflection-into-internal-static style.
- Verification: worker suite green.

## Done criteria

ALL must hold:
- [ ] `UpdateGroupStructure` re-reads the Group after commit and verifies members + removals.
- [ ] Mismatch returns `GroupUpdateNotPersisted` with before/requested/persisted/diff and `saved:false`; match returns `GroupUpdated`.
- [ ] `BuildMemberDiff` is pure (no SDK types in signature) and unit-tested.
- [ ] Circularity guard (fresh instance vs in-memory) present.
- [ ] Worker test suite green.
- [ ] Only the in-scope files modified (`git status`).
- [ ] `plans/README.md` status row updated.

## STOP conditions

- "Current state" excerpts don't match live code (drift).
- The re-read reveals the SDK genuinely does not persist group memberships (diff is
  non-empty on a healthy KB) — STOP and report; that's a persistence bug in the write
  path, not a verification gap, and fixing it belongs in a separate plan.
- `GroupStructurePart.Members` doesn't expose `Subtype`/`Supertype` as excerpted —
  STOP and report the actual shape before proceeding.
- A step verification fails twice after a reasonable fix.

## Maintenance notes

- This brings `GroupStructureService` into the issue-#59 standard already enforced on
  `DomainWriteService`, `WwpActionService`, and `WriteService.Variables`. If a future
  audit finds another write path still returning `*Updated`/`*Applied` without a
  re-read, treat that as the same finding class.
- The verifier must never falsely accuse: when the re-read returns the same in-memory
  instance (circularity) or fails, treat as unverifiable (return the success path),
  exactly like `DomainWriteService` does.
- Reviewer should confirm the diff helper is reused for both upserts and removals, and
  that the `extra` envelope carries `saved:false` on the failure path (the audit
  finding was specifically about false `GroupUpdated` success).
