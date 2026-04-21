import type { HostAdapter } from "../host/adapter.js";
import type {
    CarbideInteropExports,
    DotnetModule,
    EmscriptenModuleOverlays,
    MonoConfig,
    RuntimeAPI,
} from "./dotnet-types.js";
import { installDefaultBridge } from "../terminal/bridge.js";

export interface BootOptions {
    hostAdapter: HostAdapter;
    debugLevel?: number;
    enableDiagnosticTracing?: boolean;
    /**
     * U1.2 — minimum log level for Carbide.Core's `ILogger<>` instances. Defaults to
     * `"warning"` on the C# side. Pass `"information"` (or higher-verbosity levels) for
     * the pre-U1 "chatty" behaviour.
     */
    logLevel?: "trace" | "debug" | "information" | "warning" | "error" | "none";
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

    // Install the default `globalThis.Carbide.Terminal` surface BEFORE importing dotnet.js.
    // The forked System.Console's `CarbideStdWriteStream` (returned from
    // `Console.OpenStandardOutput()`) resolves the `Carbide.Terminal.write` JSImport the
    // first time user code writes raw bytes — if that happens in a non-interactive run
    // (e.g. `project.run()` from the CLI), there's no `installBridge` to supply the
    // global and the import used to throw `Carbide not found while looking up ...`.
    // The default bridge routes to process.stdout / process.stderr (Node) or
    // console.log / console.error (browser), so non-interactive runs get byte-accurate
    // output to the parent stream. `installBridge` overrides these during an interactive
    // session and `uninstallBridge` restores them on teardown.
    installDefaultBridge();

    // Dynamic import keeps the runtime out of the static dependency graph so bundlers that
    // don't know how to handle the .NET-shipped dotnet.js don't get confused.
    const module = (await import(/* @vite-ignore */ dotnetJsUrl)) as DotnetModule;

    // T1 — host adapters can supply emscripten-level `print` / `printErr` overrides (and
    // other Module-level fields in future) via `resolveRuntimeConfigOverlays`. The browser
    // adapter uses this to route native-side writes into the interactive terminal bridge
    // when one is attached. Two boot paths exist:
    //
    //  * Factory path (`module.default({ config, print, printErr, ... })`): accepts a full
    //    `DotnetModuleConfig` including emscripten-level overlays. Used whenever the adapter
    //    advertises overlays so `Console.OpenStandardOutput()` bytes reach the bridge.
    //  * Builder path (`module.dotnet.withConfig(...).create()`): the chain-API, but
    //    `withConfig` only accepts `MonoConfig` fields and silently drops `print`/`printErr`.
    //    Used as the no-overlay fast path so existing callers (incl. every Node code path)
    //    see byte-identical boot behaviour.
    const overlays: EmscriptenModuleOverlays = options.hostAdapter.resolveRuntimeConfigOverlays
        ? await options.hostAdapter.resolveRuntimeConfigOverlays()
        : {};
    const hasOverlays = typeof overlays.print === "function" || typeof overlays.printErr === "function";

    const monoConfig: Partial<MonoConfig> = {
        debugLevel: options.debugLevel ?? 0,
        diagnosticTracing: options.enableDiagnosticTracing ?? false,
        disableIntegrityCheck: true,
    };

    let runtime: RuntimeAPI;
    if (hasOverlays) {
        if (typeof module.default !== "function") {
            throw new Error(
                `dotnet.js did not export the expected runtime factory (got ${typeof module.default}). ` +
                    `Was the module fetched from the correct URL '${dotnetJsUrl}'?`,
            );
        }
        runtime = await module.default({
            config: monoConfig as MonoConfig,
            print: overlays.print,
            printErr: overlays.printErr,
        });
    } else {
        const builder = module.dotnet;
        if (!builder || typeof builder.withConfig !== "function") {
            throw new Error(
                `dotnet.js did not export the expected host-builder shape (got ${typeof builder}). ` +
                    `Was the module fetched from the correct URL '${dotnetJsUrl}'?`,
            );
        }
        runtime = await builder.withConfig(monoConfig).create();
    }

    const config = runtime.getConfig();
    const mainAssemblyName = config.mainAssemblyName;
    if (!mainAssemblyName) {
        throw new Error("dotnet.js config did not advertise a mainAssemblyName; cannot reach CompilationInterop.");
    }

    const exportsRoot = (await runtime.getAssemblyExports(mainAssemblyName)) as Record<string, unknown>;
    const interop = locateInterop(exportsRoot);

    const refPackUrls = options.hostAdapter.resolveReferencePack
        ? resolveRefPackUrls(await options.hostAdapter.resolveReferencePack())
        : [];
    // When a ref-pack is available, use *only* its DLLs for the compile-time metadata set.
    // Mixing ref-pack (untrimmed reference assemblies) with the runtime (trimmed
    // implementation assemblies) yields duplicate type definitions and CS0518 "Predefined
    // type not defined" errors because Roslyn can't decide which copy to bind against.
    // Without a ref-pack, fall back to the M1/M2 runtime-DLL path.
    const assemblyUrls = refPackUrls.length > 0
        ? refPackUrls
        : resolveAssemblyUrls(config, baseUrl);

    // Set the log level *before* InitAsync — the first "Carbide initialising ..." info
    // line fires inside InitAsync, so the min-level must already be in place.
    if (options.logLevel) {
        interop.SetLogLevel(options.logLevel);
    }

    await interop.InitAsync(assemblyUrls);

    return { interop, runtime, config };
}

/**
 * Joins each dll name against the ref-pack's base URL. Returns an empty array when the
 * adapter reports no ref-pack (Carbide falls back to the runtime-DLL path).
 */
function resolveRefPackUrls(
    pack: import("../host/adapter.js").ReferencePackDescriptor | null,
): string[] {
    if (!pack) return [];
    return pack.dllNames.map((name) => new URL(name, pack.baseUrl).toString());
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
