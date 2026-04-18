import type { HostAdapter } from "../adapter.js";

export interface BrowserAdapterOptions {
    /**
     * Explicit override for the base URL that serves `_framework/`. When omitted, the adapter
     * derives it from the directory containing the module-level import.meta.url plus the
     * standard path that `@carbide/core`'s publish places the runtime at.
     */
    frameworkAssetsBaseUrl?: string;
    /** Module URL used to derive the default base URL. */
    moduleUrl?: string;
}

/**
 * Browser host adapter.
 *
 * Deliberately thin in M1: it only materialises the base URL for `_framework/`. Stdout
 * capture is owned by the running C# program through Console.SetOut redirection, so the
 * adapter does not currently inject anything into the browser's console.
 */
export class BrowserHostAdapter implements HostAdapter {
    public readonly hostKind = "browser" as const;
    private readonly baseUrl: string;

    constructor(options: BrowserAdapterOptions = {}) {
        if (options.frameworkAssetsBaseUrl) {
            this.baseUrl = ensureTrailingSlash(options.frameworkAssetsBaseUrl);
            return;
        }

        const moduleUrl = options.moduleUrl;
        if (!moduleUrl) {
            throw new Error(
                "BrowserHostAdapter requires either frameworkAssetsBaseUrl or moduleUrl; pass import.meta.url from the call site.",
            );
        }
        const moduleDir = moduleUrl.slice(0, moduleUrl.lastIndexOf("/") + 1);
        // Default layout: the TS entry sits at dist/, the published _framework/ is at src/bin/Release/net10.0/publish/wwwroot/_framework/.
        // Consumers that package differently can override via frameworkAssetsBaseUrl.
        this.baseUrl = new URL("../src/bin/Release/net10.0/publish/wwwroot/_framework/", moduleDir).toString();
    }

    resolveFrameworkAssetsBaseUrl(): Promise<string> {
        return Promise.resolve(this.baseUrl);
    }

    dispose(): Promise<void> {
        return Promise.resolve();
    }
}

function ensureTrailingSlash(url: string): string {
    return url.endsWith("/") ? url : `${url}/`;
}
