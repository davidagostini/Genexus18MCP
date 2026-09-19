const fs = require('fs');
const os = require('os');
const path = require('path');

const MAX_STDERR_BYTES = 64 * 1024;

function getStdioErrorLogPath() {
    if (process.platform !== 'win32') return null;
    const localAppData = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
    return path.join(localAppData, 'GenexusMCP', 'logs', 'last-stdio-error.txt');
}

function trimToMaxBytes(value, maxBytes = MAX_STDERR_BYTES) {
    const raw = String(value || '');
    const bytes = Buffer.from(raw, 'utf8');
    if (bytes.length <= maxBytes) return raw;
    return bytes.subarray(bytes.length - maxBytes).toString('utf8');
}

function createStderrTail(maxBytes = MAX_STDERR_BYTES) {
    let value = '';
    return {
        append(chunk) {
            if (chunk === undefined || chunk === null) return;
            value = trimToMaxBytes(value + (Buffer.isBuffer(chunk) ? chunk.toString('utf8') : String(chunk)), maxBytes);
        },
        toString() {
            return value;
        }
    };
}

function oneLine(value, fallback = '') {
    const text = value === undefined || value === null ? '' : String(value);
    return text.replace(/\r?\n/g, ' ').trim() || fallback;
}

function writeLastStdioError({ gatewayExePath, exitCode = null, signal = null, error = null, stderrTail = '' } = {}) {
    const logPath = getStdioErrorLogPath();
    if (!logPath) return null;

    const tail = trimToMaxBytes(stderrTail).trimEnd();
    const lines = [
        `timestampUtc: ${new Date().toISOString()}`,
        `gateway: ${oneLine(gatewayExePath, '(unknown)')}`,
        `exitCode: ${exitCode === null || exitCode === undefined ? 'n/a' : exitCode}`,
        `signal: ${oneLine(signal, 'none')}`
    ];
    if (error) lines.push(`error: ${oneLine(error)}`);
    lines.push('stderr:');
    lines.push(tail || '(none captured)');
    lines.push('');

    try {
        fs.mkdirSync(path.dirname(logPath), { recursive: true });
        fs.writeFileSync(logPath, lines.join('\n'), 'utf8');
        return logPath;
    } catch {
        return null;
    }
}

module.exports = {
    createStderrTail,
    getStdioErrorLogPath,
    writeLastStdioError
};
