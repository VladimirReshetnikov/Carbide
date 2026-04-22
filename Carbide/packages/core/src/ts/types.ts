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
    /**
     * core-P3: version of the PE/PDB wire contract. Always `1` in v1; undefined on failed
     * builds. Exists so downstream consumers (e.g. `@carbide-ui/launcher`) can guard
     * against shape changes without inspecting Carbide's own `schemaVersion`.
     */
    peSchemaVersion?: number;
    /**
     * core-P3: the compilation's primary assembly name (e.g. `"MyApp"`). Useful as a
     * fallback for caller-supplied app-class inference. Undefined on failed builds.
     */
    primaryAssemblyName?: string;
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

/**
 * T1 — options for {@link Project.runInteractive}. Requires a caller-supplied xterm.js
 * `Terminal` instance; the host page owns xterm's DOM lifecycle, Carbide owns the bridge.
 *
 * The `Terminal` dependency is structural — Carbide never imports `@xterm/xterm` directly
 * so the core package stays framework-agnostic and the host page picks the xterm version.
 * Any object implementing the methods Carbide actually uses (`write`, `dispose`) works at
 * runtime; the `XtermTerminalLike` type below names that minimal contract.
 */
export interface InteractiveRunOptions {
    /**
     * An xterm.js Terminal instance the host page constructed and already `open()`ed into a
     * DOM node. Carbide only calls `terminal.write(...)` on it (T1); T2 extends usage to
     * `onData`, `onResize`, etc.
     */
    terminal: XtermTerminalLike;
    /**
     * Program arguments — same shape as {@link RunOptions.args}. Forwarded to the entry
     * point's `Main(string[] args)` parameter.
     */
    args?: readonly string[];
    /**
     * SGR style wrapped around each stderr flush chunk before writing to xterm. `"plain"`
     * (default) emits bytes unchanged. `"dim"` wraps with `\x1b[2m…\x1b[22m`. `"red"` wraps
     * with `\x1b[31m…\x1b[39m`.
     */
    stderrStyle?: "plain" | "dim" | "red";
}

/**
 * Minimal subset of xterm.js's `Terminal` API that Carbide T1/T2 depend on. Declared
 * structurally so `@carbide/core` doesn't pull in `@xterm/xterm` at compile time. The host
 * page is responsible for matching this contract at runtime.
 *
 * T2 adds `onData` (user keystrokes) and `onResize` (geometry changes) — both optional on
 * the type so T1 callers with a reduced mock can still satisfy `XtermTerminalLike`, but
 * absent at runtime means no input/resize plumbing is wired.
 */
export interface XtermTerminalLike {
    write(data: string | Uint8Array): void;
    /**
     * Subscribe to user input. Carbide's line editor consumes the emitted strings. Returns
     * an xterm-shaped disposable; T2 session teardown calls `.dispose()` on it.
     */
    onData?(listener: (data: string) => void): { dispose(): void };
    /**
     * Subscribe to resize events. Payload carries the new `{ cols, rows }`; Carbide pushes
     * those through `NotifyResize` to refresh `CarbideConsole.WindowWidth` / `Height`.
     */
    onResize?(listener: (size: { cols: number; rows: number }) => void): { dispose(): void };
    /** xterm's current column count. Used to prime `CarbideConsole.WindowWidth`. */
    readonly cols?: number;
    /** xterm's current row count. Used to prime `CarbideConsole.WindowHeight`. */
    readonly rows?: number;
    /**
     * xterm's buffer API, used by the line editor's Ctrl+L (clear-and-redraw) path to
     * read back the current prompt row before clearing. Declared optional so minimal
     * mocks remain compatible.
     */
    readonly buffer?: {
        readonly active?: {
            readonly cursorY: number;
            getLine(y: number): { translateToString(trimRight: boolean): string } | undefined;
        };
    };
}

/**
 * T1 — handle returned by {@link Project.runInteractive}. Resolves via {@link exitPromise}
 * when the user program's entry point returns, throws, or the session is disposed.
 */
export interface TerminalSession {
    /**
     * Resolves when the user program exits. Never rejects — uncaught user exceptions surface
     * as `RunResult.success === false` with a populated `stdErr`.
     */
    readonly exitPromise: Promise<RunResult>;
    /**
     * Tear down the terminal bridge. Idempotent. Safe to call mid-run; the C# side observes
     * the teardown signal on the next flush attempt and unwinds cleanly. Awaits the
     * in-flight run to finish (or its drain flush to complete) before resolving.
     */
    dispose(): Promise<void>;
}
