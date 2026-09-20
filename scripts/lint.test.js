'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const test = require('node:test');
const { dependencyStatus } = require('./lint');

function tempRoot() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'gxmcp-lint-'));
  fs.mkdirSync(path.join(root, 'node_modules', '.bin'), { recursive: true });
  return root;
}

test('lint setup identifies missing eslint executable and config package', () => {
  const root = tempRoot();
  try {
    assert.deepEqual(dependencyStatus(root).map((item) => item.name), ['eslint', '@eslint/js']);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('lint setup independently identifies missing @eslint/js when eslint exists', () => {
  const root = tempRoot();
  try {
    fs.writeFileSync(path.join(root, 'node_modules', '.bin', process.platform === 'win32' ? 'eslint.cmd' : 'eslint'), '');
    assert.deepEqual(dependencyStatus(root).map((item) => item.name), ['@eslint/js']);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('lint setup does not install or consume stdin in CI', () => {
  const root = tempRoot();
  try {
    const result = spawnSync(process.execPath, [path.join(__dirname, 'lint.js')], {
      cwd: path.join(__dirname, '..'),
      env: { ...process.env, CI: '1', GXMCP_LINT_ROOT: root },
      input: 's\n',
      encoding: 'utf8',
    });
    assert.equal(result.status, 1);
    assert.match(result.stderr, /GXMCP_LINT_SETUP_ERROR:/);
    assert.match(result.stderr, /npm ci/);
    assert.deepEqual(fs.readdirSync(path.join(root, 'node_modules', '.bin')), []);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
