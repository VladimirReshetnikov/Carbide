// M5 shared pipeline: parse a .csproj, create a Carbide project from its model, and attach
// source documents + byte references. Returns the configured Project so each command
// (build / run / validate) can take its command-specific final step.

import path from "node:path";
import { readFile } from "node:fs/promises";
import { parseCsproj, type ProjectModel } from "@carbide/msbuild-lite";
import type { CarbideSession, Project, ProjectOptions } from "@carbide/core";

export interface CsprojPipelineResult {
    model: ProjectModel;
    project: Project;
}

/**
 * Parse a .csproj, derive Carbide ProjectOptions, create a project and register each
 * source file and external reference. The Project is ready to build/run/validate.
 *
 * Extra references from the CLI's `--ref` flag are honoured on top of the .csproj-derived
 * set. Package/Project references in the .csproj are captured as warnings (M6/M9 territory).
 */
export async function runCsprojPipeline(
    session: CarbideSession,
    projectPath: string,
    extraRefs: readonly string[] = [],
): Promise<CsprojPipelineResult> {
    const model = await parseCsproj(projectPath);

    const options: ProjectOptions = buildOptionsFromModel(model);
    const project = session.createProject(options);

    // Attach CLI --ref DLLs first; .csproj <PackageReference> is informational only in M5.
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

    return { model, project };
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
