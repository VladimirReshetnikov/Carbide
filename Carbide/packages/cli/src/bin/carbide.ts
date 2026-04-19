#!/usr/bin/env node
// Carbide CLI entry point. Dispatches to build / run / validate commands.

import { parseArgs } from "../args.js";
import { BUILD_ARG_SPEC, runBuild } from "../commands/build.js";
import { RUN_ARG_SPEC, runRun } from "../commands/run.js";
import { VALIDATE_ARG_SPEC, runValidate } from "../commands/validate.js";
import { AUDIT_ARG_SPEC, runAudit } from "../commands/audit.js";
import { TREE_ARG_SPEC, runTree } from "../commands/tree.js";
import { handleCliFailure } from "../errors.js";

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
  audit       Print a structured read-only report of a --project graph + NuGet state.
  tree        Render the --project graph as an ASCII tree.

Options:
  --version   Print the CLI version and exit.
  --help      Print this message. Per-command help: carbide <command> --help.
  --verbose   Enable info/trace logging (alias: -v).
  --quiet     Suppress warnings to stderr (alias: -q).
  --format    json (default) | human.

Exit codes:
  0  success
  1  compile errors / ProjectReference cycle
  2  I/O or internal error
  3  unsupported flag combination / AssemblyName collision / stdout-pipe + multi-project
  4  NuGet policy refusal (allow-list / safety)
  5  NuGet network or cache miss

See https://github.com/… for full documentation.
`;

/**
 * Extract `--format` and `--verbose` hints before full arg parsing. Lets the top-level
 * catch format error payloads correctly even when the error fires during `parseArgs`.
 */
function sniffEarlyFlags(argv: readonly string[]): { format: "json" | "human"; verbose: boolean } {
    let format: "json" | "human" = "json";
    let verbose = false;
    for (let i = 0; i < argv.length; i++) {
        const t = argv[i];
        if (t === "--format" && i + 1 < argv.length) {
            const v = argv[i + 1];
            if (v === "human" || v === "json") format = v;
        } else if (t === "--format=human") format = "human";
        else if (t === "--format=json") format = "json";
        else if (t === "--verbose" || t === "-v") verbose = true;
    }
    return { format, verbose };
}

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
    const early = sniffEarlyFlags(rest);

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
            case "audit": {
                const args = parseArgs(rest, AUDIT_ARG_SPEC);
                return await runAudit(args);
            }
            case "tree": {
                const args = parseArgs(rest, TREE_ARG_SPEC);
                return await runTree(args);
            }
            default:
                process.stderr.write(`carbide: unknown command '${command}'. Run 'carbide --help'.\n`);
                return 3;
        }
    } catch (err) {
        return handleCliFailure(err, early.format, { verbose: early.verbose });
    }
}

// Workaround: when the entry is invoked via the `bin` shim on Windows, `process.argv` starts
// with node + the shim. Node's convention is that user args begin at index 2.
const code = await main(process.argv.slice(2));
process.exit(code);
