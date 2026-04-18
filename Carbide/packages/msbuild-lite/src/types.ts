// Shared types for @carbide/msbuild-lite. Mirrors the shape that cs_kit.msbuild_lite emits
// so TS and Python consumers see the same JSON.

export type WarningSeverity = "warning" | "error";

export interface Warning {
    code: string;
    message: string;
    category: string;
    severity: WarningSeverity;
}

export interface PackageReference {
    id: string;
    version: string | null;
}

export interface ConditionTraceEntry {
    scope: string;
    condition: string | null;
    evaluated: boolean;
    applies: boolean;
}

export interface CompileOperation {
    operation: "default-include" | "include" | "remove";
    pattern: string;
    matchCount?: number;
    applied?: boolean;
}

export interface CompileResolvedProvenance {
    operation: string;
    pattern: string;
    applied: boolean;
}

export interface CompileResolvedEntry {
    file: string;
    included: boolean;
    provenance: CompileResolvedProvenance[];
}

export interface EvaluationTrace {
    targetFramework: {
        selectionPolicy: "first-listed";
        candidates: string[];
        selected: string | null;
    };
    conditions: {
        evaluated: ConditionTraceEntry[];
        ignored: Array<{ scope: string; condition: string | null }>;
    };
    compileItems: {
        defaultIncludeEnabled: boolean;
        operations: CompileOperation[];
        resolved: CompileResolvedEntry[];
    };
}

/**
 * Parsed project model. Shape deliberately matches cs_kit.msbuild_lite's `MsbuildLiteModel`
 * so the Python and TypeScript parsers share one semantic contract. Added or removed fields
 * require an entry in the parity fixture list.
 */
export interface ProjectModel {
    projectPath: string;
    projectDir: string;
    targetFrameworks: string[];
    properties: ProjectProperties;
    packageReferences: PackageReference[];
    projectReferences: string[];
    sourceFiles: string[];
    warnings: Warning[];
    evaluationTrace: EvaluationTrace;
}

export interface ProjectProperties {
    configuration: string;
    platform: string;
    nullable?: string;
    langVersion?: string;
    implicitUsings?: string;
    assemblyName?: string;
    rootNamespace?: string;
    enableDefaultCompileItems?: string;
    defineConstants?: string[];
    [other: string]: unknown;
}

export interface ParseOptions {
    /** Defaults to "Debug" (matches cs_kit). Accepts "Debug|AnyCPU" shape too. */
    configuration?: string;
}
