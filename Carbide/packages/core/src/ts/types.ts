export type DiagnosticSeverity = "error" | "warning" | "info" | "hidden";

export interface Diagnostic {
    id: string;
    severity: DiagnosticSeverity;
    message: string;
    path?: string;
    spanStart: number;
    spanEnd: number;
    lineStart?: number;
    lineEnd?: number;
    columnStart?: number;
    columnEnd?: number;
}

export interface RunResult {
    schemaVersion: number;
    success: boolean;
    exitCode?: number;
    stdOut: string;
    stdErr: string;
    uncaughtException?: string | null;
    durationMs: number;
    diagnostics: Diagnostic[];
}

export interface ProjectOptions {
    targetFramework?: "net8.0" | "net10.0";
    languageVersion?: string;
    nullable?: boolean;
    implicitUsings?: boolean;
    assemblyName?: string;
    rootNamespace?: string;
}
