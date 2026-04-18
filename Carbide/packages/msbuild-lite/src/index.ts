// @carbide/msbuild-lite — public entry.
// parseCsproj(path) reads a .csproj, runs the PropertyGroup / ItemGroup walk, expands
// Compile globs against the filesystem, and returns a ProjectModel whose shape matches
// cs_kit.msbuild_lite's output (see carbide-M5-detailed-plan §2.2).

import { readFile } from "node:fs/promises";
import path from "node:path";
import { evalCondition } from "./conditions.js";
import { resolveCompileItems } from "./compile-items.js";
import {
    findChildren,
    isElement,
    parseXml,
    stripNamespace,
    XmlParseError,
    type XmlElement,
} from "./xml.js";
import type {
    CompileOperation,
    ConditionTraceEntry,
    PackageReference,
    ParseOptions,
    ProjectModel,
    ProjectProperties,
    Warning,
} from "./types.js";

export type {
    ProjectModel,
    ProjectProperties,
    PackageReference,
    Warning,
    ParseOptions,
    EvaluationTrace,
    ConditionTraceEntry,
    CompileOperation,
    CompileResolvedEntry,
} from "./types.js";

/** Parse a .csproj file from disk and return its ProjectModel. */
export async function parseCsproj(projectPath: string, options: ParseOptions = {}): Promise<ProjectModel> {
    const absPath = path.resolve(projectPath);
    const projectDir = path.dirname(absPath);
    const source = await readFile(absPath, "utf8");
    return parseCsprojString(source, absPath, options);
}

/** Parse .csproj XML text with an explicit project path. The directory is derived from the path. */
export async function parseCsprojString(
    source: string,
    projectPath: string,
    options: ParseOptions = {},
): Promise<ProjectModel> {
    const absPath = path.resolve(projectPath);
    const projectDir = path.dirname(absPath);
    const configuration = options.configuration ?? "Debug";
    const [parsedConfiguration, parsedPlatform] = extractConfigurationAndPlatform(configuration);

    const warnings: Warning[] = [];
    const addWarning = (
        code: string,
        message: string,
        category: string = "general",
        severity: Warning["severity"] = "warning",
    ): void => {
        warnings.push({ code, message, category, severity });
    };

    let root: XmlElement;
    try {
        root = parseXml(source);
    } catch (err) {
        const message = err instanceof XmlParseError ? err.message : String(err);
        addWarning("MSBLITE000", `XML parse error: ${message}`, "parse", "error");
        return {
            projectPath: absPath,
            projectDir,
            targetFrameworks: [],
            properties: { configuration: parsedConfiguration, platform: parsedPlatform },
            packageReferences: [],
            projectReferences: [],
            sourceFiles: [],
            warnings,
            evaluationTrace: {
                targetFramework: { selectionPolicy: "first-listed", candidates: [], selected: null },
                conditions: { evaluated: [], ignored: [] },
                compileItems: { defaultIncludeEnabled: true, operations: [], resolved: [] },
            },
        };
    }

    // Property bag for condition evaluation uses lowercased keys (matches cs_kit).
    const conditionProps: Record<string, string> = {
        configuration: parsedConfiguration,
        platform: parsedPlatform,
    };
    const props: ProjectProperties = {
        configuration: parsedConfiguration,
        platform: parsedPlatform,
    };

    const conditionTrace: ConditionTraceEntry[] = [];
    const ignoredConditionTrace: Array<{ scope: string; condition: string | null }> = [];

    const applyCondition = (condition: string | undefined, scope: string): boolean => {
        const c = condition ?? null;
        const r = evalCondition(c, conditionProps);
        conditionTrace.push({ scope, condition: c, evaluated: r.evaluated, applies: r.applies });
        if (!r.evaluated && c) {
            addWarning("MSBLITE001", `Condition not evaluated (ignored): ${c}`, "condition");
            ignoredConditionTrace.push({ scope, condition: c });
        }
        return r.applies;
    };

    // --- PropertyGroup walk ------------------------------------------------------------

    const tfms: string[] = [];

    for (const pg of findChildren(root, "PropertyGroup")) {
        if (!applyCondition(pg.attributes.Condition, "PropertyGroup")) continue;
        for (const child of pg.children) {
            if (!isElement(child)) continue;
            const tag = stripNamespace(child.name);
            const val = child.text || null;
            if (!val) continue;
            if (tag === "TargetFramework" || tag === "TargetFrameworks") {
                for (const t of val.split(";").map((s) => s.trim()).filter(Boolean)) {
                    tfms.push(t);
                }
                conditionProps[tag.toLowerCase()] = val;
            } else if (tag === "Nullable" || tag === "LangVersion" || tag === "ImplicitUsings") {
                props[lowercaseFirst(tag)] = val;
                conditionProps[tag.toLowerCase()] = val;
            } else if (tag === "DefineConstants") {
                const consts = val
                    .split(";")
                    .map((c) => c.trim())
                    .filter(Boolean);
                props.defineConstants = consts;
                conditionProps[tag.toLowerCase()] = val;
            } else {
                if (tag === "AssemblyName" || tag === "RootNamespace" || tag === "EnableDefaultCompileItems") {
                    props[lowercaseFirst(tag)] = val;
                }
                conditionProps[tag.toLowerCase()] = val;
            }
        }
    }

    // --- ItemGroup walk: PackageReference, ProjectReference, Compile -------------------

    const pkgRefs: PackageReference[] = [];
    const projRefs: string[] = [];
    const compileOperations: CompileOperation[] = [];

    for (const ig of findChildren(root, "ItemGroup")) {
        if (!applyCondition(ig.attributes.Condition, "ItemGroup")) continue;
        for (const item of ig.children) {
            if (!isElement(item)) continue;
            const tag = stripNamespace(item.name);
            const itemCondition = item.attributes.Condition;
            if (itemCondition && !applyCondition(itemCondition, `Item:${tag}`)) continue;

            if (tag === "PackageReference") {
                const id = item.attributes.Include ?? item.attributes.Update;
                if (!id) continue;
                const version = item.attributes.Version ?? getChildText(item, "Version") ?? null;
                pkgRefs.push({ id, version });
                addWarning(
                    "MSBLITE013",
                    `<PackageReference Include="${id}"/> is captured but not resolved; NuGet resolution lands in M6.`,
                    "package-reference",
                );
            } else if (tag === "ProjectReference") {
                const include = item.attributes.Include;
                if (include) {
                    const resolved = path.resolve(projectDir, normaliseSlashes(include));
                    projRefs.push(resolved);
                    addWarning(
                        "MSBLITE014",
                        `<ProjectReference Include="${include}"/> is captured but not built; sibling-project builds land in M9.`,
                        "project-reference",
                    );
                }
            } else if (tag === "Compile") {
                const include = item.attributes.Include;
                const remove = item.attributes.Remove;
                const update = item.attributes.Update;
                if (include) {
                    for (const patt of include.split(";").map((p) => p.trim()).filter(Boolean)) {
                        compileOperations.push({ operation: "include", pattern: patt });
                    }
                }
                if (remove) {
                    for (const patt of remove.split(";").map((p) => p.trim()).filter(Boolean)) {
                        compileOperations.push({ operation: "remove", pattern: patt });
                    }
                }
                if (update) {
                    addWarning(
                        "MSBLITE011",
                        "Compile Update metadata is not modeled in msbuild-lite.",
                        "compile-items",
                    );
                }
            }
        }
    }

    // --- Compile-item resolution -------------------------------------------------------

    const enableDefaultCompileItems = String(props.enableDefaultCompileItems ?? "true").trim().toLowerCase() !== "false";

    const explicitOps: Array<{ operation: "include" | "remove"; pattern: string }> = compileOperations
        .filter((o) => o.operation !== "default-include")
        .map((o) => ({ operation: o.operation as "include" | "remove", pattern: o.pattern }));

    const { sources, operationMatches, resolved } = await resolveCompileItems(
        projectDir,
        enableDefaultCompileItems,
        explicitOps,
    );

    // Merge match counts back into the compileOperations list (drops default-include rows
    // since those are implicit, matching cs_kit's output shape).
    for (let i = 0; i < compileOperations.length && i < operationMatches.length; i++) {
        compileOperations[i].matchCount = operationMatches[i].matchCount;
        if (operationMatches[i].matchCount === 0) {
            addWarning(
                "MSBLITE012",
                `Compile ${operationMatches[i].operation} pattern matched no source files: ${operationMatches[i].pattern}`,
                "compile-items",
            );
        }
    }

    // --- Post-processing ---------------------------------------------------------------

    const uniqueTfms = [...new Set(tfms)].sort();
    const packageReferences = deduplicatePackageRefs(pkgRefs);
    const projectReferences = [...new Set(projRefs)].sort();

    return {
        projectPath: absPath,
        projectDir,
        targetFrameworks: uniqueTfms,
        properties: props,
        packageReferences,
        projectReferences,
        sourceFiles: sources,
        warnings,
        evaluationTrace: {
            targetFramework: {
                selectionPolicy: "first-listed",
                candidates: uniqueTfms,
                selected: uniqueTfms[0] ?? null,
            },
            conditions: {
                evaluated: conditionTrace,
                ignored: ignoredConditionTrace,
            },
            compileItems: {
                defaultIncludeEnabled: enableDefaultCompileItems,
                operations: compileOperations,
                resolved,
            },
        },
    };
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

function extractConfigurationAndPlatform(configuration: string): [string, string] {
    const pipe = configuration.indexOf("|");
    if (pipe >= 0) {
        const c = configuration.slice(0, pipe).trim();
        const p = configuration.slice(pipe + 1).trim();
        return [c, p || "AnyCPU"];
    }
    return [configuration.trim(), "AnyCPU"];
}

function deduplicatePackageRefs(refs: PackageReference[]): PackageReference[] {
    const seen = new Map<string, string | null>();
    for (const r of refs) {
        const key = r.id;
        if (!seen.has(key)) seen.set(key, r.version);
    }
    return [...seen.entries()]
        .sort(([a], [b]) => a.localeCompare(b))
        .map(([id, version]) => ({ id, version }));
}

function normaliseSlashes(s: string): string {
    return s.replace(/\\/g, "/");
}
