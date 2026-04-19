// Shared .csproj pipeline.
//
// M5  gave us the single-project version: parse a `.csproj`, create a Carbide `Project`,
//     attach sources + references, and return the configured project for the command to
//     drive (build / run / validate).
//
// M6  bolted on NuGet resolution (per-project `carbide.lock.json`) and reference
//     registration from resolved package DLLs.
//
// M9  extends the pipeline to multi-project graphs: starting at one top-level csproj, walk
//     `<ProjectReference>` edges, build each reachable sub-project in topological order
//     (leaves first, root last), and attach each producer's PE bytes as session references
//     for every downstream consumer. One `CarbideSession` covers the whole graph [D86].
//
// The single-project result shape (`CsprojPipelineResult`) remains available for callers
// that still want "parse + configure, I'll build myself" semantics; under the hood it's now
// just the root slot of a size-1 multi-project result.

import path from "node:path";
import { readFile, writeFile, access } from "node:fs/promises";
import { constants as fsConstants } from "node:fs";
import { parseCsproj, type ProjectModel } from "@carbide/msbuild-lite";
import type { CarbideSession, Project, ProjectOptions, BuildResult, Diagnostic } from "@carbide/core";
import {
    resolve as resolveNuget,
    readLock,
    LockReadError,
    type ResolvedGraph,
    type ResolveLock,
} from "@carbide/nuget";
import type { NugetCliOptions } from "./nuget-options.js";
import {
    buildProjectGraph,
    canonicalKey,
    ProjectGraphCycleError,
    ProjectGraphNameCollisionError,
    ProjectReferenceNotFoundError,
    type ProjectGraph,
    type ProjectNode,
} from "./project-graph.js";
import { handleCliFailure } from "./errors.js";

/** Re-exported so CLI commands can canonicalise csproj paths without touching `project-graph`. */
export const canonicalKeyOf = canonicalKey;

/**
 * Map a graph-construction error into a `(exitCode, message)` pair for the CLI.
 *
 * U1.3 absorbed the body of this function into {@link handleCliFailure}; kept as a
 * public export for TS callers that pinned it. New code should call `handleCliFailure`
 * directly.
 */
export function handleProjectGraphError(
    err: unknown,
    format: "json" | "human",
): number {
    if (
        err instanceof ProjectGraphCycleError ||
        err instanceof ProjectGraphNameCollisionError ||
        err instanceof ProjectReferenceNotFoundError
    ) {
        return handleCliFailure(err, format);
    }
    throw err;
}

export interface PipelineWarning {
    code: string;
    message: string;
    severity: string;
    category: string;
    /** Canonical csproj path the warning attributes to. Null for CLI-level / non-project warnings. */
    project?: string | null;
}

/** Result shape preserved from M6 for single-project callers. */
export interface CsprojPipelineResult {
    model: ProjectModel;
    project: Project;
    /** Null when the project declares no `<PackageReference>`s. */
    nugetGraph: ResolvedGraph | null;
    /** Lock file path that was read from and/or written to. Null when resolution didn't run. */
    nugetLockPath: string | null;
    /** True when a fresh lock was written (false for replay / --no-lock-write / no packages). */
    nugetLockWritten: boolean;
    /** Unified warnings across csproj parsing and NuGet resolution, in emission order. */
    warnings: PipelineWarning[];
}

/** Multi-project shape (M9). Each element in `subprojects` is one graph node. */
export interface MultiProjectPipelineResult {
    /** Build order (leaves first, root last). */
    subprojects: SubprojectPipelineResult[];
    /** Convenience alias of `subprojects[subprojects.length - 1]`. */
    root: SubprojectPipelineResult;
    /** Unified warnings across graph construction, csproj parsing, and NuGet resolution. */
    warnings: PipelineWarning[];
    /** The full graph (for CLI output layout + debugging). */
    graph: ProjectGraph;
}

export interface SubprojectPipelineResult {
    csprojPath: string;
    model: ProjectModel;
    project: Project;
    assemblyName: string;
    nugetGraph: ResolvedGraph | null;
    nugetLockPath: string | null;
    nugetLockWritten: boolean;
    /** Set of source paths registered with {@link Project.addSource} (for diagnostic attribution). */
    sourcePaths: Set<string>;
    /** True when this node is the graph's root (the `--project` argument). */
    isRoot: boolean;
}

/**
 * Parse a .csproj, derive Carbide ProjectOptions, create a project and register each
 * source file and external reference. The Project is ready to build/run/validate.
 *
 * Single-project variant preserved for callers that predate M9. Internally this now delegates
 * to {@link runProjectGraphPipeline} and returns the root slot.
 */
export async function runCsprojPipeline(
    session: CarbideSession,
    projectPath: string,
    extraRefs: readonly string[] = [],
    nugetOptions: NugetCliOptions | null = null,
): Promise<CsprojPipelineResult> {
    const multi = await runProjectGraphPipeline(session, projectPath, extraRefs, nugetOptions);
    const root = multi.root;
    return {
        model: root.model,
        project: root.project,
        nugetGraph: root.nugetGraph,
        nugetLockPath: root.nugetLockPath,
        nugetLockWritten: root.nugetLockWritten,
        warnings: multi.warnings,
    };
}

/**
 * Walks `<ProjectReference>` edges from the root csproj and prepares every reachable project
 * for compilation. One {@link CarbideSession} is shared across the graph — PE bytes from each
 * leaf project become metadata references for downstream sub-projects.
 *
 * The graph is built and every Carbide `Project` is created + populated with sources and
 * references, but no build is triggered. Callers drive the per-project build / run / validate
 * step themselves; see {@link compileGraphInOrder} for the default sequencer.
 */
export interface RunProjectGraphPipelineOptions {
    /**
     * U3 — when true, skip writing `carbide.lock.json` for every sub-project. Used by
     * `carbide audit` to stay read-only; the fresh-resolve path still runs (needed to
     * populate the audit report), just without persisting the lock.
     */
    skipLockWrite?: boolean;
    /**
     * U3.4 `--scratch` — additional source paths to add to the *root* project's source
     * set after the csproj-derived sources. Sub-projects are not affected. Paths are
     * read via `readSource` (stdin `-` allowed, same as `--source`).
     */
    extraRootSources?: readonly string[];
}

export async function runProjectGraphPipeline(
    session: CarbideSession,
    rootProjectPath: string,
    extraRefs: readonly string[] = [],
    nugetOptions: NugetCliOptions | null = null,
    options: RunProjectGraphPipelineOptions = {},
): Promise<MultiProjectPipelineResult> {
    const graph = await buildProjectGraph(rootProjectPath);
    const warnings: PipelineWarning[] = [];

    // Graph-level warnings (MSPROJ*, csproj-parse warnings forwarded by the walker). The
    // walker has already dropped MSBLITE013 and MSBLITE014 — the first is re-emitted per
    // sub-project by `configureSubproject` when the resolver doesn't run, the second is
    // consumed by the walk itself.
    for (const w of graph.warnings) {
        warnings.push({
            code: w.code,
            message: w.message,
            severity: w.severity,
            category: w.category,
            project: w.project ?? null,
        });
    }

    const subprojects: SubprojectPipelineResult[] = [];

    // Pre-load --ref CLI DLLs once into the session — the root is the only project that
    // sees them (keeps the CLI flag semantics unsurprising: sub-libraries do not inherit
    // the root's --ref flags).
    const extraRefHandles = await loadExtraRefHandles(session, extraRefs);

    for (const node of graph.order) {
        const sub = await configureSubproject(session, node, nugetOptions, warnings, {
            skipLockWrite: options.skipLockWrite ?? false,
        });
        if (node.isRoot) {
            for (const handle of extraRefHandles) {
                sub.project.addReference(handle);
            }
            if (options.extraRootSources && options.extraRootSources.length > 0) {
                await addExtraRootSources(sub, options.extraRootSources);
            }
        }
        subprojects.push(sub);
    }

    const root = subprojects[subprojects.length - 1];
    if (!root || !root.isRoot) {
        throw new Error(
            "runProjectGraphPipeline: root sub-project is missing from build order; this is a graph-walker bug.",
        );
    }

    return { subprojects, root, warnings, graph };
}

/**
 * Build every sub-project in topological order. After each successful build, the emitted PE
 * becomes a session reference so downstream consumers (projects whose transitive closure
 * contains the producer) can bind against it during their own compilation.
 *
 * On compile failure: the failing project's diagnostics are returned and downstream
 * projects are skipped — they'd fail anyway without the producer's PE. Returns a list of
 * per-project `BuildResult`s in the same order as `multi.subprojects` (leaves first, root
 * last), with `null` placeholders for projects that were skipped because a producer failed.
 */
export async function compileGraphInOrder(
    session: CarbideSession,
    multi: MultiProjectPipelineResult,
    options: CompileGraphOptions = {},
): Promise<SubprojectBuildOutcome[]> {
    const skipRoot = options.skipRoot ?? false;
    const outcomes: SubprojectBuildOutcome[] = [];
    const failedKeys = new Set<string>();

    // `subprojects[i]` and `graph.order[i]` describe the same node — the pipeline builds
    // `subprojects` in lock-step with `graph.order`.
    for (let i = 0; i < multi.subprojects.length; i++) {
        const sub = multi.subprojects[i];
        const node = multi.graph.order[i];

        if (skipRoot && sub.isRoot) {
            outcomes.push({ subproject: sub, buildResult: null, skipped: true });
            continue;
        }

        // Skip this sub-project when any of its transitive ancestors failed to compile —
        // we'd only surface a cascade of "missing assembly" errors otherwise.
        let upstreamFailed = false;
        for (const ancestorKey of node.transitiveClosure) {
            if (failedKeys.has(ancestorKey)) {
                upstreamFailed = true;
                break;
            }
        }
        if (upstreamFailed) {
            outcomes.push({ subproject: sub, buildResult: null, skipped: true });
            failedKeys.add(canonicalKey(sub.csprojPath));
            continue;
        }

        const result = await sub.project.build();
        outcomes.push({ subproject: sub, buildResult: result, skipped: false });

        if (!result.success) {
            failedKeys.add(canonicalKey(sub.csprojPath));
            continue;
        }

        if (!result.pe) {
            throw new Error(
                `BuildResult.success=true but pe bytes are missing for '${sub.csprojPath}'.`,
            );
        }

        // Register this producer's PE on the shared session, then attach it to every
        // downstream consumer whose transitive closure includes this node. Matches the
        // dotnet-build "private dependencies stay private" rule (M9.5).
        const handle = session.addReference(result.pe, sub.assemblyName);
        const producerKey = canonicalKey(sub.csprojPath);
        for (let j = i + 1; j < multi.subprojects.length; j++) {
            const downstream = multi.subprojects[j];
            const downstreamNode = multi.graph.order[j];
            if (downstreamNode.transitiveClosure.has(producerKey)) {
                downstream.project.addReference(handle);
            }
        }
    }

    return outcomes;
}

export interface CompileGraphOptions {
    /**
     * When true, skip compiling the root project. Useful for `carbide run`, which compiles
     * and runs the root via `project.run()` after the producers have emitted their PEs.
     */
    skipRoot?: boolean;
}

export interface SubprojectBuildOutcome {
    subproject: SubprojectPipelineResult;
    /** Null when the build was skipped because a producer failed. */
    buildResult: BuildResult | null;
    /** True when the build was skipped (upstream failure). */
    skipped: boolean;
}

/**
 * Aggregate diagnostics across every sub-project in the graph and attribute each one to
 * the csproj whose source owns it. Returns a flat diagnostics array where every diagnostic
 * carries `project: <csproj path>` (or `project: null` when the source belongs to the root —
 * matches the single-project shape and lets existing callers stay agnostic of M9).
 */
export function attributeDiagnostics(
    outcomes: readonly SubprojectBuildOutcome[],
): AttributedDiagnostic[] {
    const attributed: AttributedDiagnostic[] = [];
    for (const outcome of outcomes) {
        if (!outcome.buildResult) continue;
        const sub = outcome.subproject;
        for (const d of outcome.buildResult.diagnostics) {
            attributed.push({
                ...d,
                project: sub.isRoot ? null : sub.csprojPath,
                subprojectAssemblyName: sub.assemblyName,
            });
        }
    }
    return attributed;
}

/**
 * Aggregate diagnostics from arbitrary per-project sources (e.g. validate's `getDiagnostics`
 * result). Caller supplies the diagnostics pre-grouped by sub-project via the
 * `diagnosticsBySubproject` map (keyed by canonical csproj path).
 */
export function attributeDiagnosticsBySubproject(
    multi: MultiProjectPipelineResult,
    diagnosticsBySubproject: ReadonlyMap<string, readonly Diagnostic[]>,
): AttributedDiagnostic[] {
    const attributed: AttributedDiagnostic[] = [];
    for (const sub of multi.subprojects) {
        const key = canonicalKey(sub.csprojPath);
        const diagnostics = diagnosticsBySubproject.get(key) ?? [];
        for (const d of diagnostics) {
            attributed.push({
                ...d,
                project: sub.isRoot ? null : sub.csprojPath,
                subprojectAssemblyName: sub.assemblyName,
            });
        }
    }
    return attributed;
}

/** A diagnostic plus the csproj it came from. `project` is null for the root sub-project. */
export type AttributedDiagnostic = Diagnostic & {
    project: string | null;
    subprojectAssemblyName: string;
};

/**
 * U3.3 — when a CS8802 diagnostic is present for the root in `--project` mode, append a
 * `CARBIDE_HINT_CS8802` info-severity diagnostic pointing at the supported escape hatches.
 * The hint helps users in "scratch directory with multiple top-level-statement files"
 * situations find the `<EnableDefaultCompileItems>false</EnableDefaultCompileItems>`,
 * explicit `<Compile Include>`, and `--scratch` workarounds.
 */
export function attachCs8802Hint(
    diagnostics: readonly AttributedDiagnostic[],
    rootCsproj: string,
): AttributedDiagnostic[] {
    const hasCs8802 = diagnostics.some((d) => d.id === "CS8802");
    if (!hasCs8802) return [...diagnostics];
    const hint: AttributedDiagnostic = {
        id: "CARBIDE_HINT_CS8802",
        severity: "info",
        message:
            "Multiple files contain top-level statements. Options: " +
            "(1) add <EnableDefaultCompileItems>false</EnableDefaultCompileItems> and list Compile items explicitly; " +
            "(2) pass --scratch to add --source files on top of the csproj-derived set; " +
            "(3) move the extra top-level statements into a regular class/method.",
        spanStart: 0,
        spanEnd: 0,
        project: rootCsproj,
        subprojectAssemblyName: "",
    };
    return [...diagnostics, hint];
}

// --- Helpers ---------------------------------------------------------------------------

async function addExtraRootSources(
    sub: SubprojectPipelineResult,
    sourceSpecs: readonly string[],
): Promise<void> {
    // U3.4 `--scratch` — appends CLI `--source` files to the root's csproj-derived set.
    // Reuses `readSource` for the stdin-`-` dance; paths are registered under the source
    // basename so diagnostics stay readable.
    const { readSource } = await import("./io.js");
    for (const spec of sourceSpecs) {
        const { path: docPath, code } = await readSource(spec);
        sub.project.addSource(docPath, code);
        sub.sourcePaths.add(docPath);
    }
}

async function loadExtraRefHandles(
    session: CarbideSession,
    extraRefs: readonly string[],
): Promise<Array<ReturnType<CarbideSession["addReference"]>>> {
    const handles: Array<ReturnType<CarbideSession["addReference"]>> = [];
    for (const refPath of extraRefs) {
        const bytes = new Uint8Array(await readFile(refPath));
        const name = path.basename(refPath, path.extname(refPath));
        handles.push(session.addReference(bytes, name));
    }
    return handles;
}

async function configureSubproject(
    session: CarbideSession,
    node: ProjectNode,
    nugetOptions: NugetCliOptions | null,
    warnings: PipelineWarning[],
    configOptions: { skipLockWrite: boolean } = { skipLockWrite: false },
): Promise<SubprojectPipelineResult> {
    const model = node.model;
    const options = buildOptionsFromModel(model);
    const project = session.createProject(options);

    // Load compile items and register them under a path relative to *this* sub-project's
    // directory so diagnostics stay short and cross-platform (M5 D51 applied per project).
    const sourcePaths = new Set<string>();
    for (const absPath of model.sourceFiles) {
        const code = await readFile(absPath, "utf8");
        const rel = path.relative(model.projectDir, absPath).split(path.sep).join("/");
        project.addSource(rel, code);
        sourcePaths.add(rel);
    }

    // MSBLITE013 is emitted only when NuGet resolution doesn't run for this sub-project
    // — when it does run, the captured references are consumed, not left dangling. The
    // graph walker already drops MSBLITE013 from its emission stream so we re-emit here
    // per-sub-project as needed (matches the M6 single-project flow).
    const shouldRunResolver = model.packageReferences.length > 0 && nugetOptions !== null;
    if (!shouldRunResolver) {
        for (const pkg of model.packageReferences) {
            warnings.push({
                code: "MSBLITE013",
                message:
                    `<PackageReference Include="${pkg.id}"/> captured by msbuild-lite; ` +
                    `resolution is the consumer's responsibility (wire @carbide/nuget to resolve).`,
                severity: "warning",
                category: "package-reference",
                project: node.csprojPath,
            });
        }
    }

    // --- NuGet resolution (per sub-project) --------------------------------------------
    let nugetGraph: ResolvedGraph | null = null;
    let nugetLockPath: string | null = null;
    let nugetLockWritten = false;

    if (shouldRunResolver) {
        const tfm = options.targetFramework ?? "net10.0";
        // D81: each sub-project gets its own lock next to its own csproj. The CLI's --lock
        // flag, when passed, overrides only the root's lock.
        const lockPath = node.isRoot && nugetOptions!.lockPath
            ? nugetOptions!.lockPath
            : path.join(model.projectDir, "carbide.lock.json");
        nugetLockPath = lockPath;

        let lock: ResolveLock | undefined;
        try {
            await access(lockPath, fsConstants.R_OK);
            lock = await readLock(lockPath);
        } catch (err) {
            if (err instanceof LockReadError) {
                warnings.push({
                    code: "MSNUGET031",
                    message: `Ignoring malformed lock file '${lockPath}': ${err.message}`,
                    severity: "warning",
                    category: "nuget",
                    project: node.csprojPath,
                });
            }
            // ENOENT: resolve fresh.
        }

        const packageRefs = model.packageReferences
            .filter((p) => p.version !== null && p.version.length > 0)
            .map((p) => ({ id: p.id, versionRange: p.version as string }));

        for (const p of model.packageReferences) {
            if (p.version === null || p.version.length === 0) {
                warnings.push({
                    code: "MSNUGET000",
                    message: `<PackageReference Include="${p.id}"/> has no Version attribute; skipping.`,
                    severity: "warning",
                    category: "nuget",
                    project: node.csprojPath,
                });
            }
        }

        nugetGraph = await resolveNuget(packageRefs, {
            targetFramework: tfm,
            allowListMode: nugetOptions!.allowListMode,
            offline: nugetOptions!.offline,
            sourceUrl: nugetOptions!.nugetSource,
            lock,
        });

        for (const ref of nugetGraph.references) {
            const handle = session.addReference(ref.bytes, ref.name);
            project.addReference(handle);
        }

        for (const w of nugetGraph.warnings) {
            warnings.push({
                code: w.code,
                message: w.message,
                severity: w.severity,
                category: "nuget",
                project: node.csprojPath,
            });
        }

        if (!lock && !nugetOptions!.noLockWrite && !configOptions.skipLockWrite) {
            await writeFile(lockPath, JSON.stringify(nugetGraph.lock, null, 2) + "\n");
            nugetLockWritten = true;
        }
    }

    return {
        csprojPath: node.csprojPath,
        model,
        project,
        assemblyName: node.assemblyName,
        nugetGraph,
        nugetLockPath,
        nugetLockWritten,
        sourcePaths,
        isRoot: node.isRoot,
    };
}

function buildOptionsFromModel(model: ProjectModel): ProjectOptions {
    const props = model.properties;
    const options: ProjectOptions = {};

    if (props.assemblyName && props.assemblyName.length > 0) {
        options.assemblyName = props.assemblyName;
    } else {
        options.assemblyName = path.basename(model.projectPath, path.extname(model.projectPath));
    }
    if (props.rootNamespace) options.rootNamespace = props.rootNamespace;
    if (props.langVersion) options.languageVersion = props.langVersion;
    if (props.implicitUsings !== undefined) {
        const v = String(props.implicitUsings).toLowerCase();
        options.implicitUsings = v === "enable" || v === "true";
    }
    if (props.nullable !== undefined) {
        const v = String(props.nullable).toLowerCase();
        options.nullable = v === "enable" || v === "true";
    }
    if (Array.isArray(props.defineConstants) && props.defineConstants.length > 0) {
        options.defineConstants = [...props.defineConstants];
    }

    if (model.evaluationTrace.targetFramework.selected === "net8.0") {
        options.targetFramework = "net8.0";
    } else if (model.evaluationTrace.targetFramework.selected === "net10.0") {
        options.targetFramework = "net10.0";
    }

    return options;
}
