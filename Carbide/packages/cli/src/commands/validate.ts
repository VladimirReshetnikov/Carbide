// `carbide validate` — runs Roslyn diagnostics only, no emit or execution. Since M9 it
// walks `<ProjectReference>` edges and surfaces diagnostics for every sub-project, each
// attributed to its originating csproj.

import path from "node:path";
import { CarbideSession, type Project } from "@carbide/core";
import { type ParsedArgs, lastString, stringList } from "../args.js";
import { deriveAssemblyName, readReferenceBytes, readSource } from "../io.js";
import { parseFormat, renderDiagnostic, renderAttributedDiagnostic, writeJson } from "../format.js";
import {
    runProjectGraphPipeline,
    compileGraphInOrder,
    attributeDiagnosticsBySubproject,
    attachCs8802Hint,
    handleProjectGraphError,
    canonicalKeyOf,
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

export const VALIDATE_ARG_SPEC = {
    strings: [
        "source",
        "ref",
        "assembly-name",
        "format",
        "project",
        ...NUGET_STRING_FLAGS,
        ...LOG_LEVEL_STRING_FLAGS,
    ],
    booleans: ["help", "scratch", ...NUGET_BOOLEAN_FLAGS, ...LOG_LEVEL_BOOLEAN_FLAGS],
    aliases: { v: "verbose", q: "quiet" },
} as const;

export async function runValidate(args: ParsedArgs): Promise<number> {
    if (args.flags.has("help")) {
        process.stdout.write(VALIDATE_HELP);
        return 0;
    }

    const sources = stringList(args, "source");
    const projectPath = lastString(args, "project");
    const scratch = args.flags.has("scratch");

    if (!projectPath && sources.length === 0) {
        process.stderr.write("carbide validate: provide either --project <path>.csproj or at least one --source.\n");
        return 3;
    }
    if (projectPath && !scratch && (sources.length > 0 || lastString(args, "assembly-name"))) {
        process.stderr.write(
            "carbide validate: --project is mutually exclusive with --source / --assembly-name (pass --scratch to combine).\n",
        );
        return 3;
    }
    if (!projectPath && scratch) {
        process.stderr.write("carbide validate: --scratch requires --project.\n");
        return 3;
    }

    const refs = stringList(args, "ref");
    const format = parseFormat(lastString(args, "format"));
    const logLevel = resolveLogLevel(args);

    const session = await CarbideSession.initializeAsync({ logLevel });
    try {
        if (projectPath) {
            return await runProjectModeValidate({
                session,
                projectPath,
                refs,
                format,
                extraRootSources: scratch ? sources : [],
                nugetOptions: extractNugetOptions(args, "validate"),
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

        const diagnostics = await project.getDiagnostics();
        const hasErrors = diagnostics.some((d) => d.severity === "error");

        if (format === "human") {
            for (const d of diagnostics) {
                process.stderr.write(renderDiagnostic(d) + "\n");
            }
        } else {
            writeJson({
                success: !hasErrors,
                assemblyName,
                diagnostics,
                warnings: [],
            });
        }
        return hasErrors ? 1 : 0;
    } finally {
        await session.shutdown();
    }
}

interface ProjectModeValidateContext {
    session: CarbideSession;
    projectPath: string;
    refs: readonly string[];
    format: "json" | "human";
    extraRootSources: readonly string[];
    nugetOptions: ReturnType<typeof extractNugetOptions>;
}

async function runProjectModeValidate(ctx: ProjectModeValidateContext): Promise<number> {
    const { session, projectPath, refs, format, nugetOptions, extraRootSources } = ctx;
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

    // Validate every sub-project separately — the root's diagnostics need the producers'
    // PEs on the session, so we compile producers first and then run getDiagnostics()
    // per-sub-project. Producer failures surface as diagnostics.
    await compileGraphInOrder(session, multi, { skipRoot: true });

    const diagnosticsByKey = new Map<string, import("@carbide/core").Diagnostic[]>();
    for (const sub of multi.subprojects) {
        const diags = await sub.project.getDiagnostics();
        diagnosticsByKey.set(canonicalKeyOf(sub.csprojPath), diags);
    }

    const attributed = attachCs8802Hint(
        attributeDiagnosticsBySubproject(multi, diagnosticsByKey),
        multi.root.csprojPath,
    );
    const hasErrors = attributed.some((d) => d.severity === "error");

    if (format === "human") {
        for (const d of attributed) {
            process.stderr.write(renderAttributedDiagnostic(d) + "\n");
        }
    } else {
        writeJson({
            success: !hasErrors,
            assemblyName: multi.root.assemblyName,
            diagnostics: attributed,
            warnings: csprojWarnings,
        });
    }
    return hasErrors ? 1 : 0;
}

const VALIDATE_HELP = `\
Usage: carbide validate [options]

Run Roslyn diagnostics over the project without emitting or executing. Exit code 0 when no
error-severity diagnostics exist; non-zero otherwise.

Input modes (mutually exclusive):
  --project <path>.csproj  Parse a .csproj and validate per its options. Walks
                           <ProjectReference> edges; every sub-project is validated (M9).
  --source <path>          Source file. Repeatable. '-' reads one source from stdin.

Options:
  --ref <path>             Reference DLL. Repeatable.
  --assembly-name <n>      Assembly name. Rejected when --project is used.
  --format json|human      Output format (default: json).
  --help                   Print this message.

NuGet flags (only relevant with --project):
  --offline                Forbid network. Require cached bytes or a matching lock.
  --lock <path>            Override lock file path. Default: <projectDir>/carbide.lock.json.
  --no-lock-write          Skip writing the lock after a fresh resolve.
  --nuget-source <url>     Override the flat-container base URL (default: nuget.org).
  --allow-list-mode <mode> strict | advisory | off. Default: strict.
`;
