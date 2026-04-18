import { createRequire } from "node:module";
import { fileURLToPath, pathToFileURL } from "node:url";
import path from "node:path";
import { access } from "node:fs/promises";
import type { HostAdapter } from "../adapter.js";
import { type AssetServerHandle, startAssetServer } from "./asset-server.js";

export type NodeAdapterAssetDelivery = "file" | "http";

export interface NodeAdapterOptions {
    /**
     * Explicit override for the directory that contains `dotnet.js`, `dotnet.native.wasm`, and
     * the bundled BCL. When omitted the adapter locates the package's publish output inside
     * @carbide/core's install tree.
     */
    frameworkDir?: string;
    /**
     * "http" (default): start a localhost HTTP server via startAssetServer and point the
     *   runtime at it. Matches what cs-agent-tools' WasmSharp adapter uses — the Mono-WASM
     *   HttpClient's file:// stream path reports 0-byte content-length and truncates DLL
     *   reads, so http:// is what actually works end-to-end today.
     * "file": hand the runtime a file:// URL. Retained for future use once the Mono-WASM
     *   file:// fetch shim exposes body bytes reliably.
     */
    assetDelivery?: NodeAdapterAssetDelivery;
}

/**
 * Node host adapter.
 *
 * Locates @carbide/core's bundled `_framework/` directory and presents it to the runtime as a
 * URL. Defaults to file:// which works in-process with no server lifecycle; switchable to an
 * asset-server URL on request.
 */
export class NodeHostAdapter implements HostAdapter {
    public readonly hostKind = "node" as const;

    private readonly delivery: NodeAdapterAssetDelivery;
    private readonly frameworkDirPromise: Promise<string>;
    private serverHandle: AssetServerHandle | null = null;

    constructor(options: NodeAdapterOptions = {}) {
        this.delivery = options.assetDelivery ?? "http";
        this.frameworkDirPromise = options.frameworkDir
            ? Promise.resolve(options.frameworkDir)
            : locateFrameworkDir();
    }

    async resolveFrameworkAssetsBaseUrl(): Promise<string> {
        const dir = await this.frameworkDirPromise;
        if (this.delivery === "http") {
            if (!this.serverHandle) {
                this.serverHandle = await startAssetServer(dir);
            }
            return this.serverHandle.baseUrl;
        }
        return fileDirUrl(dir);
    }

    /**
     * Node's default ESM loader refuses to resolve http(s):// URLs, so we always import
     * dotnet.js through file:// even when the asset server is serving DLLs over HTTP.
     */
    async resolveDotnetModuleUrl(): Promise<string> {
        const dir = await this.frameworkDirPromise;
        return fileDirUrl(dir);
    }

    async dispose(): Promise<void> {
        if (this.serverHandle) {
            await this.serverHandle.close();
            this.serverHandle = null;
        }
    }
}

function fileDirUrl(dir: string): string {
    const url = pathToFileURL(dir).toString();
    return url.endsWith("/") ? url : `${url}/`;
}

async function locateFrameworkDir(): Promise<string> {
    // Inside the npm install tree, @carbide/core's package.json publishes the framework at a
    // known relative path. When running from the source repo we resolve it the same way:
    // find this module's on-disk directory, then walk to the sibling publish output.
    const candidates = candidateFrameworkDirs();
    for (const candidate of candidates) {
        if (await dirExists(candidate)) {
            return candidate;
        }
    }
    throw new Error(
        `NodeHostAdapter could not locate the Carbide _framework/ directory. Tried:\n` +
            candidates.map((p) => `  ${p}`).join("\n") +
            `\nPass frameworkDir explicitly in options to override.`,
    );
}

function candidateFrameworkDirs(): string[] {
    const results: string[] = [];
    const thisFile = fileURLToPath(import.meta.url);
    const thisDir = path.dirname(thisFile);

    // When installed, the compiled JS sits under dist/ and the publish output sits under src/bin/.
    // When running from source (ts-node etc.), the same relative walk applies.
    // Layouts tried, in order:
    //   1. <pkg>/src/bin/Release/net10.0/publish/wwwroot/_framework
    //   2. <pkg>/dist/_framework (a post-pack reorganisation we might do later)
    //   3. <pkg>/_framework (simpler layout)
    const walkUps = [
        path.resolve(thisDir, "../../../.."),                    // dist/host/node -> pkg
        path.resolve(thisDir, "../../../../.."),                 // src/ts/host/node -> pkg
        path.resolve(thisDir, "../../../"),                      // host/node -> pkg  (if dist is flatter)
        path.resolve(thisDir, "../../"),                         // node -> pkg
        path.resolve(thisDir, "../"),                            // direct
    ];

    const seen = new Set<string>();
    for (const pkgRoot of walkUps) {
        if (seen.has(pkgRoot)) continue;
        seen.add(pkgRoot);
        results.push(path.join(pkgRoot, "src", "bin", "Release", "net10.0", "publish", "wwwroot", "_framework"));
        results.push(path.join(pkgRoot, "dist", "_framework"));
        results.push(path.join(pkgRoot, "_framework"));
    }

    // Also try require.resolve-based location for installed packages.
    try {
        const require = createRequire(import.meta.url);
        const packageJsonPath = require.resolve("@carbide/core/package.json");
        const pkgDir = path.dirname(packageJsonPath);
        results.push(path.join(pkgDir, "src", "bin", "Release", "net10.0", "publish", "wwwroot", "_framework"));
        results.push(path.join(pkgDir, "dist", "_framework"));
        results.push(path.join(pkgDir, "_framework"));
    } catch {
        // @carbide/core is not installed as a node_module (we are running from source); ignore.
    }

    return results;
}

async function dirExists(dir: string): Promise<boolean> {
    try {
        await access(dir);
        return true;
    } catch {
        return false;
    }
}
