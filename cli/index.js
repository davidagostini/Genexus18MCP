const { spawn } = require('child_process');
const {
    getGatewayExePath,
    applyLauncherConfigOrExit,
    isPathLikelyAppLockerBlocked
} = require('./lib/config');
const {
    SUPPORTED_FORMATS,
    writeStructured,
    renderOutput,
    formatToonObject
} = require('./lib/output');
const {
    handleStatus,
    handleDoctor,
    handleToolsList,
    handleConfigShow,
    handleInit,
    handleWhoami,
    handleUninstall,
    handleKb,
    handleClients,
    handleHome,
    handleLlmHelp,
    handleLayout,
    handleHelp,
    usageEnvelope,
    commandHelpMap
} = require('./commands/axi');
const { startBackgroundUpdateCheck, handleUpdate } = require('./lib/update-check');

const EXIT_CODES = {
    OK: 0,
    ERROR: 1,
    USAGE: 2
};

const GLOBAL_DEFAULTS = {
    format: 'toon',
    full: false,
    fields: null,
    interactive: false,
    writeClients: false,
    clients: null,
    allClients: false,
    serverName: null,
    force: false,
    mcpSmoke: false,
    dump: false,
    noSmoke: false,
    warm: false,
    yes: false,
    apply: false,
    channel: null,
    name: null,
    limit: 100,
    query: null,
    quiet: false,
    noColor: false,
    help: false
};

const KNOWN_COMMANDS = new Set(['status', 'doctor', 'tools', 'config', 'init', 'setup', 'whoami', 'uninstall', 'kb', 'clients', 'help', 'home', 'axi', 'llm', 'layout', 'update']);

function parseArgs(argv) {
    const result = {
        command: null,
        subcommand: null,
        options: { ...GLOBAL_DEFAULTS },
        passthroughArgs: [...argv],
        unknownFlags: [],
        positional: []
    };

    const tokens = [...argv];
    if (tokens.length === 0) return result;

    const first = tokens[0];
    if (!KNOWN_COMMANDS.has(first) && !first.startsWith('--')) {
        return result;
    }

    if (first === '--help' || first === '-h') {
        result.command = 'help';
        result.options.help = true;
        return result;
    }

    if (KNOWN_COMMANDS.has(first)) {
        result.command = first === 'setup' ? 'init' : first;
        tokens.shift();
    }

    if (result.command === 'tools' && tokens[0] === 'list') {
        result.subcommand = 'list';
        tokens.shift();
    }

    if (result.command === 'config' && tokens[0] === 'show') {
        result.subcommand = 'show';
        tokens.shift();
    }

    if (result.command === 'axi' && tokens[0] === 'home') {
        result.subcommand = 'home';
        tokens.shift();
    }

    if (result.command === 'home') {
        result.subcommand = 'home';
    }

    if (result.command === 'llm' && tokens[0] === 'help') {
        result.subcommand = 'help';
        tokens.shift();
    }

    if (result.command === 'kb' && ['list', 'add', 'remove', 'switch'].includes(tokens[0])) {
        result.subcommand = tokens[0];
        tokens.shift();
    }

    if (result.command === 'clients' && ['list', 'add', 'remove'].includes(tokens[0])) {
        result.subcommand = tokens[0];
        tokens.shift();
    }

    if (result.command === 'layout' && (tokens[0] === 'status' || tokens[0] === 'run' || tokens[0] === 'inspect')) {
        result.subcommand = tokens[0];
        tokens.shift();
    }

    for (let i = 0; i < tokens.length; i += 1) {
        const token = tokens[i];

        if (!token.startsWith('--')) {
            result.positional.push(token);
            continue;
        }

        const [rawKey, inlineValue] = token.split('=', 2);
        const key = rawKey.slice(2);
        const next = tokens[i + 1];

        const takeValue = () => {
            if (inlineValue !== undefined) return inlineValue;
            if (!next || next.startsWith('--')) return null;
            i += 1;
            return next;
        };

        switch (key) {
            case 'format': {
                const val = takeValue();
                if (val) result.options.format = val;
                else result.unknownFlags.push('--format requires a value');
                break;
            }
            case 'fields': {
                const val = takeValue();
                if (val) result.options.fields = val;
                else result.unknownFlags.push('--fields requires a value');
                break;
            }
            case 'kb': {
                const val = takeValue();
                if (val) result.options.kb = val;
                else result.unknownFlags.push('--kb requires a value');
                break;
            }
            case 'gx': {
                const val = takeValue();
                if (val) result.options.gx = val;
                else result.unknownFlags.push('--gx requires a value');
                break;
            }
            case 'name': {
                const val = takeValue();
                if (val) result.options.name = val;
                else result.unknownFlags.push('--name requires a value');
                break;
            }
            case 'limit': {
                const val = takeValue();
                if (!val) {
                    result.unknownFlags.push('--limit requires a value');
                    break;
                }
                const parsed = Number.parseInt(val, 10);
                if (!Number.isFinite(parsed) || parsed <= 0) {
                    result.unknownFlags.push('--limit must be a positive integer');
                    break;
                }
                result.options.limit = parsed;
                break;
            }
            case 'query': {
                const val = takeValue();
                if (val) result.options.query = val;
                else result.unknownFlags.push('--query requires a value');
                break;
            }
            case 'clients': {
                const val = takeValue();
                if (val) result.options.clients = val;
                else result.unknownFlags.push('--clients requires a value');
                break;
            }
            case 'all-clients':
                result.options.allClients = true;
                break;
            case 'server-name': {
                const val = takeValue();
                if (val) {
                    if (!/^[a-zA-Z0-9_-]+$/.test(val)) {
                        result.unknownFlags.push(`--server-name must be alphanumeric (letters, digits, _, -), got: "${val}"`);
                    } else {
                        result.options.serverName = val;
                    }
                } else {
                    result.unknownFlags.push('--server-name requires a value');
                }
                break;
            }
            case 'force':
                result.options.force = true;
                break;
            case 'action': {
                const val = takeValue();
                if (val) result.options.action = val;
                else result.unknownFlags.push('--action requires a value');
                break;
            }
            case 'title': {
                const val = takeValue();
                if (val) result.options.title = val;
                else result.unknownFlags.push('--title requires a value');
                break;
            }
            case 'tab': {
                const val = takeValue();
                if (val) result.options.tab = val;
                else result.unknownFlags.push('--tab requires a value');
                break;
            }
            case 'keys': {
                const val = takeValue();
                if (val) result.options.keys = val;
                else result.unknownFlags.push('--keys requires a value');
                break;
            }
            case 'text': {
                const val = takeValue();
                if (val) result.options.text = val;
                else result.unknownFlags.push('--text requires a value');
                break;
            }
            case 'x': {
                const val = takeValue();
                if (!val) {
                    result.unknownFlags.push('--x requires a value');
                    break;
                }
                const parsed = Number.parseInt(val, 10);
                if (!Number.isFinite(parsed)) {
                    result.unknownFlags.push('--x must be an integer');
                    break;
                }
                result.options.x = parsed;
                break;
            }
            case 'y': {
                const val = takeValue();
                if (!val) {
                    result.unknownFlags.push('--y requires a value');
                    break;
                }
                const parsed = Number.parseInt(val, 10);
                if (!Number.isFinite(parsed)) {
                    result.unknownFlags.push('--y must be an integer');
                    break;
                }
                result.options.y = parsed;
                break;
            }
            case 'full':
                result.options.full = true;
                break;
            case 'interactive':
                result.options.interactive = true;
                break;
            case 'write-clients':
                result.options.writeClients = true;
                break;
            // issue #32 item 3: init now patches detected clients by default; this opts out.
            case 'no-write-clients':
                result.options.noWriteClients = true;
                break;
            case 'mcp-smoke':
                result.options.mcpSmoke = true;
                break;
            case 'dump':
                result.options.dump = true;
                break;
            case 'no-smoke':
                result.options.noSmoke = true;
                break;
            case 'warm':
                result.options.warm = true;
                break;
            case 'yes':
                result.options.yes = true;
                break;
            case 'apply':
                result.options.apply = true;
                break;
            case 'channel': {
                const val = takeValue();
                if (val) result.options.channel = val;
                else result.unknownFlags.push('--channel requires a value');
                break;
            }
            case 'quiet':
                result.options.quiet = true;
                break;
            case 'no-color':
                result.options.noColor = true;
                break;
            case 'help':
                result.options.help = true;
                break;
            default:
                result.unknownFlags.push(`Unknown flag: --${key}`);
                break;
        }
    }

    return result;
}

function writeAppLockerHint(stderr, gatewayExePath) {
    const riskyZone = isPathLikelyAppLockerBlocked(gatewayExePath);
    stderr.write('[genexus-mcp] Likely cause: Windows AppLocker / SRP is blocking execution from this path.\n');
    if (riskyZone) {
        stderr.write(`[genexus-mcp] The gateway exe lives under %${riskyZone}% (typically restricted by corporate policy):\n[genexus-mcp]   ${gatewayExePath}\n`);
    }
    stderr.write('[genexus-mcp] Remediation: install to a whitelisted path with:\n');
    stderr.write('[genexus-mcp]   iex (irm https://raw.githubusercontent.com/lennix1337/Genexus18MCP/main/scripts/install.ps1)\n');
    stderr.write('[genexus-mcp] Or copy the package `publish/` folder to a path outside %APPDATA%/%LOCALAPPDATA% and point the MCP client at that exe.\n');
}

async function launchGateway(passthroughArgs, options) {
    const setup = applyLauncherConfigOrExit({
        cwd: process.cwd(),
        stderr: process.stderr,
        quiet: options.quiet
    });

    if (!setup.ok) {
        return EXIT_CODES.ERROR;
    }

    const gatewayExePath = getGatewayExePath();
    if (!require('fs').existsSync(gatewayExePath)) {
        if (!options.quiet) {
            process.stderr.write(`[genexus-mcp] ERROR: Gateway executable not found at ${gatewayExePath}\n`);
        }
        return EXIT_CODES.ERROR;
    }

    return await new Promise((resolve) => {
        const child = spawn(gatewayExePath, passthroughArgs, {
            stdio: 'inherit',
            env: process.env,
            windowsHide: true
        });

        child.on('error', (err) => {
            if (!options.quiet) {
                process.stderr.write(`[genexus-mcp] ERROR: Failed to start gateway process: ${err.message}\n`);
                const code = err && (err.code || err.errno);
                const accessDenied = code === 'EACCES' || code === 'EPERM' || /access is denied|access denied|acesso negado/i.test(err.message || '');
                if (accessDenied) {
                    writeAppLockerHint(process.stderr, gatewayExePath);
                }
            }
            resolve(EXIT_CODES.ERROR);
        });

        child.on('exit', (code, signal) => {
            if (signal) {
                resolve(EXIT_CODES.ERROR);
                return;
            }
            resolve(code || EXIT_CODES.OK);
        });
    });
}

function commandFromHelpIntent(parsed) {
    if (!parsed.options.help) return null;
    if (parsed.command === 'axi' && parsed.subcommand === 'home') return 'home';
    if (parsed.command && parsed.command !== 'help') return parsed.command;
    if (parsed.positional.length > 0) {
        const candidate = parsed.positional[0];
        if (commandHelpMap()[candidate]) return candidate;
    }
    return null;
}

function withCommandMeta(envelope, commandName) {
    const safe = envelope && typeof envelope === 'object' ? envelope : {};
    const meta = safe.meta && typeof safe.meta === 'object' ? safe.meta : {};
    return {
        ...safe,
        meta: {
            command: commandName,
            ...meta
        }
    };
}

function resolveMetaCommand(parsed, targetHelp) {
    if (targetHelp || parsed.command === 'help') return 'help';
    if (parsed.command === 'tools') return 'tools.list';
    if (parsed.command === 'config') return 'config.show';
    if (parsed.command === 'axi' || parsed.command === 'home') return 'home';
    if (parsed.command === 'llm') return 'llm.help';
    if (parsed.command === 'layout') {
        if (parsed.subcommand === 'run') return 'layout.run';
        if (parsed.subcommand === 'inspect') return 'layout.inspect';
        return 'layout.status';
    }
    if (parsed.command === 'kb') {
        return parsed.subcommand ? `kb.${parsed.subcommand}` : 'kb';
    }
    if (parsed.command === 'clients') {
        return parsed.subcommand ? `clients.${parsed.subcommand}` : 'clients.list';
    }
    if (parsed.command === 'update') return 'update';
    return parsed.command || 'unknown';
}

async function main(argv) {
    const parsed = parseArgs(argv);

    if (parsed.command !== 'update') {
        startBackgroundUpdateCheck({ quiet: parsed.options.quiet });
    }

    if (!parsed.command) {
        return launchGateway(argv, parsed.options);
    }

    if (parsed.unknownFlags.length > 0) {
        const envelope = usageEnvelope(parsed.unknownFlags.join('; '), EXIT_CODES.USAGE);
        writeStructured(process.stdout, withCommandMeta(envelope, parsed.command || 'usage'), parsed.options.format);
        return EXIT_CODES.USAGE;
    }

    if (!SUPPORTED_FORMATS.has(parsed.options.format)) {
        const envelope = usageEnvelope(`Invalid --format '${parsed.options.format}'. Use toon|json|text.`, EXIT_CODES.USAGE);
        writeStructured(process.stdout, withCommandMeta(envelope, parsed.command || 'usage'), 'toon');
        return EXIT_CODES.USAGE;
    }

    const ctx = {
        cwd: process.cwd(),
        stdout: process.stdout,
        stderr: process.stderr,
        EXIT_CODES
    };

    const targetHelp = commandFromHelpIntent(parsed);
    if (targetHelp || parsed.command === 'help') {
        const helpResult = await handleHelp(targetHelp, ctx);
        writeStructured(process.stdout, withCommandMeta(helpResult.envelope, resolveMetaCommand(parsed, targetHelp)), parsed.options.format);
        return helpResult.exitCode;
    }

    let result;

    switch (parsed.command) {
        case 'status':
            result = await handleStatus(parsed.options, ctx);
            break;
        case 'doctor':
            result = await handleDoctor(parsed.options, ctx);
            break;
        case 'home':
            result = await handleHome(parsed.options, ctx);
            break;
        case 'axi':
            if (parsed.subcommand && parsed.subcommand !== 'home') {
                writeStructured(
                    process.stdout,
                    withCommandMeta(usageEnvelope('axi supports only subcommand `home`.', EXIT_CODES.USAGE), resolveMetaCommand(parsed)),
                    parsed.options.format
                );
                return EXIT_CODES.USAGE;
            }
            result = await handleHome(parsed.options, ctx);
            break;
        case 'tools':
            if (parsed.subcommand !== 'list') {
                writeStructured(
                    process.stdout,
                    withCommandMeta(usageEnvelope('tools requires subcommand `list`.', EXIT_CODES.USAGE), resolveMetaCommand(parsed)),
                    parsed.options.format
                );
                return EXIT_CODES.USAGE;
            }
            result = await handleToolsList(parsed.options, ctx);
            break;
        case 'config':
            if (parsed.subcommand !== 'show') {
                writeStructured(
                    process.stdout,
                    withCommandMeta(usageEnvelope('config requires subcommand `show`.', EXIT_CODES.USAGE), resolveMetaCommand(parsed)),
                    parsed.options.format
                );
                return EXIT_CODES.USAGE;
            }
            result = await handleConfigShow(parsed.options, ctx);
            break;
        case 'llm':
            if (parsed.subcommand && parsed.subcommand !== 'help') {
                writeStructured(
                    process.stdout,
                    withCommandMeta(usageEnvelope('llm supports only subcommand `help`.', EXIT_CODES.USAGE), resolveMetaCommand(parsed)),
                    parsed.options.format
                );
                return EXIT_CODES.USAGE;
            }
            result = await handleLlmHelp(parsed.options, ctx);
            break;
        case 'layout':
            if (parsed.subcommand !== 'status' && parsed.subcommand !== 'run' && parsed.subcommand !== 'inspect') {
                writeStructured(
                    process.stdout,
                    withCommandMeta(usageEnvelope('layout requires subcommand `status`, `run`, or `inspect`.', EXIT_CODES.USAGE), resolveMetaCommand(parsed)),
                    parsed.options.format
                );
                return EXIT_CODES.USAGE;
            }
            result = await handleLayout(parsed.subcommand, parsed.options, ctx);
            break;
        case 'init':
            result = await handleInit(parsed.options, ctx);
            break;
        case 'whoami':
            result = await handleWhoami(parsed.options, ctx);
            break;
        case 'uninstall':
            result = await handleUninstall(parsed.options, ctx);
            break;
        case 'kb':
            if (!parsed.subcommand || !['list', 'add', 'remove', 'switch'].includes(parsed.subcommand)) {
                writeStructured(
                    process.stdout,
                    withCommandMeta(usageEnvelope('kb requires subcommand `list`, `add`, `remove`, or `switch`.', EXIT_CODES.USAGE), resolveMetaCommand(parsed)),
                    parsed.options.format
                );
                return EXIT_CODES.USAGE;
            }
            result = await handleKb(parsed.subcommand, parsed.options, ctx);
            break;
        case 'clients':
            result = await handleClients(parsed.subcommand, parsed.options, ctx);
            break;
        case 'update':
            result = await handleUpdate(parsed.options, ctx);
            break;
        default:
            writeStructured(
                process.stdout,
                withCommandMeta(usageEnvelope(`Unsupported command '${parsed.command}'.`, EXIT_CODES.USAGE), resolveMetaCommand(parsed)),
                parsed.options.format
            );
            return EXIT_CODES.USAGE;
    }

    writeStructured(process.stdout, withCommandMeta(result.envelope, resolveMetaCommand(parsed)), parsed.options.format);
    return result.exitCode;
}

module.exports = {
    main,
    parseArgs,
    EXIT_CODES,
    renderOutput,
    formatToonObject
};
