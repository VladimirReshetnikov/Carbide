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
    withConfig(config: Partial<MonoConfig>): DotnetHostBuilder;
    withDiagnosticTracing(enabled: boolean): DotnetHostBuilder;
    withDebugging(level: number): DotnetHostBuilder;
    create(): Promise<RuntimeAPI>;
}

export interface DotnetModule {
    dotnet: DotnetHostBuilder;
    exit?(code: number, reason?: unknown): void;
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
}
