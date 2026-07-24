// @carbide/msbuild-lite — public entry.
//
// M5: parseCsproj reads a single .csproj, runs PropertyGroup / ItemGroup walk, expands
//     Compile globs, and returns a ProjectModel whose shape matches cs_kit.msbuild_lite's
//     output.
//
// M11: evolved from a single-file walk into a bounded MSBuild evaluator. The walker lives
//     in `evaluator.ts`; this module orchestrates:
//       1. Directory.Build.props auto-discovery (walk up from the csproj directory).
//       2. Walking that props file into a shared EvaluationContext (if present).
//       3. Walking the csproj itself — `<Import>` inside it chains through the evaluator.
//       4. Discovering Directory.Build.targets (logs MSBLITE027 and does NOT ingest).
//       5. Resolving compile-item globs against the accumulated property bag.

import { readFile } from "node:fs/promises";
import path from "node:path";
import { resolveCompileItems } from "./compile-items.js";
import {
    createEvaluationContext,
    evaluateProjectDocument,
    findDirectoryBuild,
} from "./evaluator.js";
import { parseXml, isElement, XmlParseError } from "./xml.js";
import type {
    ParseOptions,
    ProjectModel,
    ProjectProperties,
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
    ImportTraceEntry,
} from "./types.js";

/** Parse a .csproj file from disk and return its ProjectModel. */
export async function parseCsproj(projectPath: string, options: ParseOptions = {}): Promise<ProjectModel> {
    const absPath = path.resolve(projectPath);
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

    // Seed the condition-property bag with the MSBuild basics — same keys M5 used.
    const conditionProps: Record<string, string> = {
        configuration: parsedConfiguration,
        platform: parsedPlatform,
    };
    const props: ProjectProperties = {
        configuration: parsedConfiguration,
        platform: parsedPlatform,
    };

    const ctx = createEvaluationContext(absPath, conditionProps, props);

    // 1. Directory.Build.props — walked first so its properties are overridden by
    //    downstream imports and the csproj itself.
    const dirPropsPath = await findDirectoryBuild("props", projectDir);
    if (dirPropsPath !== null) {
        try {
            const dirPropsSource = await readFile(dirPropsPath, "utf8");
            await evaluateProjectDocument(dirPropsPath, dirPropsSource, ctx, "props");
        } catch (err) {
            ctx.warnings.push({
                code: "MSBLITE024",
                message: `Could not read Directory.Build.props at '${dirPropsPath}': ${(err as Error).message}`,
                category: "import",
                severity: "warning",
                sourceFile: absPath,
            });
        }
    }

    // 2. The root csproj. `<Import>` elements inside it recurse via the evaluator.
    await evaluateProjectDocument(absPath, source, ctx, "csproj");

    // 3. Directory.Build.targets — refusal-warn if present AND non-empty; do NOT ingest.
    //    A purely-empty `<Project/>` marker file is silent (D131 allows it as a "stop the
    //    ancestor walk here" signal without noise).
    const dirTargetsPath = await findDirectoryBuild("targets", projectDir);
    if (dirTargetsPath !== null) {
        const targetsHasContent = await targetsFileHasContent(dirTargetsPath);
        if (targetsHasContent) {
            ctx.warnings.push({
                code: "MSBLITE027",
                message:
                    `Found Directory.Build.targets at '${dirTargetsPath}' but Carbide does not execute targets; contents ignored.`,
                category: "refusal",
                severity: "warning",
                sourceFile: absPath,
            });
        }
        ctx.imports.push({
            sourceFile: absPath,
            importedFile: dirTargetsPath,
            kind: "targets",
            applied: false,
            error: "refused",
        });
    }

    // 4. Compile-item resolution — same as pre-M11, driven off the accumulated property
    //    bag and compile-operation list.
    const enableDefaultCompileItems = String(ctx.props.enableDefaultCompileItems ?? "true").trim().toLowerCase() !== "false";
    const explicitOps: Array<{ operation: "include" | "remove"; pattern: string }> = ctx.compileOperations
        .filter((o) => o.operation !== "default-include")
        .map((o) => ({ operation: o.operation as "include" | "remove", pattern: o.pattern }));

    const { sources, operationMatches, resolved } = await resolveCompileItems(
        projectDir,
        enableDefaultCompileItems,
        explicitOps,
    );

    for (let i = 0; i < ctx.compileOperations.length && i < operationMatches.length; i++) {
        ctx.compileOperations[i].matchCount = operationMatches[i].matchCount;
        if (operationMatches[i].matchCount === 0) {
            ctx.warnings.push({
                code: "MSBLITE012",
                message: `Compile ${operationMatches[i].operation} pattern matched no source files: ${operationMatches[i].pattern}`,
                category: "compile-items",
                severity: "warning",
                sourceFile: absPath,
            });
        }
    }

    // 5. Post-processing: dedupe TFMs + package references. TFM order is load-bearing —
    // the evaluation trace advertises `selectionPolicy: "first-listed"`, and the downstream
    // CLI / ref-pack / NuGet resolution pipelines consume `uniqueTfms[0]` as the effective
    // framework. `new Set` preserves first-insertion order in ES2015+, so wrapping in
    // `[...new Set(ctx.tfms)]` gives us dedup + original order. Do NOT `.sort()` — that
    // silently swaps `<TargetFrameworks>net8.0;net10.0</TargetFrameworks>` to `net10.0` first.
    const uniqueTfms = [...new Set(ctx.tfms)];
    const packageReferences = deduplicatePackageRefs(ctx.pkgRefs);
    const projectReferences = [...new Set(ctx.projRefs)].sort();

    return {
        projectPath: absPath,
        projectDir,
        targetFrameworks: uniqueTfms,
        properties: ctx.props,
        packageReferences,
        projectReferences,
        sourceFiles: sources,
        warnings: ctx.warnings,
        evaluationTrace: {
            targetFramework: {
                selectionPolicy: "first-listed",
                candidates: uniqueTfms,
                selected: uniqueTfms[0] ?? null,
            },
            conditions: {
                evaluated: ctx.conditionTrace,
                ignored: ctx.ignoredConditionTrace,
            },
            compileItems: {
                defaultIncludeEnabled: enableDefaultCompileItems,
                operations: ctx.compileOperations,
                resolved,
            },
            imports: ctx.imports,
        },
    };
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

async function targetsFileHasContent(targetsPath: string): Promise<boolean> {
    try {
        const source = await readFile(targetsPath, "utf8");
        const root = parseXml(source);
        // `<Project/>` or `<Project></Project>` with no element children counts as empty.
        return root.children.some((c) => isElement(c));
    } catch (err) {
        if (err instanceof XmlParseError) return true; // a broken targets file has "content" worth warning about
        return false; // unreadable: silent (we already recorded the import trace)
    }
}

function deduplicatePackageRefs(
    refs: import("./types.js").PackageReference[],
): import("./types.js").PackageReference[] {
    const seen = new Map<string, string | null>();
    for (const r of refs) {
        const key = r.id;
        if (!seen.has(key)) seen.set(key, r.version);
    }
    return [...seen.entries()]
        .sort(([a], [b]) => a.localeCompare(b))
        .map(([id, version]) => ({ id, version }));
}

// Re-export the evaluator types for TS consumers that want direct access.
export { createEvaluationContext, evaluateProjectDocument, findDirectoryBuild } from "./evaluator.js";
export type { EvaluationContext } from "./evaluator.js";
export { computeReservedProperties, isReservedProperty } from "./reserved-properties.js";
