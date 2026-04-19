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
    /** C# language version string (e.g. "latest", "preview", "12"). Passed through to Roslyn's CSharpParseOptions. */
    languageVersion?: string;
    /** When true, enables the `Enable` NullableContextOptions globally. */
    nullable?: boolean;
    /**
     * When true (default), Carbide injects a hidden implicit-usings document so bare
     * `Console.WriteLine` compiles. Set to false to match strict `<ImplicitUsings>disable</ImplicitUsings>`.
     */
    implicitUsings?: boolean;
    assemblyName?: string;
    rootNamespace?: string;
    /** Preprocessor symbols (for `#if DEBUG`, `#if MY_FEATURE`, etc.). Equivalent to `<DefineConstants>`. */
    defineConstants?: string[];
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

/**
 * U2 — optional knobs for {@link Project.run}. When every field is default (or the caller
 * omits the options entirely), the interop boundary is called without marshalling a JSON
 * blob at all.
 */
export interface RunOptions {
    /**
     * Program arguments forwarded to the entry point's `Main(string[] args)`. Bound by
     * parameter count — programs that take no args get nothing; programs with a different
     * parameter shape are not supported.
     */
    args?: readonly string[];
    /**
     * Eagerly-buffered stdin for the user program. `null` / omitted leaves `Console.In`
     * disconnected (same as pre-U2). Binary stdin is not supported; this is UTF-8 text.
     */
    stdin?: string | null;
}
