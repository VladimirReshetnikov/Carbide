// Shared types for @carbide/msbuild-lite. Mirrors the shape that cs_kit.msbuild_lite emits
// so TS and Python consumers see the same JSON.

export type WarningSeverity = "warning" | "error";

export interface Warning {
    code: string;
    message: string;
    category: string;
    severity: WarningSeverity;
    /**
     * M11: file in which the warning was raised. Null/absent for warnings that can't be
     * attributed to a specific file (rare — typically only when the outer parseCsproj
     * itself is the failure site). Existing consumers that ignore unknown fields are
     * unaffected.
     */
    sourceFile?: string;
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
    /**
     * M11: chronological list of every file the evaluator touched during parseCsproj,
     * including auto-discovered Directory.Build.props, explicit `<Import>` elements, and
     * refused Directory.Build.targets. Absent in the pre-M11 shape.
     */
    imports?: ImportTraceEntry[];
}

/** M11 — one entry per evaluator-visited file. */
export interface ImportTraceEntry {
    /** File that issued the import. Null for the root csproj and the auto-discovered Directory.Build.*. */
    sourceFile: string | null;
    /** Absolute path of the file being imported / walked. */
    importedFile: string;
    /** Origin of the import. */
    kind: "csproj" | "props" | "targets" | "import";
    /** True when the file was walked successfully. False for cycles, missing targets, parse failures, duplicates, refused targets. */
    applied: boolean;
    /** When `applied` is false, a short reason code. */
    error?: "cycle" | "duplicate" | "missing" | "parse" | "refused";
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
