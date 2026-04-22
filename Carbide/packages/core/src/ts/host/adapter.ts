/**
 * A host adapter abstracts the boot-time and I/O differences between Node and a browser.
 * M1 only needs the methods below. M3 adds reference-upload plumbing; M4 adds writeFile.
 */
export interface HostAdapter {
    readonly hostKind: "browser" | "node";

    /**
     * URL (with trailing slash) from which individual metadata DLLs are fetched by the
     * running C# runtime through HttpClient. In Node this must be an http:// URL — the
     * Mono-WASM HttpClient's file:// stream path reports 0 content-length and truncates
     * reads. In the browser this is typically the same origin/directory that serves dotnet.js.
     */
    resolveFrameworkAssetsBaseUrl(): Promise<string>;

    /**
     * URL used for the ES-module `import()` of dotnet.js. In Node this must be a file:// URL
     * because Node's default ESM loader refuses http(s):// schemes. In the browser this is
     * typically the same base URL that serves the rest of _framework/.
     *
     * Defaults to `resolveFrameworkAssetsBaseUrl()` when the adapter does not override.
     */
    resolveDotnetModuleUrl?(): Promise<string>;

    /**
     * Describes where to fetch `@carbide/refs-net10.0` (or a sibling ref-pack). When present,
     * Carbide boots with ref-pack DLL URLs merged into the runtime InitAsync call. When
     * absent (or null), Carbide falls back to the runtime-DLL path as the compile-time
     * API surface.
     *
     * The adapter is expected to resolve the manifest locally (e.g. via `readFile` in Node)
     * and return `dllNames` directly; this avoids cross-scheme `fetch()` issues that occur
     * when ref-pack assets live on disk but are eventually delivered over HTTP.
     *
     * Returning `null` is a first-class outcome (not an error).
     */
    resolveReferencePack?(): Promise<ReferencePackDescriptor | null>;

    /**
     * T1 — optional emscripten-level Module field overlays merged into
     * `DotnetHostBuilder.withConfig(...)` at boot time. Currently used by the browser
     * adapter to install `print`/`printErr` multiplexers that route native-side writes
     * (e.g. bytes emitted via `Console.OpenStandardOutput()`) into the active interactive
     * terminal's bridge when one is attached, and to the browser devtools console
     * otherwise. Non-browser adapters typically return `{}` or omit the method.
     *
     * The returned object is consulted once per runtime boot; the multiplexer pattern
     * supports dynamic routing without re-booting.
     */
    resolveRuntimeConfigOverlays?(): Promise<
        import("../runtime/dotnet-types.js").EmscriptenModuleOverlays
    >;

    /**
     * core-P1 (plan §10.1): resolve an additional ref-pack by npm package name,
     * read its `refpack.json`, and load the listed DLL bytes. The session applies
     * them to the current session via `addReference` during `initializeAsync` when
     * the caller sets `CarbideOptions.sideload`. Adapters that don't implement this
     * method cause session init to throw when sideload is non-empty.
     */
    loadSideloadRefPack?(packageName: string): Promise<SideloadedRefPack>;

    /** Release any resources acquired by the adapter (HTTP server sockets, buffered captures). */
    dispose(): Promise<void>;
}

export interface ReferencePackDescriptor {
    /** URL (with trailing slash) from which each DLL name joins to a fetchable URL. */
    readonly baseUrl: string;
    /** DLL file names; the boot joins each against `baseUrl` to form the full URL. */
    readonly dllNames: readonly string[];
}

export interface SideloadedRefPack {
    /** The npm package name that was resolved (e.g. `"@carbide-ui/refs-avalonia"`). */
    readonly packageName: string;
    /** Absolute path or URL to the resolved `refpack.json` — useful for diagnostics. */
    readonly manifestPath: string;
    /** DLL bytes keyed by file name. The session calls `addReference(bytes, name)` per entry. */
    readonly dlls: readonly { readonly name: string; readonly bytes: Uint8Array }[];
}
