import type { HostAdapter } from "../host/adapter.js";
import type {
    CarbideInteropExports,
    DotnetHostBuilder,
    DotnetModule,
    MonoConfig,
    RuntimeAPI,
} from "./dotnet-types.js";

export interface BootOptions {
    hostAdapter: HostAdapter;
    debugLevel?: number;
    enableDiagnosticTracing?: boolean;
}

export interface BootResult {
    interop: CarbideInteropExports;
    runtime: RuntimeAPI;
    config: MonoConfig;
}

/**
 * Boots the .NET WASM runtime packaged with @carbide/core and returns the resolved
 * CompilationInterop. All adapter-specific concerns (URL shape, stream capture) are isolated
 * behind the HostAdapter.
 */
export async function bootRuntime(options: BootOptions): Promise<BootResult> {
    const baseUrl = await options.hostAdapter.resolveFrameworkAssetsBaseUrl();
    const moduleBaseUrl = options.hostAdapter.resolveDotnetModuleUrl
        ? await options.hostAdapter.resolveDotnetModuleUrl()
        : baseUrl;
    const dotnetJsUrl = new URL("dotnet.js", moduleBaseUrl).toString();

    // Dynamic import keeps the runtime out of the static dependency graph so bundlers that
    // don't know how to handle the .NET-shipped dotnet.js don't get confused.
    const module = (await import(/* @vite-ignore */ dotnetJsUrl)) as DotnetModule;
    const builder = module.dotnet as DotnetHostBuilder | undefined;
    if (!builder || typeof builder.withConfig !== "function") {
        throw new Error(
            `dotnet.js did not export the expected host-builder shape (got ${typeof builder}). Was the module fetched from the correct URL '${dotnetJsUrl}'?`,
        );
    }

    const runtime = await builder
        .withConfig({
            debugLevel: options.debugLevel ?? 0,
            diagnosticTracing: options.enableDiagnosticTracing ?? false,
            disableIntegrityCheck: true,
        })
        .create();

    const config = runtime.getConfig();
    const mainAssemblyName = config.mainAssemblyName;
    if (!mainAssemblyName) {
        throw new Error("dotnet.js config did not advertise a mainAssemblyName; cannot reach CompilationInterop.");
    }

    const exportsRoot = (await runtime.getAssemblyExports(mainAssemblyName)) as Record<string, unknown>;
    const interop = locateInterop(exportsRoot);
    const assemblyUrls = resolveAssemblyUrls(config, baseUrl);
    await interop.InitAsync(assemblyUrls);

    return { interop, runtime, config };
}

function locateInterop(root: Record<string, unknown>): CarbideInteropExports {
    // .NET's JSExport namespace surface mirrors the C# namespace tree:
    //   exportsRoot.Carbide.Core.CompilationInterop
    const node = (root.Carbide as Record<string, unknown> | undefined)?.Core as Record<string, unknown> | undefined;
    const interop = node?.CompilationInterop as CarbideInteropExports | undefined;
    if (!interop || typeof interop.InitAsync !== "function") {
        throw new Error(
            "Carbide.Core.CompilationInterop was not reachable through the assembly exports tree. Did the runtime boot the expected assembly?",
        );
    }
    return interop;
}

function resolveAssemblyUrls(config: MonoConfig, baseUrl: string): string[] {
    const resources = config.resources ?? {};
    // Satellite locale resources (culture-specific `.resources.dll`) contain only resource blobs
    // and are rejected by Roslyn as CS0009 "PE image doesn't contain managed metadata".
    // cs-agent-tools' wasmsharp patch-health flags the same issue; Carbide avoids it by design.
    const all = [...(resources.coreAssembly ?? []), ...(resources.assembly ?? [])];
    const seen = new Set<string>();
    const urls: string[] = [];
    for (const entry of all) {
        if (!entry?.name || seen.has(entry.name)) continue;
        seen.add(entry.name);
        urls.push(new URL(entry.name, baseUrl).toString());
    }
    return urls;
}
