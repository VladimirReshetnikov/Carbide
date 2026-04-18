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

/**
 * Outcome of {@link Project.build}. On success, `pe` holds the compiled assembly's bytes and
 * `pdb` holds the portable-PDB bytes (unless debug info was disabled). On failure, both are
 * absent and `diagnostics` carries the reason.
 */
export interface BuildResult {
    schemaVersion: number;
    success: boolean;
    pe?: Uint8Array;
    pdb?: Uint8Array;
    diagnostics: Diagnostic[];
    durationMs: number;
}

export interface ProjectOptions {
    targetFramework?: "net8.0" | "net10.0";
    languageVersion?: string;
    nullable?: boolean;
    implicitUsings?: boolean;
    assemblyName?: string;
    rootNamespace?: string;
}

/**
 * Opaque handle for a reference registered on a session via {@link CarbideSession.addReference}.
 * The session owns the reference's lifetime; removing it via
 * {@link CarbideSession.removeReference} or shutting the session down invalidates the handle.
 * Attaching an invalidated handle throws.
 */
export interface ReferenceHandle {
    readonly id: string;
    readonly name?: string;
    readonly sessionId: string;
    /** True once the session has disposed the reference (or the session itself). */
    readonly disposed: boolean;
}
