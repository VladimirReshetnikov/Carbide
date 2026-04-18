// `carbide build` — compiles sources into PE + PDB bytes and writes them to --out.

import path from "node:path";
import { CarbideSession } from "@carbide/core";
import { type ParsedArgs, lastString, stringList } from "../args.js";
import { deriveAssemblyName, readReferenceBytes, readSource, writeFileEnsuringDir } from "../io.js";
import { parseFormat, renderDiagnostic, writeJson } from "../format.js";

export const BUILD_ARG_SPEC = {
    strings: [
        "source",
        "ref",
        "out",
        "assembly-name",
        "target-framework",
        "format",
    ],
    booleans: ["no-debug", "help"],
} as const;

export async function runBuild(args: ParsedArgs): Promise<number> {
    if (args.flags.has("help")) {
        process.stdout.write(BUILD_HELP);
        return 0;
    }

    const sources = stringList(args, "source");
    if (sources.length === 0) {
        process.stderr.write("carbide build: at least one --source is required.\n");
        return 3;
    }

    const refs = stringList(args, "ref");
    const outDir = lastString(args, "out");
    const format = parseFormat(lastString(args, "format"));
    const assemblyName = deriveAssemblyName(lastString(args, "assembly-name"), sources);
    const skipDebug = args.flags.has("no-debug");

    const session = await CarbideSession.initializeAsync();
    try {
        const project = session.createProject({ assemblyName });

        for (const refPath of refs) {
            const { name, bytes } = await readReferenceBytes(refPath);
            const handle = session.addReference(bytes, name);
            project.addReference(handle);
        }

        for (const sourceSpec of sources) {
            const { path: docPath, code } = await readSource(sourceSpec);
            project.addSource(docPath, code);
        }

        const result = await project.build();

        if (!result.success) {
            if (format === "human") {
                for (const d of result.diagnostics) {
                    process.stderr.write(renderDiagnostic(d) + "\n");
                }
            } else {
                writeJson({
                    success: false,
                    assemblyName,
                    diagnostics: result.diagnostics,
                    durationMs: result.durationMs,
                });
            }
            return 1;
        }

        // Write artefacts to --out when specified.
        let pePath: string | undefined;
        let pdbPath: string | undefined;
        if (outDir === "-") {
            // --out - => PE bytes to stdout (no PDB). See M4 plan D47.
            if (!result.pe) throw new Error("BuildResult missing pe on success.");
            process.stdout.write(Buffer.from(result.pe));
        } else if (outDir) {
            if (!result.pe) throw new Error("BuildResult missing pe on success.");
            pePath = path.join(outDir, `${assemblyName}.dll`);
            await writeFileEnsuringDir(pePath, result.pe);
            if (!skipDebug && result.pdb && result.pdb.length > 0) {
                pdbPath = path.join(outDir, `${assemblyName}.pdb`);
                await writeFileEnsuringDir(pdbPath, result.pdb);
            }
        }

        if (format === "human") {
            if (pePath) process.stderr.write(`built ${pePath}\n`);
            if (pdbPath) process.stderr.write(`built ${pdbPath}\n`);
            if (!outDir) process.stderr.write(`built in-memory (no --out)\n`);
        } else if (outDir !== "-") {
            writeJson({
                success: true,
                assemblyName,
                pe: pePath ?? null,
                pdb: pdbPath ?? null,
                durationMs: result.durationMs,
                diagnostics: result.diagnostics,
            });
        }
        return 0;
    } finally {
        await session.shutdown();
    }
}

const BUILD_HELP = `\
Usage: carbide build [options]

Compile C# source files into a .dll (and portable .pdb) without executing them.

Options:
  --source <path>        Source file. Repeatable. '-' reads one source from stdin.
  --ref <path>           Reference DLL. Repeatable.
  --out <dir>            Output directory. Writes <assembly-name>.dll + .pdb.
                         Pass '-' to write PE bytes to stdout (no PDB).
  --assembly-name <n>    Assembly name. Default: basename of first source.
  --target-framework <t> Target framework (default: net10.0). Currently informational.
  --no-debug             Skip writing the .pdb.
  --format json|human    Output format (default: json).
  --help                 Print this message.

Exit codes:
  0  success
  1  compile errors
  2  i/o error
  3  unsupported flag combination
`;
