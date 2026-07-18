import { createRequire } from "node:module";
import { fileURLToPath, pathToFileURL } from "node:url";
import path from "node:path";
import { access, readdir, readFile } from "node:fs/promises";
import type { HostAdapter, ReferencePackDescriptor, SideloadedRefPack } from "../adapter.js";
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

    /**
     * Locates `@carbide/refs-net10.0` (or a later TFM-sibling) via require.resolve and
     * starts an asset server for its `ref/net10.0/` directory. Returns null when no
     * ref-pack package is installed — Carbide then falls back to the runtime-DLL path.
     *
     * Uses its own asset server rather than serving the ref-pack from the primary framework
     * directory so packages can live anywhere under node_modules without additional wiring.
     */
    async resolveReferencePack(): Promise<ReferencePackDescriptor | null> {
        const info = await locateRefPack();
        if (!info) {
            return null;
        }
        let manifest: { dlls?: Array<{ name: string }> };
        try {
            const text = await readFile(info.manifestPath, "utf8");
            manifest = JSON.parse(text);
        } catch {
            // Manifest is missing or malformed. Fall back as if the ref-pack wasn't installed.
            return null;
        }
        const dllNames = (manifest.dlls ?? []).map((d) => d.name).filter((n): n is string => !!n);
        if (dllNames.length === 0) {
            return null;
        }
        if (!this.refPackServer) {
            this.refPackServer = await startAssetServer(info.refDir);
        }
        return {
            baseUrl: this.refPackServer.baseUrl,
            dllNames,
        };
    }

    private refPackServer: AssetServerHandle | null = null;

    /**
     * core-P1 (plan §10.1): resolve `<packageName>/refpack.json` via Node's
     * createRequire (walks node_modules from this module), read the manifest, and
     * stream each listed DLL's bytes into memory. Returns a descriptor the session
     * applies via `addReference(bytes, dllName)` during `initializeAsync`.
     *
     * Throws with a self-descriptive message when the package isn't installed, the
     * manifest is malformed, or any listed DLL is missing on disk — sideload is
     * explicit; silent partial success would confuse consumers.
     */
    async loadSideloadRefPack(packageName: string): Promise<SideloadedRefPack> {
        const manifestPath = await locateSideloadManifest(packageName);
        let manifest: { refDirectory?: string; dlls?: Array<{ name?: string }> };
        try {
            manifest = JSON.parse(await readFile(manifestPath, "utf8"));
        } catch (err) {
            throw new Error(
                `[sideload] ${packageName}: failed to parse refpack.json at ${manifestPath}: ${(err as Error).message}`,
            );
        }
        if (!manifest.refDirectory) {
            throw new Error(
                `[sideload] ${packageName}: refpack.json is missing the 'refDirectory' field.`,
            );
        }
        const dllList = (manifest.dlls ?? []).filter((d): d is { name: string } => typeof d?.name === "string");
        if (dllList.length === 0) {
            throw new Error(`[sideload] ${packageName}: refpack.json lists no DLLs.`);
        }
        const packageDir = path.dirname(manifestPath);
        const dllDir = path.resolve(packageDir, manifest.refDirectory);
        const dlls = await Promise.all(
            dllList.map(async (d) => {
                const dllPath = path.join(dllDir, d.name);
                try {
                    const bytes = await readFile(dllPath);
                    return { name: d.name, bytes: new Uint8Array(bytes) };
                } catch (err) {
                    throw new Error(
                        `[sideload] ${packageName}: missing DLL at ${dllPath}. ` +
                        `Run 'npm install' or 'npm run build' in that package to regenerate the ref/ tree. ` +
                        `(fs reported: ${(err as Error).message})`,
                    );
                }
            }),
        );
        return { packageName, manifestPath, dlls };
    }

    async dispose(): Promise<void> {
        if (this.serverHandle) {
            await this.serverHandle.close();
            this.serverHandle = null;
        }
        if (this.refPackServer) {
            await this.refPackServer.close();
            this.refPackServer = null;
        }
    }
}

/**
 * core-P1: resolve a sideload package's refpack.json. Tries Node's own resolver first
 * (normal case for consumers who `npm install`ed the package), then falls back to a
 * monorepo-sibling walk-up — matches the precedent set by `locateRefPack` for
 * `@carbide/refs-net10.0`. Throws a self-descriptive error when neither strategy works.
 */
async function locateSideloadManifest(packageName: string): Promise<string> {
    try {
        const require = createRequire(import.meta.url);
        return require.resolve(`${packageName}/refpack.json`);
    } catch {
        // Fall through to the monorepo walk-up.
    }

    const unscoped = packageName.replace(/^@[^/]+\//, "");
    const thisDir = path.dirname(fileURLToPath(import.meta.url));
    const candidates: string[] = [];
    let dir = thisDir;
    // Walk up 10 ancestors. At each level probe:
    //   - packages/<unscoped>/refpack.json (same monorepo root as @carbide/core)
    //   - <unscoped>/refpack.json (adjacent layout)
    //   - <sibling>/packages/<unscoped>/refpack.json for every immediate child
    //     of this ancestor — covers cross-root layouts like @carbide-ui/* living
    //     under Carbide.UI/packages/ while @carbide/core lives under
    //     Carbide/packages/.
    for (let i = 0; i < 10; i++) {
        candidates.push(path.join(dir, "packages", unscoped, "refpack.json"));
        candidates.push(path.join(dir, unscoped, "refpack.json"));
        try {
            const siblings = await readdir(dir);
            for (const sibling of siblings) {
                if (sibling.startsWith(".")) continue;
                candidates.push(path.join(dir, sibling, "packages", unscoped, "refpack.json"));
            }
        } catch {
            // Directory not readable at this ancestor — skip sibling scan for this level.
        }
        const parent = path.dirname(dir);
        if (parent === dir) break;
        dir = parent;
    }
    for (const candidate of candidates) {
        if (await fileExists(candidate)) return candidate;
    }
    throw new Error(
        `[sideload] cannot locate ${packageName}/refpack.json — is the package installed ` +
        `(via npm) or present as a monorepo sibling (looked for packages/${unscoped}/)? ` +
        `First few candidates: ${candidates.slice(0, 4).join(", ")}`,
    );
}

async function fileExists(p: string): Promise<boolean> {
    try {
        await access(p);
        return true;
    } catch {
        return false;
    }
}

async function locateRefPack(): Promise<{
    packageDir: string;
    refDir: string;
    manifestPath: string;
} | null> {
    // Try the installed-package path first (common case for consumers).
    try {
        const require = createRequire(import.meta.url);
        const manifestPath = require.resolve("@carbide/refs-net10.0/ref-manifest.json");
        const packageDir = path.dirname(manifestPath);
        const refDir = path.join(packageDir, "ref", "net10.0");
        if (await dirExists(refDir)) {
            return { packageDir, refDir, manifestPath };
        }
    } catch {
        // Not installed via node_modules; try the monorepo sibling path.
    }

    // Monorepo fallback: `packages/refs-net10.0/` sits next to `packages/core/`.
    const thisDir = path.dirname(fileURLToPath(import.meta.url));
    const candidates = [
        path.resolve(thisDir, "../../../..", "refs-net10.0"),
        path.resolve(thisDir, "../../../../..", "refs-net10.0"),
        path.resolve(thisDir, "../../../../..", "packages", "refs-net10.0"),
    ];
    for (const candidate of candidates) {
        const refDir = path.join(candidate, "ref", "net10.0");
        const manifestPath = path.join(candidate, "ref-manifest.json");
        if (await dirExists(refDir)) {
            return { packageDir: candidate, refDir, manifestPath };
        }
    }
    return null;
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
