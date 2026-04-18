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

    /** Release any resources acquired by the adapter (HTTP server sockets, buffered captures). */
    dispose(): Promise<void>;
}
