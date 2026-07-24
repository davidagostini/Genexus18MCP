# Plan 064: Guard the rename provider against unsaved edits before running the KB-side rename

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 98b9a7d..HEAD -- src/nexus-ide/src/renameProvider.ts`
> If it changed since this plan was written, compare the "Current state"
> excerpt against live code before proceeding; on a mismatch, STOP.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `98b9a7d`, 2026-07-23

## Why this matters

`GxRenameProvider.provideRenameEdits` issues the KB-side rename (`this.provider.refactor`)
**without first checking whether the active document has unsaved edits**. The GeneXus
worker renames against the last-*saved* KB state, so any unsaved occurrence the user
just typed is missed. The provider then shows "Variable renamed successfully" while
the dirty editor is explicitly **skipped** from post-rename refresh (with only a
passive warning), leaving an inconsistent rename that is easy to miss. The existing
`document.isDirty` check (`renameProvider.ts:107`) only guards *re-hydrating* an
already-dirty editor after the rename — it does nothing to stop the stale rename from
being issued in the first place.

Fixing this makes rename operate on exactly the content the user is looking at.

## Current state

File: `src/nexus-ide/src/renameProvider.ts` — VS Code `RenameProvider`; F2 rename entry point.

`provideRenameEdits` (lines 14-56) never inspects `document.isDirty`; it goes straight
to `refactor`:

```ts
async provideRenameEdits(
    document: vscode.TextDocument,
    position: vscode.Position,
    newName: string,
    _token: vscode.CancellationToken
): Promise<vscode.WorkspaceEdit | undefined> {
    const range = document.getWordRangeAtPosition(position);
    if (!range) return undefined;

    const oldName = document.getText(range);
    const objName = this.getObjName(document);
    const isVariable = isVariableToken(document, range, oldName);

    try {
        await vscode.window.withProgress({ /* ... */ }, async () => {
            const result = await this.provider.refactor(/* RenameVariable | RenameAttribute */, 300000);
            // ...
        });
        await this.refreshAffectedEditors(objName, isVariable);
        // ...
```

The only dirty check today is *after* the rename, in `refreshAffectedEditors`
(lines 106-112), and it merely skips refresh + warns:

```ts
for (const doc of docs) {
  if (doc.isDirty) {
    vscode.window.showWarningMessage(`Nexus IDE: skipped refreshing '${doc.uri.fsPath}' after rename — it has unsaved changes. ...`);
    continue;
  }
  // ...
}
```

### Convention

TypeScript, 2-space (this file uses 4-space — **match the file's existing 4-space
indent**, per repo rule "match existing style"). `tsc -p ./`, ESLint 9. Tests:
`src/nexus-ide/src/test/suite/renameProvider.test.ts` already exists — extend it.
User-facing strings in this file are a mix of English/Portuguese; match the English
style already used at `renameProvider.ts:75`.

## Commands you will need

Run from `src/nexus-ide/`.

| Purpose | Command           | Expected |
|---------|-------------------|----------|
| Compile | `npm run compile` | exit 0   |
| Lint    | `npm run lint`    | 0 new errors |
| Tests   | `npm test`        | all pass |
| Gate    | `npm run check`   | all pass |

## Scope

**In scope**:
- `src/nexus-ide/src/renameProvider.ts`
- `src/nexus-ide/src/test/suite/renameProvider.test.ts` (extend)

**Out of scope**:
- `refreshAffectedEditors`'s existing post-rename dirty handling — leave it (it's a
  correct second layer). Only add the *pre*-rename guard.
- `gxFileSystem.ts` / `GxShadowService` — do not change the `refactor` transport.
- `isVariableToken` / `GxVariableToken.ts` — detection logic is out of scope.

## Git workflow

- Branch: `advisor/064-nexus-rename-dirty-guard`
- Commit style: `fix(nexus-ide): ...`.
- Do NOT push or open a PR unless instructed.

## Steps

### Step 1: Add the pre-rename dirty guard

At the top of `provideRenameEdits`, after computing `range`/`oldName` but **before**
the `withProgress`/`refactor` call, add: if `document.isDirty`, prompt the user to
save first (modal), and either save-then-proceed or cancel. Target shape (match the
file's 4-space indent):

```ts
if (document.isDirty) {
    const choice = await vscode.window.showWarningMessage(
        `'${document.fileName.split(/[\\/]/).pop()}' has unsaved changes. The rename runs against the saved Knowledge Base, so it would miss unsaved edits. Save and rename?`,
        { modal: true },
        'Save and Rename',
    );
    if (choice !== 'Save and Rename') {
        return undefined; // user cancelled — no rename issued
    }
    const saved = await document.save();
    if (!saved) {
        vscode.window.showErrorMessage('Nexus IDE: could not save the document; rename aborted.');
        return undefined;
    }
}
```

Notes:
- A **modal** warning is intentional: rename is a deliberate, high-consequence action
  and a non-modal toast would be missed.
- `document.save()` for a `gxkb18:`/shadow `file:` document routes through the
  extension's own `FileSystemProvider.writeFile`, persisting to the KB — after it
  resolves, `refactor` operates on current content. Returning `undefined` from a
  `RenameProvider` cleanly aborts VS Code's rename with no edit applied.

**Verify**: `npm run compile` → exit 0.

### Step 2: Extend the tests

Add cases to `src/nexus-ide/src/test/suite/renameProvider.test.ts`. Use the existing
test's mocking approach for `document`, `provider`, and stubbed `vscode.window`
prompts. Cover:
- **Clean document**: `document.isDirty === false` → `refactor` **is** called (no prompt path taken). (Guards against the guard blocking normal renames.)
- **Dirty + user cancels**: `document.isDirty === true`, the warning stub returns `undefined`/anything other than `'Save and Rename'` → `refactor` is **not** called and the method returns `undefined`.
- **Dirty + user saves**: warning stub returns `'Save and Rename'`, `document.save()` stub resolves `true` → `document.save` called **before** `refactor`, and `refactor` **is** called.

If the existing test file doesn't already stub `vscode.window.showWarningMessage`,
add a minimal stub following how it stubs other `vscode.window` calls.

**Verify**: `npm test` → all pass including the 3 new cases.

### Step 3: Full gate

**Verify**: `npm run check` → all pass.

## Test plan

- 3 new cases in `renameProvider.test.ts`: clean→proceeds, dirty+cancel→no-op, dirty+save→saves-then-renames.
- Pattern: the existing cases in the same file.
- Verification: `npm test` → all pass.

## Done criteria

ALL must hold:
- [ ] `npm run compile` exits 0.
- [ ] `npm run lint` exits 0, no new errors.
- [ ] `npm test` passes; the 3 new `renameProvider.test.ts` cases pass.
- [ ] `grep -n "document.isDirty" src/nexus-ide/src/renameProvider.ts` shows the guard **before** the `refactor` call (in `provideRenameEdits`), in addition to the pre-existing one in `refreshAffectedEditors`.
- [ ] Only the two in-scope files modified (`git status`).
- [ ] `plans/README.md` status row updated.

## STOP conditions

- "Current state" excerpt doesn't match live code (drift).
- `document.save()` does not exist / does not persist for the shadow `file:` scheme in this VS Code API version (check `@types/vscode` — `TextDocument.save(): Thenable<boolean>` has existed since 1.6) — if the test reveals save is a no-op for gxkb18 docs, STOP and report; the guard may need to route through `provider.writeFile` instead.
- A step verification fails twice after a reasonable fix.

## Maintenance notes

- If a future change adds true multi-part live editor sync (the TODO referenced at
  `renameProvider.ts:59-63`), this guard still applies — the save must happen before
  the KB-side rename regardless of how editors are re-hydrated after.
- Reviewer should confirm the clean-document path is untouched (no new prompt on the
  common case) and that cancel returns `undefined` (VS Code aborts cleanly).
