// NuGet v3 flat-container client. Wraps the two endpoints the resolver needs:
//   - GET {source}/{id-lower}/index.json         → { versions: string[] }
//   - GET {source}/{id-lower}/{version}/{id-lower}.{version}.nupkg
// with the local filesystem cache.

import { MSNUGET_CODES } from "./warnings.js";
import type { Cache } from "./cache.js";

export const DEFAULT_SOURCE_URL = "https://api.nuget.org/v3-flatcontainer/";

export interface FlatContainer {
    readonly sourceUrl: string;
    listVersions(id: string): Promise<string[]>;
    /** Returns bytes + whether they came from cache. */
    downloadNupkg(id: string, version: string): Promise<{ bytes: Uint8Array; fromCache: boolean; sha256: string }>;
}

export interface FlatContainerOptions {
    sourceUrl?: string;
    cache: Cache;
    fetch?: typeof fetch;
    offline?: boolean;
}

export class OfflineCacheMissError extends Error {
    constructor(public readonly id: string, public readonly version: string | null) {
        const desc = version ? `${id}@${version}` : id;
        super(`Cache miss for '${desc}' under --offline mode (${MSNUGET_CODES.CACHE_MISS_OFFLINE}).`);
        this.name = "OfflineCacheMissError";
    }
}

export function createFlatContainer(opts: FlatContainerOptions): FlatContainer {
    const sourceUrl = normaliseSourceUrl(opts.sourceUrl ?? DEFAULT_SOURCE_URL);
    const fetchImpl = opts.fetch ?? globalThis.fetch;
    const offline = !!opts.offline;
    const cache = opts.cache;

    return {
        sourceUrl,
        async listVersions(id: string): Promise<string[]> {
            // Try cache first: if the id has any cached versions, that's a good starting set.
            // The live endpoint is the source of truth — offline mode returns cache only.
            if (offline) {
                return cache.listCachedVersions(id);
            }
            if (!fetchImpl) {
                throw new Error("No fetch implementation available; pass one via ResolveOptions.fetch.");
            }
            const url = `${sourceUrl}${id.toLowerCase()}/index.json`;
            const resp = await fetchImpl(url);
            if (!resp.ok) {
                throw new Error(`Flat-container listVersions for '${id}' failed: HTTP ${resp.status} at ${url}`);
            }
            const payload = (await resp.json()) as { versions?: string[] };
            return payload.versions ?? [];
        },

        async downloadNupkg(id: string, version: string) {
            const cached = await cache.getNupkg(id, version);
            if (cached) {
                return { bytes: cached.bytes, fromCache: true, sha256: cached.meta.sha256 };
            }
            if (offline) {
                throw new OfflineCacheMissError(id, version);
            }
            if (!fetchImpl) {
                throw new Error("No fetch implementation available; pass one via ResolveOptions.fetch.");
            }
            const lower = id.toLowerCase();
            const url = `${sourceUrl}${lower}/${version}/${lower}.${version}.nupkg`;
            const resp = await fetchImpl(url);
            if (!resp.ok) {
                throw new Error(`Flat-container downloadNupkg for '${id}@${version}' failed: HTTP ${resp.status} at ${url}`);
            }
            const bytes = new Uint8Array(await resp.arrayBuffer());
            const { sha256 } = await cache.putNupkg(id, version, bytes, url);
            return { bytes, fromCache: false, sha256 };
        },
    };
}

function normaliseSourceUrl(url: string): string {
    return url.endsWith("/") ? url : `${url}/`;
}
