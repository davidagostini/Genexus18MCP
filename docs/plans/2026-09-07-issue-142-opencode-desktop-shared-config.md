# Align OpenCode Desktop to Shared opencode.jsonc Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Resolve Issue #142 by configuring OpenCode Desktop to use the shared `opencode.jsonc` / `opencode.json` configuration path, enabling automatic registration and status detection instead of treating it as an AppData manual setup gap.

**Architecture:** OpenCode Desktop and OpenCode CLI on Windows (and other platforms) read and write `%USERPROFILE%\.config\opencode\opencode.jsonc` (or `opencode.json`). In `cli/lib/config.js`, update `opencode-desktop` to use `format: 'opencode'`, `path: resolveOpenCodeConfigPath(xdgConfig)`, and `detectByMarkerOnly: true` (ensuring Desktop is only reported as installed when its application directory/markers exist). Remove `writeSupported: false` and manual setup notes. Update CLI tests, `README.md`, `AGENTS.md`, and `CHANGELOG.md`.

**Tech Stack:** Node.js (v18+), native `node:test`, `cli/lib/config.js`, `cli/lib/client-adapters.js`, `cli/run.test.js`.

---

### Task 1: Update OpenCode Desktop client definition and detection in `cli/lib/config.js`

**Files:**
- Modify: `cli/lib/config.js:650-665`
- Modify: `cli/lib/config.js:683-702`

**Step 1: Write failing test in `cli/run.test.js`**

Add tests proving:
1. `clientsStatus()` reports `opencode-desktop` with `format: 'opencode'`, `writeSupported: true`, `registrationMode: 'automatic'`, and `configPath` pointing to `resolveOpenCodeConfigPath(xdgConfig)`.
2. When the shared `opencode.jsonc` has `genexus18mcp`, `opencode-desktop` reports `registered: true`.
3. `clients add --clients opencode-desktop` writes the entry into `opencode.jsonc` and succeeds in `ok.patchedClients`.
4. When Desktop markers are absent, `opencode-desktop` is `installed: false` even if `opencode.jsonc` exists.

**Step 2: Run test to verify it fails**

Run: `node --test --test-name-pattern="OpenCode Desktop" cli/run.test.js`
Expected: FAIL (expecting old manual setup behavior).

**Step 3: Implement changes in `cli/lib/config.js`**

1. In `detectClientInstalled(client)`:
```javascript
function detectClientInstalled(client) {
    const markers = Array.isArray(client.installMarkers) ? client.installMarkers : [];
    const hasConfig = fs.existsSync(client.path);
    let markerHit = null;
    for (const m of markers) {
        if (fs.existsSync(m)) {
            markerHit = m;
            break;
        }
    }
    const installed = client.detectByMarkerOnly ? (markerHit !== null) : (hasConfig || markerHit !== null);
    return {
        installed,
        hasConfig,
        markerHit,
        markersChecked: markers
    };
}
```

2. In `getClientConfigTargets()`:
```javascript
        {
            id: 'opencode-desktop',
            name: 'OpenCode Desktop',
            format: 'opencode',
            path: resolveOpenCodeConfigPath(xdgConfig),
            detectByMarkerOnly: true,
            installMarkers: [
                path.join(localAppData, 'Programs', '@opencode-aidesktop'),
                path.join(appData, 'ai.opencode.desktop'),
                '/Applications/OpenCode.app'
            ]
        },
```

**Step 4: Run tests to verify they pass**

Run: `node --test --test-name-pattern="OpenCode Desktop" cli/run.test.js`
Expected: PASS.

---

### Task 2: Update all OpenCode Desktop CLI tests in `cli/run.test.js`

**Files:**
- Modify: `cli/run.test.js:794-870`

**Step 1: Replace obsolete manual tests with shared-config automatic tests**

Update the tests:
- `clients list reports OpenCode Desktop as automatic and registered when shared config has entry`
- `clients add patches OpenCode Desktop into shared opencode.jsonc`
- `clients add --clients opencode-desktop without markers still writes shared config when explicitly targeted`
- `OpenCode Desktop is not falsely reported as installed when only CLI config exists`

**Step 2: Run all CLI tests**

Run: `npm test`
Expected: PASS (all 79+ tests pass).

---

### Task 3: Update documentation and AGENTS.md

**Files:**
- Modify: `README.md:221-240`
- Modify: `AGENTS.md:136-138`
- Modify: `CHANGELOG.md` under `## Unreleased` -> `### Fixed`

**Step 1: Edit `README.md`**
Update client table: OpenCode Desktop is marked `✅` with note "Shares `opencode.jsonc` with OpenCode CLI; restart required". Remove manual setup instructions section or note that automatic setup is now supported.

**Step 2: Edit `AGENTS.md`**
Update client registration section to state that OpenCode Desktop shares `opencode.jsonc` with OpenCode CLI and is auto-registered.

**Step 3: Edit `CHANGELOG.md`**
Add changelog entry under `### Fixed`:
`- Fixed OpenCode Desktop client detection and registration to target the shared `opencode.jsonc` configuration path rather than treating it as an AppData manual setup gap (Issue #142).`

---

### Task 4: Run repository validations and close Issue #142

**Step 1: Run linter and tests**
Run:
- `npm run lint`
- `npm test`
- `dotnet test Genexus18MCP.sln`

**Step 2: Review git diff and commit**
Commit the fix for Issue #142 with clear commit message referencing #142.

**Step 3: Push and comment/close Issue #142**
Comment on Issue #142 with fix details and close the issue.
