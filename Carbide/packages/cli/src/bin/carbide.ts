#!/usr/bin/env node
// Carbide CLI entry point. Dispatches to build / run / validate commands.

import { ArgParseError, parseArgs } from "../args.js";
import { BUILD_ARG_SPEC, runBuild } from "../commands/build.js";
import { RUN_ARG_SPEC, runRun } from "../commands/run.js";
import { VALIDATE_ARG_SPEC, runValidate } from "../commands/validate.js";

// Carbide's C# logger emits info/debug/trace via `console.info` / `console.debug`, which
// Node pipes to stdout. That would clobber the CLI's JSON output, so redirect them to
// stderr here before any command boots the runtime. console.warn / console.error already
// go to stderr; console.log is left alone because user programs that call Console.WriteLine
// expect it to end up on the outer stdout (via Carbide's stream capture + pass-through).
const origInfo = console.info.bind(console);
const origDebug = console.debug.bind(console);
const redirectToStderr = (...args: unknown[]) => {
    process.stderr.write(args.map(String).join(" ") + "\n");
};
console.info = redirectToStderr as typeof console.info;
console.debug = redirectToStderr as typeof console.debug;
// Keep references alive so TS doesn't drop them (and so future debug hooks can restore).
void origInfo;
void origDebug;

const VERSION = "0.0.0";

const TOP_LEVEL_HELP = `\
Usage: carbide <command> [options]

Commands:
  build       Compile sources into PE + PDB bytes (optionally written to disk).
  run         Compile sources and execute the program.
  validate    Run Roslyn diagnostics only (no emit, no execution).

Options:
  --version   Print the CLI version and exit.
  --help      Print this message. Per-command help: carbide <command> --help.

See https://github.com/… for full documentation.
`;

async function main(argv: readonly string[]): Promise<number> {
    if (argv.length === 0 || argv[0] === "--help" || argv[0] === "-h" || argv[0] === "help") {
        process.stdout.write(TOP_LEVEL_HELP);
        return 0;
    }
    if (argv[0] === "--version" || argv[0] === "-V") {
        process.stdout.write(VERSION + "\n");
        return 0;
    }

    const command = argv[0];
    const rest = argv.slice(1);

    try {
        switch (command) {
            case "build": {
                const args = parseArgs(rest, BUILD_ARG_SPEC);
                return await runBuild(args);
            }
            case "run": {
                const args = parseArgs(rest, RUN_ARG_SPEC);
                return await runRun(args);
            }
            case "validate": {
                const args = parseArgs(rest, VALIDATE_ARG_SPEC);
                return await runValidate(args);
            }
            default:
                process.stderr.write(`carbide: unknown command '${command}'. Run 'carbide --help'.\n`);
                return 3;
        }
    } catch (err) {
        if (err instanceof ArgParseError) {
            process.stderr.write(`carbide: ${err.message}\n`);
            return 3;
        }
        // Unexpected error — surface but don't throw past `main` so exit code is controlled.
        const msg = err instanceof Error ? (err.stack ?? err.message) : String(err);
        process.stderr.write(`carbide: unexpected error:\n${msg}\n`);
        return 2;
    }
}

// Workaround: when the entry is invoked via the `bin` shim on Windows, `process.argv` starts
// with node + the shim. Node's convention is that user args begin at index 2.
const code = await main(process.argv.slice(2));
process.exit(code);
