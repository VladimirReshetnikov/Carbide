// `carbide build` — compiles sources into PE + PDB bytes and writes them to --out.

import path from "node:path";
import { CarbideSession, type Project } from "@carbide/core";
import { type ParsedArgs, lastString, stringList } from "../args.js";
import { deriveAssemblyName, readReferenceBytes, readSource, writeFileEnsuringDir } from "../io.js";
import { parseFormat, renderDiagnostic, writeJson } from "../format.js";
import { runCsprojPipeline } from "../project-file.js";
import {
    NUGET_BOOLEAN_FLAGS,
    NUGET_STRING_FLAGS,
    extractNugetOptions,
} from "../nuget-options.js";

export const BUILD_ARG_SPEC = {
    strings: [
        "source",
        "ref",
        "out",
        "assembly-name",
        "target-framework",
        "format",
        "project",
        ...NUGET_STRING_FLAGS,
    ],
    booleans: ["no-debug", "help", ...NUGET_BOOLEAN_FLAGS],
} as const;

export async function runBuild(args: ParsedArgs): Promise<number> {
    if (args.flags.has("help")) {
        process.stdout.write(BUILD_HELP);
        return 0;
    }

    const sources = stringList(args, "source");
    const projectPath = lastString(args, "project");

    if (!projectPath && sources.length === 0) {
        process.stderr.write("carbide build: provide either --project <path>.csproj or at least one --source.\n");
        return 3;
    }
    if (
        projectPath &&
        (sources.length > 0 || lastString(args, "assembly-name") || lastString(args, "target-framework"))
    ) {
        process.stderr.write(
            "carbide build: --project is mutually exclusive with --source / --assembly-name / --target-framework (M5 D59).\n",
        );
        return 3;
    }

    const refs = stringList(args, "ref");
    const outDir = lastString(args, "out");
    const format = parseFormat(lastString(args, "format"));
    const skipDebug = args.flags.has("no-debug");

    const session = await CarbideSession.initializeAsync();
    try {
        let project: Project;
        let assemblyName: string;
        let csprojWarnings: Array<{ code: string; message: string; severity: string }> = [];

        if (projectPath) {
            const nugetOptions = extractNugetOptions(args, "build");
            const pipeline = await runCsprojPipeline(session, projectPath, refs, nugetOptions);
            project = pipeline.project;
            const modelAsmName = pipeline.model.properties.assemblyName as string | undefined;
            assemblyName =
                modelAsmName && modelAsmName.length > 0
                    ? modelAsmName
                    : path.basename(pipeline.model.projectPath, path.extname(pipeline.model.projectPath));
            csprojWarnings = pipeline.warnings.map((w) => ({
                code: w.code,
                message: w.message,
                severity: w.severity,
            }));
            if (format === "human") {
                for (const w of pipeline.warnings) {
                    process.stderr.write(`carbide: ${w.severity} ${w.code}: ${w.message}\n`);
                }
                if (pipeline.nugetLockWritten && pipeline.nugetLockPath) {
                    process.stderr.write(`carbide: wrote ${pipeline.nugetLockPath}\n`);
                }
            }
        } else {
            assemblyName = deriveAssemblyName(lastString(args, "assembly-name"), sources);
            project = session.createProject({ assemblyName });

            for (const refPath of refs) {
                const { name, bytes } = await readReferenceBytes(refPath);
                const handle = session.addReference(bytes, name);
                project.addReference(handle);
            }

            for (const sourceSpec of sources) {
                const { path: docPath, code } = await readSource(sourceSpec);
                project.addSource(docPath, code);
            }
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
                    warnings: csprojWarnings,
                    durationMs: result.durationMs,
                });
            }
            return 1;
        }

        // Write artefacts to --out when specified.
        let pePath: string | undefined;
        let pdbPath: string | undefined;
        if (outDir === "-") {
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
                warnings: csprojWarnings,
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

Input modes (mutually exclusive):
  --project <path>.csproj  Parse a .csproj (MSBuild subset) and build per its options.
  --source <path>          Source file. Repeatable. '-' reads one source from stdin.

Options:
  --ref <path>             Reference DLL. Repeatable. Honoured in both input modes.
  --out <dir>              Output directory. Writes <assembly-name>.dll + .pdb.
                           Pass '-' to write PE bytes to stdout (no PDB).
  --assembly-name <n>      Assembly name. Default: basename of first source.
                           Rejected when --project is used.
  --target-framework <t>   Target framework (default: net10.0). Currently informational.
                           Rejected when --project is used.
  --no-debug               Skip writing the .pdb.
  --format json|human      Output format (default: json).
  --help                   Print this message.

NuGet flags (only relevant with --project):
  --offline                Forbid network. Require cached bytes or a matching lock.
  --lock <path>            Override lock file path. Default: <projectDir>/carbide.lock.json.
                           When the file exists it is replayed verbatim.
  --no-lock-write          Skip writing the lock after a fresh resolve.
  --nuget-source <url>     Override the flat-container base URL (default: nuget.org).
  --allow-list-mode <mode> strict | advisory | off. Default: strict.

Exit codes:
  0  success
  1  compile errors
  2  i/o error
  3  unsupported flag combination
`;
