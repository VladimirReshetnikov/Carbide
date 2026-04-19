// `carbide run` — compiles sources and executes the program. Streams the program's
// stdout/stderr through the outer process. Since M9, --project walks `<ProjectReference>`
// edges and builds every sub-project before running the root.
//
// Note: program arguments after `--` are parsed by the CLI arg parser but are not
// forwarded into the runtime yet.

import path from "node:path";
import { CarbideSession, type Project, type RunOptions } from "@carbide/core";
import { type ParsedArgs, lastString, stringList } from "../args.js";
import { deriveAssemblyName, readReferenceBytes, readSource, readStdinSource } from "../io.js";
import { parseFormat, renderDiagnostic, renderAttributedDiagnostic, writeJson } from "../format.js";
import {
    runProjectGraphPipeline,
    compileGraphInOrder,
    attributeDiagnostics,
    handleProjectGraphError,
} from "../project-file.js";
import {
    NUGET_BOOLEAN_FLAGS,
    NUGET_STRING_FLAGS,
    extractNugetOptions,
} from "../nuget-options.js";
import {
    LOG_LEVEL_BOOLEAN_FLAGS,
    LOG_LEVEL_STRING_FLAGS,
    resolveLogLevel,
} from "../logging.js";

export const RUN_ARG_SPEC = {
    strings: [
        "source",
        "ref",
        "assembly-name",
        "format",
        "project",
        "stdin",
        ...NUGET_STRING_FLAGS,
        ...LOG_LEVEL_STRING_FLAGS,
    ],
    booleans: ["help", "scratch", ...NUGET_BOOLEAN_FLAGS, ...LOG_LEVEL_BOOLEAN_FLAGS],
    aliases: { v: "verbose", q: "quiet" },
} as const;

export async function runRun(args: ParsedArgs): Promise<number> {
    if (args.flags.has("help")) {
        process.stdout.write(RUN_HELP);
        return 0;
    }

    const sources = stringList(args, "source");
    const projectPath = lastString(args, "project");
    const scratch = args.flags.has("scratch");

    if (!projectPath && sources.length === 0) {
        process.stderr.write("carbide run: provide either --project <path>.csproj or at least one --source.\n");
        return 3;
    }
    if (projectPath && !scratch && (sources.length > 0 || lastString(args, "assembly-name"))) {
        process.stderr.write(
            "carbide run: --project is mutually exclusive with --source / --assembly-name (pass --scratch to combine).\n",
        );
        return 3;
    }
    if (!projectPath && scratch) {
        process.stderr.write("carbide run: --scratch requires --project.\n");
        return 3;
    }

    const refs = stringList(args, "ref");
    const format = parseFormat(lastString(args, "format"));
    const logLevel = resolveLogLevel(args);

    // U2: forward program arguments (after `--`) and stdin into the user program.
    const programArgs = args.programArgs;
    const stdinSpec = lastString(args, "stdin");
    const stdinContent = await readStdinSource(stdinSpec);
    const runOptions: RunOptions = { args: programArgs, stdin: stdinContent };
    const invocation = {
        args: [...programArgs],
        stdinBytes: stdinContent !== null ? Buffer.byteLength(stdinContent, "utf8") : 0,
    };

    const session = await CarbideSession.initializeAsync({ logLevel });
    try {
        if (projectPath) {
            return await runProjectModeRun({
                session,
                projectPath,
                refs,
                format,
                runOptions,
                invocation,
                extraRootSources: scratch ? sources : [],
                nugetOptions: extractNugetOptions(args, "run"),
            });
        }

        // Source-flag mode — single project.
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

        const result = await project.run(runOptions);

        if (!result.success) {
            if (result.diagnostics.length > 0) {
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
                        invocation,
                    });
                }
                return 1;
            }
            process.stderr.write(result.stdErr);
            if (format === "json") {
                writeJson({
                    success: false,
                    assemblyName,
                    stdOut: result.stdOut,
                    stdErr: result.stdErr,
                    uncaughtException: result.uncaughtException ?? null,
                    exitCode: result.exitCode ?? 1,
                    warnings: [],
                    durationMs: result.durationMs,
                    invocation,
                });
            } else {
                process.stdout.write(result.stdOut);
            }
            return result.exitCode && result.exitCode !== 0 ? result.exitCode : 1;
        }

        if (format === "human") {
            process.stdout.write(result.stdOut);
            if (result.stdErr) process.stderr.write(result.stdErr);
        } else {
            writeJson({
                success: true,
                assemblyName,
                stdOut: result.stdOut,
                stdErr: result.stdErr,
                exitCode: result.exitCode ?? 0,
                warnings: [],
                durationMs: result.durationMs,
                invocation,
            });
        }
        return result.exitCode ?? 0;
    } finally {
        await session.shutdown();
    }
}

interface ProjectModeRunContext {
    session: CarbideSession;
    projectPath: string;
    refs: readonly string[];
    format: "json" | "human";
    runOptions: RunOptions;
    invocation: { args: string[]; stdinBytes: number };
    extraRootSources: readonly string[];
    nugetOptions: ReturnType<typeof extractNugetOptions>;
}

async function runProjectModeRun(ctx: ProjectModeRunContext): Promise<number> {
    const { session, projectPath, refs, format, nugetOptions, runOptions, invocation, extraRootSources } = ctx;
    let multi: Awaited<ReturnType<typeof runProjectGraphPipeline>>;
    try {
        multi = await runProjectGraphPipeline(session, projectPath, refs, nugetOptions, {
            extraRootSources,
        });
    } catch (err) {
        return handleProjectGraphError(err, format);
    }

    const csprojWarnings = multi.warnings.map((w) => ({
        code: w.code,
        message: w.message,
        severity: w.severity,
        project: w.project ?? null,
    }));

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

    // Build every producer (skip the root). Each producer's PE registers on the session
    // so the root's compile-and-run sees it.
    const outcomes = await compileGraphInOrder(session, multi, { skipRoot: true });
    const rootAssembly = multi.root.assemblyName;
    const attributed = attributeDiagnostics(outcomes);

    // If any producer failed to build, surface its diagnostics and exit 1 — the root can't
    // run without its dependencies.
    const nonRootFailed = outcomes.some(
        (o) => !o.subproject.isRoot && (o.skipped || (o.buildResult && !o.buildResult.success)),
    );
    if (nonRootFailed) {
        if (format === "human") {
            for (const d of attributed) {
                process.stderr.write(renderAttributedDiagnostic(d) + "\n");
            }
        } else {
            writeJson({
                success: false,
                assemblyName: rootAssembly,
                diagnostics: attributed,
                warnings: csprojWarnings,
                durationMs: 0,
                invocation,
            });
        }
        return 1;
    }

    const result = await multi.root.project.run(runOptions);

    if (!result.success) {
        if (result.diagnostics.length > 0) {
            if (format === "human") {
                for (const d of result.diagnostics) {
                    process.stderr.write(renderDiagnostic(d) + "\n");
                }
            } else {
                writeJson({
                    success: false,
                    assemblyName: rootAssembly,
                    diagnostics: result.diagnostics,
                    warnings: csprojWarnings,
                    durationMs: result.durationMs,
                    invocation,
                });
            }
            return 1;
        }
        process.stderr.write(result.stdErr);
        if (format === "json") {
            writeJson({
                success: false,
                assemblyName: rootAssembly,
                stdOut: result.stdOut,
                stdErr: result.stdErr,
                uncaughtException: result.uncaughtException ?? null,
                exitCode: result.exitCode ?? 1,
                warnings: csprojWarnings,
                durationMs: result.durationMs,
                invocation,
            });
        } else {
            process.stdout.write(result.stdOut);
        }
        return result.exitCode && result.exitCode !== 0 ? result.exitCode : 1;
    }

    if (format === "human") {
        process.stdout.write(result.stdOut);
        if (result.stdErr) process.stderr.write(result.stdErr);
    } else {
        writeJson({
            success: true,
            assemblyName: rootAssembly,
            stdOut: result.stdOut,
            stdErr: result.stdErr,
            exitCode: result.exitCode ?? 0,
            warnings: csprojWarnings,
            durationMs: result.durationMs,
            invocation,
        });
    }
    return result.exitCode ?? 0;
}

const RUN_HELP = `\
Usage: carbide run [options] [-- <program args>...]

Compile C# sources and execute the program.

Input modes (mutually exclusive):
  --project <path>.csproj  Parse a .csproj and run per its options. Walks <ProjectReference>
                           edges; every sub-project is built before the root runs (M9).
  --source <path>          Source file. Repeatable. '-' reads one source from stdin.

Options:
  --ref <path>             Reference DLL. Repeatable.
  --assembly-name <n>      Assembly name. Rejected when --project is used.
  --stdin <path | ->       U2: feed the program's Console.In from a file or the CLI's
                           own stdin (-). Default: Console.In is disconnected.
  --format json|human      Output format (default: json).
  --help                   Print this message.

Notes:
  - U2: program arguments after -- are forwarded to the user program's
    Main(string[] args) parameter.

NuGet flags (only relevant with --project):
  --offline                Forbid network. Require cached bytes or a matching lock.
  --lock <path>            Override lock file path. Default: <projectDir>/carbide.lock.json.
  --no-lock-write          Skip writing the lock after a fresh resolve.
  --nuget-source <url>     Override the flat-container base URL (default: nuget.org).
  --allow-list-mode <mode> strict | advisory | off. Default: strict.
`;
