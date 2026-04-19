// `carbide tree` — render the project graph as an ASCII tree. Reads the same data the
// `audit` command does (via a read-only `runProjectGraphPipeline`) and formats it as a
// terminal-friendly nested view of sub-projects and their direct NuGet dependencies.

import path from "node:path";
import { CarbideSession } from "@carbide/core";
import { type ParsedArgs, lastString, stringList } from "../args.js";
import { parseFormat } from "../format.js";
import {
    runProjectGraphPipeline,
    handleProjectGraphError,
    canonicalKeyOf,
    type MultiProjectPipelineResult,
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

export const TREE_ARG_SPEC = {
    strings: ["project", "format", ...NUGET_STRING_FLAGS, ...LOG_LEVEL_STRING_FLAGS],
    booleans: ["help", ...NUGET_BOOLEAN_FLAGS, ...LOG_LEVEL_BOOLEAN_FLAGS],
    aliases: { v: "verbose", q: "quiet" },
} as const;

export async function runTree(args: ParsedArgs): Promise<number> {
    if (args.flags.has("help")) {
        process.stdout.write(TREE_HELP);
        return 0;
    }

    const projectPath = lastString(args, "project");
    if (!projectPath) {
        process.stderr.write("carbide tree: --project <path>.csproj is required.\n");
        return 3;
    }

    const format = parseFormat(lastString(args, "format"));
    const logLevel = resolveLogLevel(args);
    const nugetOptions = extractNugetOptions(args, "tree");

    const session = await CarbideSession.initializeAsync({ logLevel });
    try {
        let multi: MultiProjectPipelineResult;
        try {
            // skipLockWrite: tree is a viewer, not a builder; do not side-effect lock files.
            multi = await runProjectGraphPipeline(session, projectPath, stringList(args, "ref"), nugetOptions, {
                skipLockWrite: true,
            });
        } catch (err) {
            return handleProjectGraphError(err, format);
        }

        process.stdout.write(renderTree(multi));
        return 0;
    } finally {
        await session.shutdown();
    }
}

/** Render the project graph as an ASCII tree rooted at `multi.root`. */
export function renderTree(multi: MultiProjectPipelineResult): string {
    const byKey = new Map<string, SubprojectPipelineResult>();
    for (const sub of multi.subprojects) byKey.set(canonicalKeyOf(sub.csprojPath), sub);

    const visited = new Set<string>();
    const lines: string[] = [];

    const walk = (key: string, prefix: string, isLast: boolean, depth: number) => {
        const sub = byKey.get(key);
        if (!sub) return;
        const connector = depth === 0 ? "" : isLast ? "└── " : "├── ";
        const header = `${describe(sub)}`;
        const alreadyShown = visited.has(key);
        if (alreadyShown) {
            lines.push(`${prefix}${connector}${header} <already shown above>`);
            return;
        }
        visited.add(key);
        lines.push(`${prefix}${connector}${header}`);

        const nextPrefix = depth === 0 ? "" : prefix + (isLast ? "    " : "│   ");

        // Direct ProjectReferences (in graph-walk order within the sub-project).
        const nodeIdx = multi.subprojects.indexOf(sub);
        const node = multi.graph.order[nodeIdx];
        const directRefs = node.projectReferences;

        // Direct PackageReferences (printed as leaves after the project-children).
        const pkgs = sub.model.packageReferences;

        const childCount = directRefs.length + pkgs.length;
        let idx = 0;
        for (const refPath of directRefs) {
            const last = idx === childCount - 1;
            walk(canonicalKeyOf(refPath), nextPrefix, last, depth + 1);
            idx++;
        }
        for (const pkg of pkgs) {
            const last = idx === childCount - 1;
            const pconn = last ? "└── " : "├── ";
            const resolved = sub.nugetGraph?.lock.packages.find((rp) => rp.id === pkg.id);
            const version = resolved ? resolved.version : pkg.version ?? "(no version)";
            lines.push(`${nextPrefix}${pconn}[nuget] ${pkg.id} ${version}`);
            idx++;
        }
    };

    walk(canonicalKeyOf(multi.root.csprojPath), "", true, 0);
    return lines.join("\n") + "\n";
}

function describe(sub: SubprojectPipelineResult): string {
    const tfm = sub.model.evaluationTrace.targetFramework.selected ?? "net10.0";
    return `${path.basename(sub.csprojPath)} (${sub.assemblyName}, ${tfm})`;
}

const TREE_HELP = `\
Usage: carbide tree --project <path>.csproj [options]

Render the <ProjectReference> graph as an ASCII tree, with each sub-project's direct
<PackageReference>s shown as [nuget] leaves. Read-only; never writes carbide.lock.json
or emits artefacts.

Options:
  --project <path>.csproj  Required. Root csproj.
  --format json|human      Output format (default: human). json mode emits the same
                           structured payload as 'carbide audit'.
  --verbose / -v           Enable info/trace runtime logging.
  --quiet / -q             Suppress warnings to stderr.
  --log-level <level>      Explicit runtime log level.
  --help                   Print this message.

NuGet flags (behave as for build/run/validate):
  --offline, --lock <path>, --nuget-source <url>, --allow-list-mode <mode>.
`;
