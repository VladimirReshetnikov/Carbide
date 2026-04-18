// M5+M6 shared pipeline: parse a .csproj, create a Carbide project from its model, and
// attach source documents + byte references (from --ref and — new in M6 — resolved NuGet
// references). Returns the configured Project so each command (build / run / validate)
// can take its command-specific final step.

import path from "node:path";
import { readFile, writeFile, access } from "node:fs/promises";
import { constants as fsConstants } from "node:fs";
import { parseCsproj, type ProjectModel } from "@carbide/msbuild-lite";
import type { CarbideSession, Project, ProjectOptions } from "@carbide/core";
import {
    resolve as resolveNuget,
    readLock,
    LockReadError,
    type ResolvedGraph,
    type ResolveLock,
} from "@carbide/nuget";
import type { NugetCliOptions } from "./nuget-options.js";

export interface PipelineWarning {
    code: string;
    message: string;
    severity: string;
    category: string;
}

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

/**
 * Parse a .csproj, derive Carbide ProjectOptions, create a project and register each
 * source file and external reference. The Project is ready to build/run/validate.
 *
 * Extra references from the CLI's `--ref` flag are honoured on top of the .csproj-derived
 * set. `<PackageReference>`s are resolved via @carbide/nuget when the project declares
 * any; `nugetOptions` governs offline / lock / allow-list behaviour.
 */
export async function runCsprojPipeline(
    session: CarbideSession,
    projectPath: string,
    extraRefs: readonly string[] = [],
    nugetOptions: NugetCliOptions | null = null,
): Promise<CsprojPipelineResult> {
    const model = await parseCsproj(projectPath);

    const options: ProjectOptions = buildOptionsFromModel(model);
    const project = session.createProject(options);

    // Attach CLI --ref DLLs first.
    for (const refPath of extraRefs) {
        const bytes = new Uint8Array(await readFile(refPath));
        const name = path.basename(refPath, path.extname(refPath));
        const handle = session.addReference(bytes, name);
        project.addReference(handle);
    }

    // Load each compile-item's bytes; register under the relative-to-project-dir path so
    // diagnostics stay short and cross-platform (M5 D51).
    for (const absPath of model.sourceFiles) {
        const code = await readFile(absPath, "utf8");
        const rel = path.relative(model.projectDir, absPath).split(path.sep).join("/");
        project.addSource(rel, code);
    }

    // Start the unified warnings list. We'll suppress MSBLITE013 if resolution actually runs.
    const warnings: PipelineWarning[] = [];
    const shouldRunResolver = model.packageReferences.length > 0 && nugetOptions !== null;
    for (const w of model.warnings) {
        if (shouldRunResolver && w.code === "MSBLITE013") continue;
        warnings.push({ code: w.code, message: w.message, severity: w.severity, category: w.category });
    }

    // --- NuGet resolution -------------------------------------------------------------
    let nugetGraph: ResolvedGraph | null = null;
    let nugetLockPath: string | null = null;
    let nugetLockWritten = false;

    if (shouldRunResolver) {
        const tfm = options.targetFramework ?? "net10.0";
        const lockPath = nugetOptions!.lockPath ?? path.join(model.projectDir, "carbide.lock.json");
        nugetLockPath = lockPath;

        // If a lock exists at that path, read it and replay — deterministic by default.
        let lock: ResolveLock | undefined;
        try {
            await access(lockPath, fsConstants.R_OK);
            lock = await readLock(lockPath);
        } catch (err) {
            if (err instanceof LockReadError) {
                // Malformed lock: surface as a warning, fall through to fresh resolve.
                warnings.push({
                    code: "MSNUGET031",
                    message: `Ignoring malformed lock file '${lockPath}': ${err.message}`,
                    severity: "warning",
                    category: "nuget",
                });
            }
            // ENOENT or access failure: lock not present — resolve fresh.
        }

        const packageRefs = model.packageReferences
            .filter((p) => p.version !== null && p.version.length > 0)
            .map((p) => ({ id: p.id, versionRange: p.version as string }));

        // A PackageReference without a Version attribute isn't meaningful in Carbide's
        // bounded subset (no central package management). Surface and skip.
        for (const p of model.packageReferences) {
            if (p.version === null || p.version.length === 0) {
                warnings.push({
                    code: "MSNUGET000",
                    message: `<PackageReference Include="${p.id}"/> has no Version attribute; skipping.`,
                    severity: "warning",
                    category: "nuget",
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

        // Feed every resolved reference DLL to the Carbide session.
        for (const ref of nugetGraph.references) {
            const handle = session.addReference(ref.bytes, ref.name);
            project.addReference(handle);
        }

        // Collect resolver warnings into the unified stream.
        for (const w of nugetGraph.warnings) {
            warnings.push({ code: w.code, message: w.message, severity: w.severity, category: "nuget" });
        }

        // Write the lock unless we replayed one or the user said no.
        if (!lock && !nugetOptions!.noLockWrite) {
            await writeFile(lockPath, JSON.stringify(nugetGraph.lock, null, 2) + "\n");
            nugetLockWritten = true;
        }
    }

    return { model, project, nugetGraph, nugetLockPath, nugetLockWritten, warnings };
}

function buildOptionsFromModel(model: ProjectModel): ProjectOptions {
    const props = model.properties;
    const options: ProjectOptions = {};

    if (props.assemblyName && props.assemblyName.length > 0) {
        options.assemblyName = props.assemblyName;
    } else {
        // Fall back to the project filename without extension.
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

    // Target framework: pick the first one in sorted-set ordering (matches first-listed
    // selection in cs_kit and M5 D50). If absent, leave options.targetFramework unset —
    // Carbide defaults to net10.0.
    if (model.evaluationTrace.targetFramework.selected === "net8.0") {
        options.targetFramework = "net8.0";
    } else if (model.evaluationTrace.targetFramework.selected === "net10.0") {
        options.targetFramework = "net10.0";
    }

    return options;
}
