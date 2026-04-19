// `carbide build` — compiles sources into PE + PDB bytes and writes them to --out.
// Since M9, when --project is used Carbide walks `<ProjectReference>` edges and builds the
// whole graph; sub-project PEs land alongside the root's in --out.

import path from "node:path";
import { CarbideSession, type Project } from "@carbide/core";
import { type ParsedArgs, lastString, stringList } from "../args.js";
import { deriveAssemblyName, readReferenceBytes, readSource, writeFileEnsuringDir } from "../io.js";
import { parseFormat, renderDiagnostic, renderAttributedDiagnostic, writeJson } from "../format.js";
import {
    runProjectGraphPipeline,
    compileGraphInOrder,
    attributeDiagnostics,
    handleProjectGraphError,
    type PipelineWarning,
} from "../project-file.js";
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
        if (projectPath) {
            return await runProjectModeBuild({
                session,
                projectPath,
                refs,
                outDir,
                format,
                skipDebug,
                nugetOptions: extractNugetOptions(args, "build"),
            });
        }

        // Source-flag mode — single project, no graph walk needed.
        const assemblyName = deriveAssemblyName(lastString(args, "assembly-name"), sources);
        const project: Project = session.createProject({ assemblyName });

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
                    warnings: [],
                    durationMs: result.durationMs,
                });
            }
            return 1;
        }

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
                warnings: [],
            });
        }
        return 0;
    } finally {
        await session.shutdown();
    }
}

interface ProjectModeBuildContext {
    session: CarbideSession;
    projectPath: string;
    refs: readonly string[];
    outDir: string | undefined;
    format: "json" | "human";
    skipDebug: boolean;
    nugetOptions: ReturnType<typeof extractNugetOptions>;
}

async function runProjectModeBuild(ctx: ProjectModeBuildContext): Promise<number> {
    const { session, projectPath, refs, outDir, format, skipDebug, nugetOptions } = ctx;
    let multi: Awaited<ReturnType<typeof runProjectGraphPipeline>>;
    try {
        multi = await runProjectGraphPipeline(session, projectPath, refs, nugetOptions);
    } catch (err) {
        return handleProjectGraphError(err, format);
    }

    // `--out -` (PE bytes to stdout) is incompatible with multi-project graphs because two
    // PEs would corrupt each other on the pipe. Reject early [D84, MSPROJ003].
    if (outDir === "-" && multi.subprojects.length > 1) {
        process.stderr.write(
            "carbide build: MSPROJ003: --out - cannot be used with a multi-project graph " +
                `(${multi.subprojects.length} projects reachable from ${projectPath}).\n`,
        );
        return 3;
    }

    const csprojWarnings = normaliseWarnings(multi.warnings);
    if (format === "human") {
        for (const w of multi.warnings) {
            const where = w.project ? path.basename(w.project) : "carbide";
            process.stderr.write(`${where}: ${w.severity} ${w.code}: ${w.message}\n`);
        }
        for (const sub of multi.subprojects) {
            if (sub.nugetLockWritten && sub.nugetLockPath) {
                process.stderr.write(`carbide: wrote ${sub.nugetLockPath}\n`);
            }
        }
    }

    const outcomes = await compileGraphInOrder(session, multi);

    const allAttributed = attributeDiagnostics(outcomes);
    const failed = outcomes.some((o) => (o.buildResult && !o.buildResult.success) || o.skipped);
    const root = multi.root;

    if (failed) {
        if (format === "human") {
            for (const d of allAttributed) {
                process.stderr.write(renderAttributedDiagnostic(d) + "\n");
            }
            for (const o of outcomes) {
                if (o.skipped) {
                    process.stderr.write(
                        `carbide: skipped ${path.basename(o.subproject.csprojPath)} due to upstream failure\n`,
                    );
                }
            }
        } else {
            writeJson({
                success: false,
                assemblyName: root.assemblyName,
                diagnostics: allAttributed,
                warnings: csprojWarnings,
                subprojects: outcomes.map((o) => ({
                    csproj: path.basename(o.subproject.csprojPath),
                    assemblyName: o.subproject.assemblyName,
                    success: o.buildResult ? o.buildResult.success : false,
                    skipped: o.skipped,
                })),
                durationMs: root.model ? undefined : undefined,
            });
        }
        return 1;
    }

    // All builds succeeded. Emit artefacts per-sub-project when --out is a directory. In
    // stdout mode (single-project only) fall through to writing the root's PE bytes.
    const emittedFiles: Array<{ assemblyName: string; pe: string; pdb: string | null }> = [];

    if (outDir === "-") {
        const rootOutcome = outcomes[outcomes.length - 1];
        if (!rootOutcome.buildResult || !rootOutcome.buildResult.pe) {
            throw new Error("BuildResult missing pe on success.");
        }
        process.stdout.write(Buffer.from(rootOutcome.buildResult.pe));
    } else if (outDir) {
        for (const o of outcomes) {
            if (!o.buildResult || !o.buildResult.pe) continue;
            const pePath = path.join(outDir, `${o.subproject.assemblyName}.dll`);
            await writeFileEnsuringDir(pePath, o.buildResult.pe);
            let pdbPath: string | null = null;
            if (!skipDebug && o.buildResult.pdb && o.buildResult.pdb.length > 0) {
                pdbPath = path.join(outDir, `${o.subproject.assemblyName}.pdb`);
                await writeFileEnsuringDir(pdbPath, o.buildResult.pdb);
            }
            emittedFiles.push({ assemblyName: o.subproject.assemblyName, pe: pePath, pdb: pdbPath });
        }
    }

    if (format === "human") {
        for (const f of emittedFiles) {
            process.stderr.write(`built ${f.pe}\n`);
            if (f.pdb) process.stderr.write(`built ${f.pdb}\n`);
        }
        if (!outDir) process.stderr.write(`built in-memory (no --out)\n`);
    } else if (outDir !== "-") {
        const rootFile = emittedFiles.find((f) => f.assemblyName === root.assemblyName);
        const rootOutcome = outcomes[outcomes.length - 1];
        writeJson({
            success: true,
            assemblyName: root.assemblyName,
            pe: rootFile?.pe ?? null,
            pdb: rootFile?.pdb ?? null,
            durationMs: rootOutcome.buildResult?.durationMs ?? 0,
            diagnostics: allAttributed,
            warnings: csprojWarnings,
            subprojects: outcomes.map((o) => ({
                csproj: path.basename(o.subproject.csprojPath),
                assemblyName: o.subproject.assemblyName,
                pe: emittedFiles.find((f) => f.assemblyName === o.subproject.assemblyName)?.pe ?? null,
                pdb: emittedFiles.find((f) => f.assemblyName === o.subproject.assemblyName)?.pdb ?? null,
                durationMs: o.buildResult?.durationMs ?? 0,
            })),
        });
    }
    return 0;
}

function normaliseWarnings(warnings: readonly PipelineWarning[]) {
    return warnings.map((w) => ({
        code: w.code,
        message: w.message,
        severity: w.severity,
        project: w.project ?? null,
    }));
}

const BUILD_HELP = `\
Usage: carbide build [options]

Compile C# source files into a .dll (and portable .pdb) without executing them.

Input modes (mutually exclusive):
  --project <path>.csproj  Parse a .csproj (MSBuild subset) and build per its options.
                           Walks <ProjectReference> edges and builds the whole graph
                           (M9): each sub-project's <AssemblyName>.dll lands in --out/.
  --source <path>          Source file. Repeatable. '-' reads one source from stdin.

Options:
  --ref <path>             Reference DLL. Repeatable. Honoured in both input modes.
                           In --project mode the flag attaches to the root project only.
  --out <dir>              Output directory. Writes <assembly-name>.dll + .pdb.
                           Pass '-' to write PE bytes to stdout (no PDB). Rejected for
                           multi-project graphs (MSPROJ003).
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
                           Applies to the root project only; sub-projects keep their own
                           lock files next to each sibling csproj.
  --no-lock-write          Skip writing the lock after a fresh resolve.
  --nuget-source <url>     Override the flat-container base URL (default: nuget.org).
  --allow-list-mode <mode> strict | advisory | off. Default: strict.

Exit codes:
  0  success
  1  compile errors (or MSPROJ001 cycle on multi-project graphs)
  2  i/o error
  3  unsupported flag combination (includes MSPROJ002 collision, MSPROJ003 --out -)
`;
