#!/usr/bin/env node
const { main, EXIT_CODES } = require('./index');
const { writeStructured } = require('./lib/output');
const { operationalErrorEnvelope } = require('./commands/axi');
const { writeLastStdioError } = require('./lib/stdio-diagnostics');

// Detect gateway-passthrough mode: no subcommand means we're forwarding stdio
// as the MCP JSON-RPC channel. Writing a JSON envelope to stdout there would
// corrupt the protocol stream. Route errors to stderr instead.
const argv = process.argv.slice(2);
const KNOWN_COMMANDS = new Set(['status', 'doctor', 'tools', 'config', 'init', 'setup', 'whoami', 'uninstall', 'kb', 'clients', 'help', 'home', 'axi', 'llm', 'layout', 'update']);
const isInteractiveCommand = argv.length > 0 && KNOWN_COMMANDS.has(argv[0]);
const errorOut = isInteractiveCommand ? process.stdout : process.stderr;

main(argv)
    .then((code) => process.exit(code))
    .catch((err) => {
        if (!isInteractiveCommand) {
            writeLastStdioError({
                gatewayExePath: process.env.GENEXUS_MCP_GATEWAY_EXE || process.argv[1],
                exitCode: EXIT_CODES.ERROR,
                error: err && err.message ? err.message : 'Unhandled CLI failure.',
                stderrTail: err && err.stack ? err.stack : ''
            });
        }
        const envelope = operationalErrorEnvelope('Unhandled CLI failure.', EXIT_CODES.ERROR);
        envelope.meta = { ...(envelope.meta || {}), command: 'runtime' };
        writeStructured(errorOut, envelope, 'toon');
        process.exit(EXIT_CODES.ERROR);
    });
