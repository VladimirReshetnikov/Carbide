// TFM (Target Framework Moniker) compatibility matrix. Carbide targets net10.0; this module
// tells the resolver which lib/<tfm>/ folders inside a nupkg it can consume. See M6 §3 M6.2
// and D51 (first-listed TFM wins).

export type TfmFamily = "net" | "netstandard" | "netframework";

export interface Tfm {
    label: string;
    family: TfmFamily;
    /** Integer version. For `net10.0` → 10, `netstandard2.1` → 21, `net472` → 472. */
    version: number;
}

/** Parse a TFM string. Returns null for unrecognised or multi-TFM strings. */
export function parseTfm(raw: string): Tfm | null {
    const s = raw.trim().toLowerCase();
    // net10.0, net9.0, net8.0, net7.0, net6.0, net5.0, netcoreapp3.1 (not supported, returns null)
    let m = /^net(\d+)\.(\d+)$/.exec(s);
    if (m) {
        const major = parseInt(m[1], 10);
        const minor = parseInt(m[2], 10);
        if (!Number.isFinite(major) || !Number.isFinite(minor)) return null;
        // Encode net10.0 as 100, net9.0 as 90, etc. Minor goes in the ones place (rare).
        return { label: `net${major}.${minor}`, family: "net", version: major * 10 + minor };
    }
    m = /^netstandard(\d+)\.(\d+)$/.exec(s);
    if (m) {
        const major = parseInt(m[1], 10);
        const minor = parseInt(m[2], 10);
        if (!Number.isFinite(major) || !Number.isFinite(minor)) return null;
        return { label: `netstandard${major}.${minor}`, family: "netstandard", version: major * 10 + minor };
    }
    // netcoreapp*, net472, net48, etc. — not supported for M6 Carbide target.
    return null;
}

/**
 * Return the list of TFMs (label form) that `target` can consume lib folders from, best match
 * first. For net10.0:
 *   net10.0 → net9.0 → net8.0 → net7.0 → net6.0 → netstandard2.1 → netstandard2.0
 */
export function compatibleLibFolders(target: Tfm): readonly string[] {
    if (target.family === "net") {
        const out: string[] = [];
        // Walk down net major versions.
        for (let v = target.version; v >= 60; v -= 10) {
            const major = Math.floor(v / 10);
            const minor = v % 10;
            out.push(`net${major}.${minor}`);
        }
        // Fall back to netstandard.
        out.push("netstandard2.1", "netstandard2.0");
        return out;
    }
    if (target.family === "netstandard") {
        const out: string[] = [];
        for (let v = target.version; v >= 20; v--) {
            const major = Math.floor(v / 10);
            const minor = v % 10;
            out.push(`netstandard${major}.${minor}`);
        }
        return out;
    }
    return [];
}

/**
 * Pick the best lib/<tfm>/ folder from a listing. Compares case-insensitively; returns the
 * exact folder name from `folders`.
 */
export function pickBestLibFolder(target: Tfm, folders: readonly string[]): string | null {
    const compat = compatibleLibFolders(target);
    const lower = new Map<string, string>();
    for (const f of folders) lower.set(f.toLowerCase(), f);
    for (const candidate of compat) {
        const hit = lower.get(candidate);
        if (hit) return hit;
    }
    return null;
}

/** Extract the lib/<folder> segment from a full nupkg path. Returns null for non-lib paths. */
export function libFolderOf(entry: string): string | null {
    const parts = entry.split("/");
    if (parts.length < 3) return null;
    if (parts[0].toLowerCase() !== "lib") return null;
    return parts[1];
}

/** Group lib-folder listings by folder name. */
export function collectLibFolders(entries: readonly string[]): readonly string[] {
    const out = new Set<string>();
    for (const e of entries) {
        const f = libFolderOf(e);
        if (f) out.add(f);
    }
    return [...out];
}
