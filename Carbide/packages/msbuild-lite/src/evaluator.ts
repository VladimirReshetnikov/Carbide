// M11 — bounded MSBuild evaluator.
//
// The pre-M11 parser did a single-file walk: parseCsproj read one XML root and populated
// ProjectModel. M11 extends this to a multi-file evaluation:
//
//   1. Auto-discover and walk Directory.Build.props (walking up from the csproj dir).
//   2. Walk the csproj itself, honouring `<Import Project="..."/>` elements inline.
//   3. Discover Directory.Build.targets and refuse to ingest it (MSBLITE027).
//
// Each walk mutates a shared `EvaluationContext`. Properties from files walked first are
// overridden by later files (document order). Cycles and missing imports are tracked in
// `context.imports[]` and surface as warnings; the evaluation proceeds with whatever it
// could walk successfully.
//
// This module does not load or parse the root csproj itself — that's `parseCsproj`'s job.
// It consumes already-parsed XML roots plus the resolved file paths they came from.

import { readFile, access } from "node:fs/promises";
import { constants as fsConstants } from "node:fs";
import path from "node:path";
import { evalCondition, substituteVars } from "./conditions.js";
import {
    isElement,
    parseXml,
    stripNamespace,
    XmlParseError,
    type XmlElement,
} from "./xml.js";
import { computeReservedProperties, isReservedProperty } from "./reserved-properties.js";
import type {
    CompileOperation,
    ConditionTraceEntry,
    ImportTraceEntry,
    PackageReference,
    ProjectProperties,
    Warning,
} from "./types.js";

export interface EvaluationContext {
    /** Canonical paths of files already fully walked — de-duplicates re-imports (D129). */
    visited: Set<string>;
    /** Stack of files currently being walked — detects import cycles. */
    importStack: string[];
    /** Case-insensitive property bag consumed by `evalCondition` / `substituteVars`. */
    conditionProps: Record<string, string>;
    /** Structured properties for the eventual ProjectModel output. */
    props: ProjectProperties;
    /** Target-framework list accumulated across all files. */
    tfms: string[];
    /** Output accumulators. */
    pkgRefs: PackageReference[];
    projRefs: string[];
    compileOperations: CompileOperation[];
    warnings: Warning[];
    conditionTrace: ConditionTraceEntry[];
    ignoredConditionTrace: Array<{ scope: string; condition: string | null }>;
    imports: ImportTraceEntry[];
    /** Absolute path of the root csproj — drives `$(MSBuildProject*)`. */
    rootProjectPath: string;
}

export function createEvaluationContext(
    rootProjectPath: string,
    initialConditionProps: Record<string, string>,
    initialProps: ProjectProperties,
): EvaluationContext {
    return {
        visited: new Set(),
        importStack: [],
        conditionProps: { ...initialConditionProps },
        props: initialProps,
        tfms: [],
        pkgRefs: [],
        projRefs: [],
        compileOperations: [],
        warnings: [],
        conditionTrace: [],
        ignoredConditionTrace: [],
        imports: [],
        rootProjectPath,
    };
}

/**
 * Walk one project-shaped XML document (csproj, props, targets file; they all share the
 * same outer `<Project>`-element shape in the subset Carbide supports). Mutates `ctx`
 * in-place; pushes/pops `ctx.importStack` around the walk so nested imports can detect
 * cycles.
 *
 * `kind` is a string used only in warning messages / traces so the user can distinguish
 * "the csproj said X" from "Directory.Build.props said X" at debug time.
 */
export async function evaluateProjectDocument(
    filePath: string,
    source: string,
    ctx: EvaluationContext,
    kind: "csproj" | "props" | "targets" | "import",
): Promise<void> {
    const absPath = path.resolve(filePath);
    const canonical = canonicalFilePath(absPath);

    if (ctx.importStack.includes(canonical)) {
        // Cycle — don't push, just record and bail.
        addWarning(
            ctx,
            "MSBLITE025",
            `<Import> cycle detected: ${[...ctx.importStack, canonical].join(" -> ")}`,
            "import",
            absPath,
        );
        ctx.imports.push({
            sourceFile: ctx.importStack[ctx.importStack.length - 1] ?? null,
            importedFile: absPath,
            kind,
            applied: false,
            error: "cycle",
        });
        return;
    }
    if (ctx.visited.has(canonical)) {
        // Already walked — de-dupe (D129). Record the repeat import but skip re-evaluation.
        ctx.imports.push({
            sourceFile: ctx.importStack[ctx.importStack.length - 1] ?? null,
            importedFile: absPath,
            kind,
            applied: false,
            error: "duplicate",
        });
        return;
    }

    let root: XmlElement;
    try {
        root = parseXml(source);
    } catch (err) {
        const message = err instanceof XmlParseError ? err.message : String(err);
        addWarning(ctx, "MSBLITE000", `XML parse error in ${path.basename(absPath)}: ${message}`, "parse", absPath, "error");
        ctx.imports.push({
            sourceFile: ctx.importStack[ctx.importStack.length - 1] ?? null,
            importedFile: absPath,
            kind,
            applied: false,
            error: "parse",
        });
        return;
    }

    // Reserved `$(MSBuildThisFile*)` properties are scoped to the current file — save +
    // restore around the walk so imports don't leak theirs into the outer file's bag.
    const savedReserved = saveReservedKeys(ctx.conditionProps);
    Object.assign(ctx.conditionProps, computeReservedProperties(absPath, ctx.rootProjectPath));

    ctx.importStack.push(canonical);
    ctx.imports.push({
        sourceFile: ctx.importStack.length >= 2 ? ctx.importStack[ctx.importStack.length - 2] : null,
        importedFile: absPath,
        kind,
        applied: true,
    });
    try {
        await walkProjectRoot(root, absPath, ctx);
        ctx.visited.add(canonical);
    } finally {
        ctx.importStack.pop();
        restoreReservedKeys(ctx.conditionProps, savedReserved);
    }
}

/**
 * Walk the children of a `<Project>` element in document order. Properties, items,
 * imports, and refusals are all handled here.
 */
async function walkProjectRoot(
    root: XmlElement,
    filePath: string,
    ctx: EvaluationContext,
): Promise<void> {
    for (const child of root.children) {
        if (!isElement(child)) continue;
        const tag = stripNamespace(child.name);
        switch (tag) {
            case "PropertyGroup":
                walkPropertyGroup(child, filePath, ctx);
                break;
            case "ItemGroup":
                walkItemGroup(child, filePath, ctx);
                break;
            case "Import":
                await walkImport(child, filePath, ctx);
                break;
            case "Target":
                addWarning(
                    ctx,
                    "MSBLITE020",
                    `<Target Name="${child.attributes.Name ?? "?"}"/> is not executed by Carbide (msbuild-lite refuses target execution).`,
                    "refusal",
                    filePath,
                );
                break;
            case "Task":
                addWarning(
                    ctx,
                    "MSBLITE021",
                    `<Task ${child.attributes.Name ?? ""}/> is not executed by Carbide.`,
                    "refusal",
                    filePath,
                );
                break;
            case "UsingTask":
                addWarning(
                    ctx,
                    "MSBLITE022",
                    `<UsingTask TaskName="${child.attributes.TaskName ?? "?"}"/> is ignored (Carbide doesn't run tasks).`,
                    "refusal",
                    filePath,
                );
                break;
            case "Choose":
                addWarning(
                    ctx,
                    "MSBLITE023",
                    "<Choose>/<When>/<Otherwise> is not evaluated by Carbide; use <PropertyGroup Condition=\"…\"> instead.",
                    "refusal",
                    filePath,
                );
                break;
            case "ItemDefinitionGroup":
                addWarning(
                    ctx,
                    "MSBLITE028",
                    "<ItemDefinitionGroup> is not evaluated by Carbide (item defaults are ignored).",
                    "refusal",
                    filePath,
                );
                break;
            default:
                // Unknown elements pass silently — MSBuild tolerates them, and real projects
                // commonly have tool-specific extensions (e.g. <ItemGroup Label=...>) that
                // aren't actually problematic.
                break;
        }
    }
}

function walkPropertyGroup(pg: XmlElement, filePath: string, ctx: EvaluationContext): void {
    if (!applyCondition(ctx, pg.attributes.Condition, "PropertyGroup", filePath)) return;

    for (const child of pg.children) {
        if (!isElement(child)) continue;
        const tag = stripNamespace(child.name);
        const itemCondition = child.attributes.Condition;
        if (itemCondition && !applyCondition(ctx, itemCondition, `Property:${tag}`, filePath)) {
            continue;
        }
        const val = child.text || null;
        if (!val) continue;

        if (isReservedProperty(tag)) {
            addWarning(
                ctx,
                "MSBLITE029",
                `Cannot set reserved MSBuild property '${tag}'; assignment ignored.`,
                "reserved-property",
                filePath,
            );
            continue;
        }

        const substituted = substituteVars(val, ctx.conditionProps);

        if (tag === "TargetFramework" || tag === "TargetFrameworks") {
            for (const t of substituted.split(";").map((s) => s.trim()).filter(Boolean)) {
                ctx.tfms.push(t);
            }
            ctx.conditionProps[tag.toLowerCase()] = substituted;
        } else if (tag === "Nullable" || tag === "LangVersion" || tag === "ImplicitUsings") {
            ctx.props[lowercaseFirst(tag)] = substituted;
            ctx.conditionProps[tag.toLowerCase()] = substituted;
        } else if (tag === "DefineConstants") {
            const consts = substituted.split(";").map((c) => c.trim()).filter(Boolean);
            ctx.props.defineConstants = consts;
            ctx.conditionProps[tag.toLowerCase()] = substituted;
        } else if (tag === "AssemblyName" || tag === "RootNamespace" || tag === "EnableDefaultCompileItems") {
            ctx.props[lowercaseFirst(tag)] = substituted;
            ctx.conditionProps[tag.toLowerCase()] = substituted;
        } else {
            ctx.conditionProps[tag.toLowerCase()] = substituted;
        }
    }
}

function walkItemGroup(ig: XmlElement, filePath: string, ctx: EvaluationContext): void {
    if (!applyCondition(ctx, ig.attributes.Condition, "ItemGroup", filePath)) return;

    const projectDir = path.dirname(filePath);
    // Review R2 §9 — property substitution (e.g. `Version="$(NewtonsoftVersion)"`) is
    // applied to property values and conditions but was NOT being applied to item
    // attributes before this point, so `<PackageReference Version="$(X)"/>` and
    // `<Compile Include="$(Dir)/*.cs"/>` would be stored raw, causing downstream
    // consumers to ship MSBuild-style literal placeholders. Thread the same helper
    // through here.
    const sub = (v: string | undefined | null): string | null =>
        v ? substituteVars(v, ctx.conditionProps) : null;

    for (const item of ig.children) {
        if (!isElement(item)) continue;
        const tag = stripNamespace(item.name);
        const itemCondition = item.attributes.Condition;
        if (itemCondition && !applyCondition(ctx, itemCondition, `Item:${tag}`, filePath)) continue;

        if (tag === "PackageReference") {
            const id = sub(item.attributes.Include) ?? sub(item.attributes.Update);
            if (!id) continue;
            const version = sub(item.attributes.Version) ?? sub(getChildText(item, "Version"));
            ctx.pkgRefs.push({ id, version });
            addWarning(
                ctx,
                "MSBLITE013",
                `<PackageReference Include="${id}"/> captured by msbuild-lite; resolution is the consumer's responsibility (wire @carbide/nuget to resolve).`,
                "package-reference",
                filePath,
            );
        } else if (tag === "ProjectReference") {
            const include = sub(item.attributes.Include);
            if (include) {
                const resolved = path.resolve(projectDir, normaliseSlashes(include));
                ctx.projRefs.push(resolved);
                addWarning(
                    ctx,
                    "MSBLITE014",
                    `<ProjectReference Include="${include}"/> captured by msbuild-lite; ` +
                        `sibling-project orchestration is the consumer's responsibility ` +
                        `(wire @carbide/cli's project-graph walker to build them).`,
                    "project-reference",
                    filePath,
                );
            }
        } else if (tag === "Compile") {
            const include = sub(item.attributes.Include);
            const remove = sub(item.attributes.Remove);
            const update = sub(item.attributes.Update);
            if (include) {
                for (const patt of include.split(";").map((p) => p.trim()).filter(Boolean)) {
                    ctx.compileOperations.push({ operation: "include", pattern: patt });
                }
            }
            if (remove) {
                for (const patt of remove.split(";").map((p) => p.trim()).filter(Boolean)) {
                    ctx.compileOperations.push({ operation: "remove", pattern: patt });
                }
            }
            if (update) {
                addWarning(
                    ctx,
                    "MSBLITE011",
                    "Compile Update metadata is not modeled in msbuild-lite.",
                    "compile-items",
                    filePath,
                );
            }
        }
    }
}

async function walkImport(imp: XmlElement, filePath: string, ctx: EvaluationContext): Promise<void> {
    if (!applyCondition(ctx, imp.attributes.Condition, "Import", filePath)) return;

    const rawProject = imp.attributes.Project;
    if (!rawProject) {
        addWarning(
            ctx,
            "MSBLITE024",
            "<Import/> has no Project attribute; skipping.",
            "import",
            filePath,
        );
        return;
    }

    const substituted = substituteVars(rawProject, ctx.conditionProps);
    if (!substituted.trim()) {
        // Variable-substituted to empty string — common when a conditional chain's target
        // path is built from optional properties. Skip silently unless the user explicitly
        // gated with a Condition (which we already evaluated above).
        return;
    }

    const resolved = path.resolve(path.dirname(filePath), normaliseSlashes(substituted));
    let contents: string;
    try {
        contents = await readFile(resolved, "utf8");
    } catch {
        addWarning(
            ctx,
            "MSBLITE024",
            `<Import Project="${substituted}"/> target not found (resolved to '${resolved}').`,
            "import",
            filePath,
        );
        ctx.imports.push({
            sourceFile: filePath,
            importedFile: resolved,
            kind: "import",
            applied: false,
            error: "missing",
        });
        return;
    }

    await evaluateProjectDocument(resolved, contents, ctx, "import");
}

/**
 * Walk up from `startDir` looking for a `Directory.Build.<kind>` file. Returns the
 * absolute path of the closest match or null if none exists between `startDir` and the
 * filesystem root.
 */
export async function findDirectoryBuild(
    kind: "props" | "targets",
    startDir: string,
): Promise<string | null> {
    let dir = path.resolve(startDir);
    while (true) {
        const candidate = path.join(dir, `Directory.Build.${kind}`);
        try {
            await access(candidate, fsConstants.R_OK);
            return candidate;
        } catch {
            // not here — walk up
        }
        const parent = path.dirname(dir);
        if (parent === dir) return null; // hit the filesystem root
        dir = parent;
    }
}

// --- Helpers -----------------------------------------------------------------------------

function applyCondition(
    ctx: EvaluationContext,
    condition: string | undefined,
    scope: string,
    filePath: string,
): boolean {
    const c = condition ?? null;
    const r = evalCondition(c, ctx.conditionProps);
    ctx.conditionTrace.push({ scope, condition: c, evaluated: r.evaluated, applies: r.applies });
    if (!r.evaluated && c) {
        addWarning(ctx, "MSBLITE001", `Condition not evaluated (ignored): ${c}`, "condition", filePath);
        ctx.ignoredConditionTrace.push({ scope, condition: c });
    }
    return r.applies;
}

function addWarning(
    ctx: EvaluationContext,
    code: string,
    message: string,
    category: string,
    sourceFile?: string,
    severity: Warning["severity"] = "warning",
): void {
    ctx.warnings.push({ code, message, category, severity, sourceFile });
}

function getChildText(element: XmlElement, name: string): string | null {
    for (const child of element.children) {
        if (!isElement(child)) continue;
        if (stripNamespace(child.name) === name) {
            return child.text.length > 0 ? child.text : null;
        }
    }
    return null;
}

function lowercaseFirst(name: string): string {
    return name.length === 0 ? name : name[0].toLowerCase() + name.slice(1);
}

function normaliseSlashes(s: string): string {
    return s.replace(/\\/g, "/");
}

function canonicalFilePath(absPath: string): string {
    return process.platform === "win32" ? absPath.toLowerCase() : absPath;
}

function saveReservedKeys(props: Record<string, string>): Record<string, string | undefined> {
    const saved: Record<string, string | undefined> = {};
    for (const key of [
        "msbuildthisfile",
        "msbuildthisfilename",
        "msbuildthisfiledirectory",
        "msbuildthisfilefullpath",
        "msbuildthisfileextension",
    ]) {
        saved[key] = props[key];
    }
    return saved;
}

function restoreReservedKeys(
    props: Record<string, string>,
    saved: Record<string, string | undefined>,
): void {
    for (const [key, value] of Object.entries(saved)) {
        if (value === undefined) delete props[key];
        else props[key] = value;
    }
}
