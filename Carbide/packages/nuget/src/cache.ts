// Filesystem cache for nupkg bytes. Scheme:
//   <root>/<id-lower>/<version>/
//     <id-lower>.<version>.nupkg
//     .carbide-meta.json   — { sha256, fetchedAt, sourceUrl }
//
// SHA-256 is recorded at first fetch and verified on every subsequent read (M6 D72).

import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile, readdir, access } from "node:fs/promises";
import { constants as fsConstants } from "node:fs";
import path from "node:path";
import os from "node:os";

export interface CacheMeta {
    sha256: string;
    fetchedAt: string;
    sourceUrl: string;
}

export interface Cache {
    readonly rootDir: string;
    getNupkg(id: string, version: string): Promise<{ bytes: Uint8Array; meta: CacheMeta } | null>;
    putNupkg(
        id: string,
        version: string,
        bytes: Uint8Array,
        sourceUrl: string,
    ): Promise<{ sha256: string; path: string }>;
    listCachedVersions(id: string): Promise<string[]>;
}

/** Default cache location: ~/.carbide/nuget-cache/. Respects the CARBIDE_NUGET_CACHE_DIR env var. */
export function defaultCacheDir(): string {
    const env = process.env.CARBIDE_NUGET_CACHE_DIR;
    if (env && env.length > 0) return env;
    return path.join(os.homedir(), ".carbide", "nuget-cache");
}

export function openDefaultCache(): Cache {
    return openCache(defaultCacheDir());
}

export function openCache(rootDir: string): Cache {
    return new FileSystemCache(rootDir);
}

class FileSystemCache implements Cache {
    constructor(public readonly rootDir: string) {}

    async getNupkg(id: string, version: string): Promise<{ bytes: Uint8Array; meta: CacheMeta } | null> {
        const dir = this.packageDir(id, version);
        const nupkgPath = path.join(dir, this.nupkgFileName(id, version));
        const metaPath = path.join(dir, ".carbide-meta.json");
        try {
            await access(nupkgPath, fsConstants.R_OK);
        } catch {
            return null;
        }
        let bytes: Uint8Array;
        let meta: CacheMeta;
        try {
            bytes = new Uint8Array(await readFile(nupkgPath));
        } catch {
            return null;
        }
        try {
            meta = JSON.parse(await readFile(metaPath, "utf8")) as CacheMeta;
        } catch {
            // Missing/corrupt meta → treat the cache entry as absent so we re-fetch.
            return null;
        }
        const actual = sha256Hex(bytes);
        if (actual !== meta.sha256) {
            // Integrity mismatch. Treat as cache miss; caller decides whether to re-fetch.
            return null;
        }
        return { bytes, meta };
    }

    async putNupkg(
        id: string,
        version: string,
        bytes: Uint8Array,
        sourceUrl: string,
    ): Promise<{ sha256: string; path: string }> {
        const dir = this.packageDir(id, version);
        await mkdir(dir, { recursive: true });
        const sha = sha256Hex(bytes);
        const nupkgPath = path.join(dir, this.nupkgFileName(id, version));
        const metaPath = path.join(dir, ".carbide-meta.json");
        await writeFile(nupkgPath, bytes);
        const meta: CacheMeta = { sha256: sha, fetchedAt: new Date().toISOString(), sourceUrl };
        await writeFile(metaPath, JSON.stringify(meta, null, 2) + "\n");
        return { sha256: sha, path: nupkgPath };
    }

    async listCachedVersions(id: string): Promise<string[]> {
        const dir = path.join(this.rootDir, id.toLowerCase());
        try {
            const entries = await readdir(dir, { withFileTypes: true });
            return entries.filter((e) => e.isDirectory()).map((e) => e.name);
        } catch {
            return [];
        }
    }

    private packageDir(id: string, version: string): string {
        return path.join(this.rootDir, id.toLowerCase(), version);
    }
    private nupkgFileName(id: string, version: string): string {
        return `${id.toLowerCase()}.${version}.nupkg`;
    }
}

export function sha256Hex(bytes: Uint8Array): string {
    const h = createHash("sha256");
    h.update(bytes);
    return h.digest("hex");
}
