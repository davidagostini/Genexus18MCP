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
