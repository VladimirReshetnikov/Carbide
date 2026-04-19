// M9: project-graph module. Pure-TS orchestration that walks `<ProjectReference>` edges
// starting from a root .csproj, parses every reachable project, detects cycles and
// AssemblyName collisions, and emits a topologically ordered build plan (leaves first,
// root last). See [carbide-M9-detailed-plan §3, §5 D78].
//
// The module mutates no CarbideSession state — it only produces a plan. Orchestration
// (per-project NuGet resolution, Carbide Project creation, sibling-PE attachment) lives
// in `project-file.ts`, which consumes the graph.

import path from "node:path";
import { parseCsproj, type ProjectModel, type Warning } from "@carbide/msbuild-lite";

/** One node in the project graph: a `.csproj` that participates in the build. */
export interface ProjectNode {
    /** Canonical absolute path to the .csproj (verbatim casing preserved for diagnostics). */
    csprojPath: string;
    /** Parsed model (M5 shape). */
    model: ProjectModel;
    /** Canonical absolute paths to direct `<ProjectReference>` targets. */
    projectReferences: string[];
    /**
     * Canonical *keys* (via {@link canonicalKey}) of every project in this node's transitive
     * closure via `<ProjectReference>` edges. Populated after the walk and cycle check finish.
     */
    transitiveClosure: Set<string>;
    /** AssemblyName resolved from the model (or the csproj filename when absent). */
    assemblyName: string;
    /** True when this is the root node (the `--project` argument). */
    isRoot: boolean;
}

export interface ProjectGraph {
    /** All nodes keyed by canonical csproj path (lowercase on Windows, verbatim elsewhere). */
    nodes: ReadonlyMap<string, ProjectNode>;
    /** Topological order: leaves first, root last. */
    order: readonly ProjectNode[];
    /** Warnings emitted during graph construction (csproj parse warnings, MSPROJ010, etc.). */
    warnings: GraphWarning[];
    /** Canonical key of the root node (lookup in `nodes`). */
    rootKey: string;
}

export interface GraphWarning {
    code: string;
    message: string;
    severity: "warning" | "error";
    category: string;
    /** Canonical csproj path the warning attributes to, when applicable. */
    project?: string;
}

export interface BuildProjectGraphOptions {
    /** Forwarded to `parseCsproj` per sub-project. Defaults to `"Debug"`. */
    configuration?: string;
}

/** Thrown when the graph contains a `<ProjectReference>` cycle. */
export class ProjectGraphCycleError extends Error {
    constructor(public readonly cyclePath: readonly string[]) {
        super(`MSPROJ001: ProjectReference cycle detected: ${cyclePath.join(" -> ")}`);
        this.name = "ProjectGraphCycleError";
    }
}

/** Thrown when two projects in the graph share the same AssemblyName. */
export class ProjectGraphNameCollisionError extends Error {
    constructor(
        public readonly assemblyName: string,
        public readonly csprojPaths: readonly string[],
    ) {
        super(
            `MSPROJ002: AssemblyName '${assemblyName}' claimed by multiple projects: ${csprojPaths.join(", ")}`,
        );
        this.name = "ProjectGraphNameCollisionError";
    }
}

/** Thrown when a `<ProjectReference Include="..."/>` target csproj doesn't exist. */
export class ProjectReferenceNotFoundError extends Error {
    constructor(
        public readonly referrerPath: string,
        public readonly includeAttr: string,
        public readonly resolvedPath: string,
    ) {
        super(
            `MSPROJ004: ProjectReference target not found: '${includeAttr}' ` +
                `(resolved to '${resolvedPath}') referenced from '${referrerPath}'`,
        );
        this.name = "ProjectReferenceNotFoundError";
    }
}

/**
 * Build the project graph starting at `rootCsprojPath`. Walks `<ProjectReference>` edges
 * transitively, parsing each reachable csproj exactly once. Detects cycles and AssemblyName
 * collisions before returning — both are hard errors (thrown from this function).
 *
 * Returns a frozen plan: `order` lists sub-projects leaves-first, so a consumer can build
 * each in turn with its producers' PEs already registered on the session.
 */
export async function buildProjectGraph(
    rootCsprojPath: string,
    options: BuildProjectGraphOptions = {},
): Promise<ProjectGraph> {
    const rootAbs = path.resolve(rootCsprojPath);
    const rootKey = canonicalKey(rootAbs);

    const nodes = new Map<string, ProjectNode>();
    const warnings: GraphWarning[] = [];

    // DFS walk from the root, parsing each reachable csproj exactly once. We canonicalise
    // the map key so the diamond A→B,A→C,B→D,C→D yields four nodes, not five.
    const toVisit: Array<{ absPath: string; isRoot: boolean }> = [{ absPath: rootAbs, isRoot: true }];
    while (toVisit.length > 0) {
        const { absPath, isRoot } = toVisit.pop()!;
        const key = canonicalKey(absPath);
        if (nodes.has(key)) continue;

        let model: ProjectModel;
        try {
            model = await parseCsproj(absPath, { configuration: options.configuration ?? "Debug" });
        } catch (err) {
            if (isNodeFsNotFoundError(err)) {
                // The root itself not existing: surface as MSPROJ004 too, with a self-pointing
                // referrer path — the top-level CLI already validates the --project path, so this
                // branch is defensive.
                throw new ProjectReferenceNotFoundError(absPath, absPath, absPath);
            }
            throw err;
        }

        // MSBLITE* warnings from parseCsproj flow into the graph's unified warnings stream,
        // each attributed to the sub-project they were raised against. MSBLITE013 and
        // MSBLITE014 are the two codes M6 / M9 respectively *consume* and are suppressed
        // here — the corresponding resolver re-emits MSBLITE013 per-sub-project in
        // `project-file.ts` when resolution doesn't end up running.
        for (const w of model.warnings) {
            if (w.code === "MSBLITE014") continue;
            if (w.code === "MSBLITE013") continue;
            warnings.push({
                code: w.code,
                message: w.message,
                severity: w.severity,
                category: w.category,
                project: absPath,
            });
        }

        const assemblyName = deriveAssemblyName(model);
        const projectReferences = dedupePreserveOrder(
            model.projectReferences.map((p) => path.resolve(p)),
        );

        nodes.set(key, {
            csprojPath: absPath,
            model,
            projectReferences,
            transitiveClosure: new Set<string>(),
            assemblyName,
            isRoot,
        });

        for (const refAbs of projectReferences) {
            if (!nodes.has(canonicalKey(refAbs))) {
                toVisit.push({ absPath: refAbs, isRoot: false });
            }
        }
    }

    // Verify every referenced csproj actually exists on disk. parseCsproj throws ENOENT when
    // invoked on a missing path, so the "is this file present?" signal already surfaced for
    // directly-reached nodes above; the guard here catches cases where a referrer's resolved
    // path was never pushed (would be a walker bug) and also normalises the error wrapping.
    for (const node of nodes.values()) {
        for (const refAbs of node.projectReferences) {
            if (!nodes.has(canonicalKey(refAbs))) {
                // Find the include attribute that produced this path for the error message.
                const include = findIncludeAttrForResolvedPath(node, refAbs) ?? refAbs;
                throw new ProjectReferenceNotFoundError(node.csprojPath, include, refAbs);
            }
        }
    }

    // AssemblyName collision check — raise before any compilation can start clobbering
    // output artefacts. See D85.
    const assemblyBuckets = new Map<string, string[]>();
    for (const node of nodes.values()) {
        const bucket = assemblyBuckets.get(node.assemblyName);
        if (bucket) {
            bucket.push(node.csprojPath);
        } else {
            assemblyBuckets.set(node.assemblyName, [node.csprojPath]);
        }
    }
    for (const [name, paths] of assemblyBuckets) {
        if (paths.length > 1) {
            throw new ProjectGraphNameCollisionError(name, paths);
        }
    }

    // Topological sort + cycle detection via DFS post-order. The caller sees leaves first
    // (M9.3); the root is the last element.
    const order = topologicalSort(rootKey, nodes);

    // Populate transitive closures (derived from the projectReferences edges). Used by the
    // pipeline to decide which sibling PEs to attach when compiling a given sub-project.
    populateTransitiveClosures(nodes);

    // Cross-project TFM compatibility (D89) — warning-only for M9. The compatibility ladder
    // in @carbide/nuget's tfm-compat is not re-exported here; we check the coarse "first-listed
    // TFM" per project and flag only gross mismatches (net10.0 root + net472 sibling).
    for (const node of nodes.values()) {
        if (node.isRoot) continue;
        const rootNode = nodes.get(rootKey)!;
        const rootTfm = firstTfm(rootNode);
        const nodeTfm = firstTfm(node);
        if (rootTfm && nodeTfm && isGrossTfmMismatch(rootTfm, nodeTfm)) {
            warnings.push({
                code: "MSPROJ010",
                message:
                    `Sub-project '${node.csprojPath}' targets '${nodeTfm}' which is incompatible ` +
                    `with the root project's '${rootTfm}'. Build may fail at compile time.`,
                severity: "warning",
                category: "project-reference",
                project: node.csprojPath,
            });
        }
    }

    return { nodes, order, warnings, rootKey };
}

/** Canonicalise a path for use as a map key. Casing-insensitive on Windows only. */
export function canonicalKey(absPath: string): string {
    return process.platform === "win32" ? absPath.toLowerCase() : absPath;
}

/** AssemblyName fallback: csproj filename without extension when `<AssemblyName>` is absent. */
function deriveAssemblyName(model: ProjectModel): string {
    const explicit = model.properties.assemblyName as string | undefined;
    if (explicit && explicit.length > 0) return explicit;
    return path.basename(model.projectPath, path.extname(model.projectPath));
}

function firstTfm(node: ProjectNode): string | null {
    return node.model.targetFrameworks[0] ?? null;
}

function isGrossTfmMismatch(rootTfm: string, nodeTfm: string): boolean {
    // Coarse heuristic: `netN.0` / `netstandardN.M` are generally interoperable; explicit
    // .NET Framework monikers (`net4xx`) interoperating with modern .NET is the common
    // footgun we want to flag. Leave the fine-grained enforcement to Roslyn.
    const looksFramework = (t: string) => /^net4\d\d?$/.test(t.toLowerCase());
    return looksFramework(rootTfm) !== looksFramework(nodeTfm);
}

function findIncludeAttrForResolvedPath(node: ProjectNode, resolvedAbs: string): string | null {
    // Walk the model's resolved projectReferences against the raw include attributes —
    // msbuild-lite gives us only resolved paths, but we can reconstruct the original Include
    // by stripping the common prefix with the referring csproj's directory.
    const rel = path.relative(node.model.projectDir, resolvedAbs);
    return rel.split(path.sep).join("/");
}

function dedupePreserveOrder(items: readonly string[]): string[] {
    const seen = new Set<string>();
    const out: string[] = [];
    for (const item of items) {
        const key = canonicalKey(item);
        if (seen.has(key)) continue;
        seen.add(key);
        out.push(item);
    }
    return out;
}

/**
 * Classic DFS post-order topological sort with in-stack cycle detection. Start from the
 * root; visit every reachable node via `projectReferences` edges; emit each node when its
 * descendants have all been emitted. Returns the build order (leaves first, root last).
 */
function topologicalSort(rootKey: string, nodes: ReadonlyMap<string, ProjectNode>): ProjectNode[] {
    const order: ProjectNode[] = [];
    const visiting = new Set<string>();
    const visited = new Set<string>();
    const stack: string[] = []; // path used to build the cycle message on error.

    const visit = (key: string): void => {
        if (visited.has(key)) return;
        if (visiting.has(key)) {
            // Walk back up `stack` to find the slice that forms the cycle.
            const cycleStart = stack.indexOf(key);
            const cycleKeys = cycleStart >= 0 ? stack.slice(cycleStart) : [...stack];
            cycleKeys.push(key);
            throw new ProjectGraphCycleError(cycleKeys.map((k) => nodes.get(k)!.csprojPath));
        }

        visiting.add(key);
        stack.push(key);
        const node = nodes.get(key);
        if (!node) {
            throw new Error(`Internal graph error: node missing for key '${key}'.`);
        }
        for (const refAbs of node.projectReferences) {
            visit(canonicalKey(refAbs));
        }
        visiting.delete(key);
        stack.pop();
        visited.add(key);
        order.push(node);
    };

    visit(rootKey);

    // Sanity: every node must have been visited (unreachable nodes would indicate a walker
    // bug, since we only add to `nodes` via the walk rooted at rootKey).
    if (order.length !== nodes.size) {
        throw new Error(
            `Internal graph error: topological sort visited ${order.length} of ${nodes.size} nodes.`,
        );
    }

    return order;
}

/** Compute each node's transitive-closure set (all projects reachable via ProjectReferences). */
function populateTransitiveClosures(nodes: Map<string, ProjectNode>): void {
    for (const node of nodes.values()) {
        const closure = node.transitiveClosure;
        const walk = (n: ProjectNode): void => {
            for (const refAbs of n.projectReferences) {
                const key = canonicalKey(refAbs);
                if (closure.has(key)) continue;
                closure.add(key);
                const child = nodes.get(key);
                if (child) walk(child);
            }
        };
        walk(node);
    }
}

function isNodeFsNotFoundError(err: unknown): boolean {
    if (!err || typeof err !== "object") return false;
    const code = (err as { code?: unknown }).code;
    return code === "ENOENT" || code === "ENOTDIR";
}

// Re-export the parsed warning type as a local alias so the graph's consumer can unify
// warnings without importing @carbide/msbuild-lite directly.
export type { Warning };
