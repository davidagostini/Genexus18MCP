'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const test = require('node:test');

test('postinstall accepts a linked git worktree as a development checkout', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'gxmcp-verify-install-'));
  try {
    fs.mkdirSync(path.join(root, 'scripts'), { recursive: true });
    fs.copyFileSync(path.join(__dirname, 'verify-install.js'), path.join(root, 'scripts', 'verify-install.js'));
    fs.writeFileSync(path.join(root, 'package.json'), JSON.stringify({ version: '3.5.3' }));
    fs.writeFileSync(path.join(root, '.git'), 'gitdir: C:/repo/.git/worktrees/test\n');

    const result = spawnSync(process.execPath, [path.join(root, 'scripts', 'verify-install.js')], {
      encoding: 'utf8',
    });
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stderr, /dev checkout/);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
