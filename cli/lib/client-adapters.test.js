const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
const {
    getClientAdapter,
    McpServersJsonAdapter,
    VsCodeServersAdapter,
    OpenCodeJsoncAdapter,
    CodexTomlAdapter,
    ClientConfigManager
} = require('./client-adapters');

test('getClientAdapter returns correct strategy for each format', () => {
    assert.ok(getClientAdapter('mcpServers') instanceof McpServersJsonAdapter);
    assert.ok(getClientAdapter('vscode-servers') instanceof VsCodeServersAdapter);
    assert.ok(getClientAdapter('opencode') instanceof OpenCodeJsoncAdapter);
    assert.ok(getClientAdapter('codex-toml') instanceof CodexTomlAdapter);
});

test('ClientConfigManager applies and reads mcpServers format cleanly', () => {
    const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-cli-test-'));
    const configPath = path.join(tmpDir, 'mcp.json');
    const client = { id: 'test-cursor', name: 'Test Cursor', format: 'mcpServers', path: configPath };
    const launcher = { command: 'node', args: ['run.js'] };

    try {
        const manager = new ClientConfigManager();
        manager.apply(client, launcher, 'C:\\MyKb\\config.json', { serverName: 'genexus18mcp' });

        const raw = fs.readFileSync(configPath, 'utf8');
        const parsed = JSON.parse(raw);
        assert.ok(parsed.mcpServers);
        assert.ok(parsed.mcpServers.genexus18mcp);
        assert.equal(parsed.mcpServers.genexus18mcp.command, 'node');

        const readEntry = manager.read(client, 'genexus18mcp');
        assert.ok(readEntry);
        assert.equal(readEntry.command, 'node');

        const wasRemoved = manager.remove(client, { serverName: 'genexus18mcp' });
        assert.equal(wasRemoved, true);

        const afterRemove = JSON.parse(fs.readFileSync(configPath, 'utf8'));
        assert.equal(afterRemove.mcpServers.genexus18mcp, undefined);
    } finally {
        try { fs.rmSync(tmpDir, { recursive: true, force: true }); } catch { }
    }
});
