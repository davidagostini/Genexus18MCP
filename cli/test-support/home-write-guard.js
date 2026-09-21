// Test-only preload: snapshot real registry roots before any sandbox overrides.
// Exit immediately: config writers catch IO errors and may otherwise report success.
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { URL, fileURLToPath } = require('node:url');
const { getClientConfigTargets } = require('../lib/config');

const inheritedRoots = process.env.GXMCP_TEST_PROTECTED_ROOTS;
const roots = inheritedRoots ? JSON.parse(inheritedRoots) : [
    ...getClientConfigTargets().flatMap(client => [client.path, ...(client.alternatePaths || [])])
        .map(p => path.dirname(p) === os.homedir() ? p : path.dirname(p)),
    ...[os.homedir(), process.env.HOME, process.env.USERPROFILE].filter(Boolean).map(p => path.join(p, '.genexus-mcp')),
    path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'GenexusMCP')
];

function canonicalPath(value) {
    let current = path.resolve(value instanceof URL ? fileURLToPath(value) : String(value));
    const tail = [];
    while (!fs.existsSync(current)) {
        const parent = path.dirname(current);
        if (parent === current) break;
        tail.unshift(path.basename(current));
        current = parent;
    }
    const resolved = path.join(fs.realpathSync.native(current), ...tail);
    return process.platform === 'win32' ? resolved.toLowerCase() : resolved;
}

const protectedRoots = [...new Set(roots.map(canonicalPath))];
const originalExit = process.exit.bind(process);

function checkWrite(value) {
    if (typeof value === 'number') return; // writable descriptors are guarded at open.
    const candidate = canonicalPath(value);
    if (protectedRoots.some(root => candidate === root || candidate.startsWith(root + path.sep)
        || candidate.startsWith(root + '.'))) {
        fs.writeSync(2, 'GXMCP_TEST_HOME_WRITE_BLOCKED: use sandboxHomeEnv; refusing operator configuration mutation.\n');
        originalExit(97);
    }
}

// Guard the filesystem primitives used by client backups, atomic config writes,
// unpatch and launcher config. Installed in both the runner and CLI children.
// Config writers currently use sync IO; extend this guard if they adopt async IO.
for (const [method, indexes] of Object.entries({
    mkdirSync: [0], writeFileSync: [0], appendFileSync: [0], unlinkSync: [0], rmSync: [0],
    rmdirSync: [0], copyFileSync: [1], cpSync: [1], renameSync: [0, 1], truncateSync: [0],
    mkdtempSync: [0], symlinkSync: [1], linkSync: [0, 1], createWriteStream: [0]
})) {
    const original = fs[method];
    fs[method] = function (...args) {
        for (const index of indexes) checkWrite(args[index]);
        return original.apply(this, args);
    };
}
const originalOpen = fs.openSync;
fs.openSync = function (file, flags, ...args) {
    const writable = typeof flags === 'number'
        ? flags & (fs.constants.O_WRONLY | fs.constants.O_RDWR | fs.constants.O_CREAT | fs.constants.O_TRUNC | fs.constants.O_APPEND)
        : /[wa+]/.test(flags);
    if (writable) checkWrite(file);
    return originalOpen.call(this, file, flags, ...args);
};

module.exports = { protectedRoots };
