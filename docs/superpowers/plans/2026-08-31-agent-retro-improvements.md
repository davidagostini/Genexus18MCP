# Agent Retrospective Improvements Implementation Plan

> **For agentic workers:** Execute this plan task-by-task in the current worktree. Preserve the existing Issue #123 changes and do not commit, push, release, or deploy without an explicit request.

**Goal:** Turn the retrospective findings into durable regression coverage, stricter CLI quality gates, smaller agent instructions, and a reliable focused-test command.

**Architecture:** Keep runtime behavior in the existing CLI modules. Add regression coverage where behavior crosses the package/config boundary, make lint fail on warnings after removing the known dead helper, move detailed project playbooks out of the auto-loaded `AGENTS.md` into linked documentation, and add a small Node test-runner wrapper so focused tests always receive the correct argument order.

**Tech Stack:** Node.js 18+, npm scripts, Node built-in test runner, ESLint 9, Markdown project documentation.

---

### Task 1: Cover the unified Antigravity configuration path

**Files:**
- Modify: `cli/run.test.js` near the existing Antigravity launcher tests.
- Read: `cli/lib/config.js` (`resolveAntigravityConfigPath` and `patchClientConfig`).

- [x] **Step 1: Add a regression test** that pre-creates `.gemini/config/mcp_config.json`, runs `clients add --clients antigravity`, preserves unrelated servers, and asserts the direct packaged gateway (or the documented npx fallback when the package artifact is absent).

```js
test('clients add uses the unified Antigravity config when it already exists', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-antigravity-unified-'));
    try {
        const env = sandboxHomeEnv(tempRoot);
        const configPath = path.join(tempRoot, 'config.json');
        const unifiedPath = path.join(tempRoot, '.gemini', 'config', 'mcp_config.json');
        fs.writeFileSync(configPath, JSON.stringify({ Environment: { KBPath: tempRoot } }));
        fs.mkdirSync(path.dirname(unifiedPath), { recursive: true });
        fs.writeFileSync(unifiedPath, JSON.stringify({ mcpServers: { unrelated: { command: 'keep-me' } } }));

        const result = runCli(['clients', 'add', '--clients', 'antigravity', '--format', 'json'], {
            env: { ...env, GX_CONFIG_PATH: configPath, GENEXUS_MCP_GATEWAY_EXE: '' }
        });
        assert.equal(result.status, 0);

        const written = JSON.parse(fs.readFileSync(unifiedPath, 'utf8'));
        const entry = written.mcpServers.genexus18mcp;
        const packagedGateway = path.join(__dirname, '..', 'publish', 'GxMcp.Gateway.exe');
        assert.ok(written.mcpServers.unrelated);
        if (fs.existsSync(packagedGateway)) {
            assert.equal(entry.command, packagedGateway);
            assert.deepEqual(entry.args, []);
        } else {
            assert.equal(entry.command, 'npx.cmd');
            assert.deepEqual(entry.args, ['-y', 'genexus-mcp@latest']);
        }
        assert.equal(fs.existsSync(path.join(tempRoot, '.gemini', 'antigravity', 'mcp_config.json')), false);
    } finally {
        fs.rmSync(tempRoot, { recursive: true, force: true });
    }
});
```

- [x] **Step 2: Run the focused test**.

Run: `npm run test:one -- "unified Antigravity config"`

Expected: the new test passes and no test process is left running.

### Task 2: Make the package contract and lint gate explicit

**Files:**
- Modify: `cli/run.test.js` in the npm package contract test.
- Modify: `cli/lib/config.js` to remove the unused `stripCodexGenexusBlocks` helper.
- Modify: `package.json` to make `npm run lint` fail on warnings.
- Modify: `CHANGELOG.md` under `## Unreleased` → `### Internal`.

- [x] **Step 1: Extend the package contract test** with `cli/lib/stdio-diagnostics.js` and assert both source existence and coverage by `package.json.files`.

```js
    const requiredCliFiles = ['cli/lib/stdio-diagnostics.js'];
    for (const relative of requiredCliFiles) {
        assert.ok(fs.existsSync(path.join(__dirname, '..', relative)), `runtime file ${relative} must exist on disk`);
        const normalized = relative.replace(/\\/g, '/');
        const covered = (pkg.files || []).some((pattern) => {
            const normalizedPattern = pattern.replace(/\\/g, '/');
            return normalized === normalizedPattern || normalized.startsWith(normalizedPattern.endsWith('/') ? normalizedPattern : `${normalizedPattern}/`);
        });
        assert.ok(covered, `runtime file '${relative}' must be included by package.json files`);
    }
```

- [x] **Step 2: Remove the unused helper** after confirming `rg -n "stripCodexGenexusBlocks" cli` finds only its definition.

- [x] **Step 3: Change the lint script** to:

```json
"lint": "eslint cli scripts eslint.config.js --max-warnings=0"
```

- [x] **Step 4: Add an internal changelog note** explaining that the CLI lint gate now rejects warnings and the dead helper was removed.

- [x] **Step 5: Run lint and the package contract test**.

Run: `npm run lint`

Expected: exit code 0 with no warnings.

Run: `npm run test:one -- "npm package contains"`

Expected: the package contract test passes.

### Task 3: Move detailed agent guidance out of `AGENTS.md`

**Files:**
- Modify: `AGENTS.md`.
- Create: `docs/agent_playbook.md` with the detailed tool-surface and authoring guidance currently embedded in `AGENTS.md`.
- Create: `docs/release_protocol.md` with release execution, npm lag, and changelog voice guidance currently embedded in `AGENTS.md`.

- [x] **Step 1: Copy the detailed tool playbooks and authoring notes** into `docs/agent_playbook.md`, preserving the live tool names, safety constraints, SDK caveats, API grammar, placement verification, event ordering, and SDPanel projection notes.

- [x] **Step 2: Move release mechanics and changelog voice rules** into `docs/release_protocol.md`, preserving the explicit-release gate, release command, GitHub/npm verification, issue-closure rule, npm CDN caveat, and user-facing changelog rules.

- [x] **Step 3: Replace the long sections in `AGENTS.md`** with concise durable rules and navigation pointers to the two new documents, `docs/environment_variables.md`, `docs/mcp_debugging_guide.md`, and `docs/llm_cli_mcp_playbook.md`.

- [x] **Step 4: Keep critical behavior in `AGENTS.md`**: source-of-truth files, build/test commands, changelog requirement, no-release-without-approval, semantic-cache invalidation duty, the scoped binary-lock permission, and the one-line self-update behavior.

- [x] **Step 5: Verify that the refactored file contains no stale moved-section references** and that the detailed documents contain every moved heading.

Run: `rg -n "Live tool playbook|Authoring constraints|Explicit release gate|Changelog voice|docs/agent_playbook|docs/release_protocol" AGENTS.md docs/agent_playbook.md docs/release_protocol.md`

Expected: `AGENTS.md` contains pointers and concise rules; the detailed sections occur in the dedicated docs.

### Task 4: Add a reliable focused-test command

**Files:**
- Create: `scripts/test-one.js`.
- Modify: `package.json` to add the `test:one` script.

- [x] **Step 1: Add the cross-platform wrapper** that requires one pattern, invokes `node --test --test-name-pattern <pattern> cli/run.test.js` with the option before the test file, inherits stdio, and forwards the child exit code.

```js
#!/usr/bin/env node
const { spawnSync } = require('node:child_process');
const path = require('node:path');

const pattern = process.argv.slice(2).join(' ').trim();
if (!pattern) {
    process.stderr.write('Usage: npm run test:one -- "test name pattern"\n');
    process.exit(2);
}

const result = spawnSync(process.execPath, [
    '--test',
    '--test-name-pattern',
    pattern,
    path.join(__dirname, '..', 'cli', 'run.test.js')
], { stdio: 'inherit' });

process.exit(result.status === null ? 1 : result.status);
```

- [x] **Step 2: Add the npm script**:

```json
"test:one": "node scripts/test-one.js"
```

- [x] **Step 3: Execute a focused test** with `npm run test:one -- "stdio launcher persists"` and confirm only the matching test runs.

### Task 5: Full validation and final review

**Files:**
- Inspect: all files modified by Tasks 1–4 plus the existing Issue #123 diff.

- [x] **Step 1: Run `npm test` and confirm zero failures.**
- [x] **Step 2: Run `npm run lint` and confirm zero errors and zero warnings.**
- [x] **Step 3: Run `npm pack --dry-run --ignore-scripts --json` and confirm `cli/lib/stdio-diagnostics.js` and `scripts/test-one.js` are listed.
- [x] **Step 4: Run `git diff --check` and inspect `git status --short` for only intended files.
- [x] **Step 5: Report exact validation results and any residual limitation; do not commit or release.
