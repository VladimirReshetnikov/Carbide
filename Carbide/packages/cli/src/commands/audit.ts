// `carbide audit` — read-only inspection of a csproj + its project graph + NuGet
// resolution. Never emits PE, never writes carbide.lock.json (unless `--write-lock`),
// never runs the program. Useful for "show me what Carbide sees" debugging and pre-
// commit lint workflows.

import path from "node:path";
import { CarbideSession } from "@carbide/core";
import { type ParsedArgs, lastString, stringList } from "../args.js";
import { parseFormat, writeJson } from "../format.js";
import {
    runProjectGraphPipeline,
    handleProjectGraphError,
    type MultiProjectPipelineResult,
    type PipelineWarning,
    type SubprojectPipelineResult,
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

export const AUDIT_ARG_SPEC = {
    strings: ["project", "format", "ref", ...NUGET_STRING_FLAGS, ...LOG_LEVEL_STRING_FLAGS],
    booleans: [
        "help",
        "write-lock",
        ...NUGET_BOOLEAN_FLAGS,
        ...LOG_LEVEL_BOOLEAN_FLAGS,
    ],
    aliases: { v: "verbose", q: "quiet" },
} as const;

export async function runAudit(args: ParsedArgs): Promise<number> {
    if (args.flags.has("help")) {
        process.stdout.write(AUDIT_HELP);
        return 0;
    }

    const projectPath = lastString(args, "project");
    if (!projectPath) {
        process.stderr.write("carbide audit: --project <path>.csproj is required.\n");
        return 3;
    }

    const format = parseFormat(lastString(args, "format"));
    const logLevel = resolveLogLevel(args);
    const refs = stringList(args, "ref");
    const writeLock = args.flags.has("write-lock");
    const nugetOptions = extractNugetOptions(args, "audit");

    const session = await CarbideSession.initializeAsync({ logLevel });
    try {
        let multi: MultiProjectPipelineResult;
        try {
            multi = await runProjectGraphPipeline(session, projectPath, refs, nugetOptions, {
                skipLockWrite: !writeLock,
            });
        } catch (err) {
            // Graph errors return a stable exit code; non-graph errors (NuGet refusal,
            // I/O failure, etc.) fall through to the top-level handler in bin/carbide.ts.
            return handleProjectGraphError(err, format);
        }

        if (format === "json") {
            writeJson(buildAuditReport(multi));
        } else {
            process.stdout.write(renderHumanReport(multi));
        }
        return 0;
    } finally {
        await session.shutdown();
    }
}

function buildAuditReport(multi: MultiProjectPipelineResult): Record<string, unknown> {
    return {
        success: true,
        warnings: serializeWarnings(multi.warnings),
        graph: {
            root: multi.root.csprojPath,
            order: multi.subprojects.map((sub, i) => {
                const node = multi.graph.order[i];
                return {
                    csproj: sub.csprojPath,
                    assemblyName: sub.assemblyName,
                    isRoot: sub.isRoot,
                    projectReferences: node.projectReferences,
                    transitiveClosure: [...node.transitiveClosure].sort(),
                };
            }),
        },
        subprojects: multi.subprojects.map((sub) => serializeSubproject(sub)),
    };
}

function serializeSubproject(sub: SubprojectPipelineResult): Record<string, unknown> {
    const m = sub.model;
    return {
        csproj: sub.csprojPath,
        assemblyName: sub.assemblyName,
        targetFramework: m.evaluationTrace.targetFramework.selected,
        properties: m.properties,
        sourceFiles: [...sub.sourcePaths].sort(),
        packageReferences: m.packageReferences,
        resolvedPackages: sub.nugetGraph
            ? sub.nugetGraph.lock.packages.map((p) => ({
                id: p.id,
                version: p.version,
                sha256: p.sha256,
            }))
            : [],
        nugetLockPath: sub.nugetLockPath,
        nugetLockWritten: sub.nugetLockWritten,
        compileItems: truncateCompileTrace(m.evaluationTrace.compileItems),
    };
}

/**
 * U3 D127-ish cap: the `compileItems.resolved` trace can be long for real projects.
 * Cap per-project at 100 entries and set `truncated: true` so consumers know to go
 * direct to `@carbide/msbuild-lite` if they need more.
 */
function truncateCompileTrace(
    compileItems: import("@carbide/msbuild-lite").EvaluationTrace["compileItems"],
): Record<string, unknown> {
    const cap = 100;
    const resolved = compileItems.resolved;
    return {
        defaultIncludeEnabled: compileItems.defaultIncludeEnabled,
        operations: compileItems.operations,
        resolved: resolved.slice(0, cap),
        resolvedTruncated: resolved.length > cap,
        resolvedTotal: resolved.length,
    };
}

function serializeWarnings(warnings: readonly PipelineWarning[]) {
    return warnings.map((w) => ({
        code: w.code,
        message: w.message,
        severity: w.severity,
        project: w.project ?? null,
    }));
}

function renderHumanReport(multi: MultiProjectPipelineResult): string {
    const lines: string[] = [];
    const rootBase = (p: string) => path.basename(p);

    lines.push(`project: ${rootBase(multi.root.csprojPath)} (${multi.root.assemblyName}, ${multi.root.model.evaluationTrace.targetFramework.selected ?? "net10.0"})`);
    for (const sub of multi.subprojects) {
        if (sub.isRoot) continue;
        lines.push(`  <- ${rootBase(sub.csprojPath)} (${sub.assemblyName}, ${sub.model.evaluationTrace.targetFramework.selected ?? "net10.0"})`);
    }

    lines.push("sources (per project):");
    for (const sub of multi.subprojects) {
        const srcs = [...sub.sourcePaths].sort().join(", ");
        lines.push(`  ${rootBase(sub.csprojPath)}: ${srcs || "(none)"}`);
    }

    lines.push("packages (per project):");
    for (const sub of multi.subprojects) {
        const refs = sub.model.packageReferences
            .map((p) => {
                const resolved = sub.nugetGraph?.lock.packages.find((rp) => rp.id === p.id);
                return resolved
                    ? `${p.id} ${p.version} [resolved: ${resolved.version}]`
                    : `${p.id} ${p.version ?? "(no version)"} [unresolved]`;
            })
            .join(", ");
        lines.push(`  ${rootBase(sub.csprojPath)}: ${refs || "(none)"}`);
    }

    if (multi.warnings.length > 0) {
        lines.push("warnings:");
        for (const w of multi.warnings) {
            const where = w.project ? rootBase(w.project) : "carbide";
            lines.push(`  ${where}: ${w.severity} ${w.code}: ${w.message}`);
        }
    }

    return lines.join("\n") + "\n";
}

const AUDIT_HELP = `\
Usage: carbide audit --project <path>.csproj [options]

Print a structured, read-only report of everything Carbide sees in a --project-driven
build: parsed csproj properties, the reachable <ProjectReference> graph, sources per
sub-project, NuGet resolution (when not running offline-only), and aggregated warnings.
No compilation runs; no artefacts are emitted.

Options:
  --project <path>.csproj  Required. Root csproj to audit.
  --ref <path>             Reference DLL. Repeatable. Attached to the root only.
  --format json|human      Output format (default: json).
  --write-lock             Opt in to writing carbide.lock.json during resolution.
                           Default is read-only (no lock writes).
  --verbose / -v           Enable info/trace runtime logging.
  --quiet / -q             Suppress warnings to stderr.
  --log-level <level>      Explicit runtime log level.
  --help                   Print this message.

NuGet flags (behave as for build/run/validate):
  --offline, --lock <path>, --no-lock-write, --nuget-source <url>, --allow-list-mode <mode>.

Exit codes match build/run/validate (see 'carbide --help').
`;
