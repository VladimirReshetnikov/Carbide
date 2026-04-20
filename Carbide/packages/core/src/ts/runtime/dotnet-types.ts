/**
 * Minimal shape of the `dotnet` export produced by `dotnet publish` in .NET 10 Mono-WASM.
 *
 * Not every builder method is modelled — only the ones Carbide actually calls. This keeps
 * our surface traceable; any new method we consume must be added here deliberately.
 */

export interface MonoConfig {
    mainAssemblyName?: string;
    debugLevel?: number;
    diagnosticTracing?: boolean;
    disableIntegrityCheck?: boolean;
    resources?: {
        coreAssembly?: Array<{ name: string; virtualPath?: string }>;
        assembly?: Array<{ name: string; virtualPath?: string }>;
        satelliteResources?: Record<string, Array<{ name: string; virtualPath?: string }>>;
        jsModuleNative?: Array<{ name: string }>;
        jsModuleRuntime?: Array<{ name: string }>;
        wasmNative?: Array<{ name: string }>;
    };
}

export interface RuntimeAPI {
    getAssemblyExports(assemblyName: string): Promise<AssemblyExports>;
    getConfig(): MonoConfig;
}

export interface AssemblyExports {
    [namespaceSegment: string]: unknown;
}

export interface DotnetHostBuilder {
    /**
     * Accepts `MonoConfig` fields plus emscripten Module overlays (see
     * {@link EmscriptenModuleOverlays}). The real dotnet.js is permissive about extra keys;
     * the widened type lets Carbide pass `print` / `printErr` through the same call.
     */
    withConfig(config: Partial<MonoConfig> & EmscriptenModuleOverlays): DotnetHostBuilder;
    withDiagnosticTracing(enabled: boolean): DotnetHostBuilder;
    withDebugging(level: number): DotnetHostBuilder;
    create(): Promise<RuntimeAPI>;
}

/**
 * The `DotnetModuleConfig` shape that dotnet.js's default export accepts. Public fields
 * from upstream's dotnet.d.ts plus the emscripten-level `print` / `printErr` overlays we
 * care about. The builder (`dotnet.withConfig(...).create()`) only accepts `MonoConfig`
 * fields, so print/printErr have to travel through the factory path instead.
 */
export interface DotnetModuleConfig extends EmscriptenModuleOverlays {
    config?: MonoConfig;
    onConfigLoaded?: (config: MonoConfig) => void | Promise<void>;
    onDotnetReady?: () => void | Promise<void>;
    onDownloadResourceProgress?: (resourcesLoaded: number, totalResources: number) => void;
}

/** Signature of dotnet.js's default export — a factory that boots the runtime. */
export type DotnetRuntimeFactory = (config: DotnetModuleConfig) => Promise<RuntimeAPI>;

export interface DotnetModule {
    /** Default export: a factory that accepts a full DotnetModuleConfig (incl. print/printErr). */
    default: DotnetRuntimeFactory;
    /** Fluent builder. Convenient for MonoConfig fields but can't set emscripten-level overrides. */
    dotnet: DotnetHostBuilder;
    exit?(code: number, reason?: unknown): void;
}

/**
 * T1 — a subset of emscripten's Module fields that Carbide routes through the factory
 * path into the boot. Host adapters return these through `resolveRuntimeConfigOverlays()`
 * so runtime-side stdout/stderr writes (including bytes from `Console.OpenStandardOutput()`)
 * reach the interactive terminal bridge instead of the browser devtools console.
 *
 * Emscripten calls `print(text)` once per line with the trailing newline stripped; `printErr`
 * is the stderr counterpart. Both are absent by default and fall back to `console.log` /
 * `console.error`. `DotnetHostBuilder.withConfig` silently ignores these fields (they
 * aren't `MonoConfig` members), which is why Carbide boots via the factory path when a
 * host adapter advertises overlays.
 */
export interface EmscriptenModuleOverlays {
    print?(text: string): void;
    printErr?(text: string): void;
}

export interface CarbideInteropExports {
    InitAsync(assemblies: string[]): Promise<void>;
    /**
     * U1.2 — sets the runtime logger's minimum level. Accepts Microsoft.Extensions.Logging
     * names (trace, debug, information, warning, error, critical, none). Called before
     * {@link InitAsync} to suppress the "Carbide initialising ..." info line.
     */
    SetLogLevel(level: string): void;
    CreateSession(optionsJson: string): string;
    DisposeSession(sessionId: string): void;
    CreateProject(sessionId: string, optionsJson: string): string;
    AddSource(projectId: string, path: string, code: string): void;
    UpdateSource(projectId: string, path: string, code: string): void;
    RemoveSource(projectId: string, path: string): void;
    AddReference(sessionId: string, base64Bytes: string, name: string | null): string;
    RemoveReference(sessionId: string, referenceId: string): boolean;
    AttachReference(projectId: string, referenceId: string): void;
    GetDiagnosticsAsync(projectId: string): Promise<string>;
    BuildAsync(projectId: string): Promise<string>;
    /**
     * U2 — second parameter `runOptionsJson` is a {@link RunOptionsRequest}-shaped JSON
     * string carrying argv and stdin. An empty string means "defaults" (no args, no
     * stdin), which skips JSON parsing on the C# side.
     */
    RunAsync(projectId: string, runOptionsJson: string): Promise<string>;
    /**
     * T1 — interactive run path. `optionsJson` is a
     * {@link import("../interop/schema.js").RunInteractiveOptionsRequest}-shaped JSON
     * string. The C# side installs streaming `Console.Out`/`Console.Error` writers that push
     * into `globalThis.Carbide.Terminal.{write,writeErr}` before invoking the entry point.
     * Resolves with the same `RunResult` shape as `RunAsync` plus a final drain of pending
     * output.
     */
    RunInteractiveAsync(projectId: string, optionsJson: string): Promise<string>;
    /**
     * T1 — signal the C# side that the interactive session is tearing down. The current T1
     * implementation is a stub that records the teardown; T2's `BrowserTerminalReader.Complete()`
     * runs on state-disposal anyway (via `using InputStateDisposer`), so disposing the
     * interop stays a low-cost signal rather than a required teardown step.
     */
    DisposeTerminal(projectId: string): void;
    /**
     * T2 — deliver stdin to the active interactive run. `isKeyMode` distinguishes between
     * the line-editor's committed-line path (false) and the raw-byte key-mode path (true).
     * The C# side routes into the project's `BrowserTerminalReader` and resolves any
     * pending `ReadLineAsync` / `ReadKeyAsync`.
     */
    DeliverStdIn(projectId: string, isKeyMode: boolean, data: string): void;
    /** T2 — xterm resize notification. Updates `CarbideConsole.WindowWidth/Height`. */
    NotifyResize(projectId: string, cols: number, rows: number): void;
    /**
     * T2 — signal delivery. T2 honors `"SIGINT"` (Ctrl+C); unknown names are silently
     * ignored so the TS side can future-proof.
     */
    DeliverSignal(projectId: string, signalName: string): void;
    /**
     * T2 — propagate C#-side `CarbideConsole.TreatControlCAsInput` changes to the JS line
     * editor so Ctrl+C routes through the right path (byte vs signal) without a per-keystroke
     * round-trip.
     */
    SetTreatControlCAsInput(projectId: string, value: boolean): void;
}
