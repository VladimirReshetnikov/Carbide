// Shared types for @carbide/nuget.

export type WarningSeverity = "info" | "warning" | "error";

export interface Warning {
    code: string;
    message: string;
    severity: WarningSeverity;
}

export interface PackageReference {
    id: string;
    versionRange: string;
}

export type AllowListMode = "strict" | "advisory" | "off";

export interface ResolveOptions {
    /** TFM the root project is compiling for. Defaults to `net10.0`. */
    targetFramework?: string;
    /** Allow-list policy (§5 D65); default `strict`. */
    allowListMode?: AllowListMode;
    /** Source URL root (flat-container). Default: nuget.org. */
    sourceUrl?: string;
    /** Cache directory override. Default `~/.carbide/nuget-cache/`. */
    cacheDir?: string;
    /** Forbid network access; fail on cache miss. Default false. */
    offline?: boolean;
    /** Pre-populated lock file. When present, replay it verbatim (no resolution, no network). */
    lock?: ResolveLock;
    /** Injectable fetch — for tests. Default: `globalThis.fetch`. */
    fetch?: typeof fetch;
    /** Cache instance override — for tests. Default: filesystem under cacheDir. */
    cache?: import("./cache.js").Cache;
    /** Flat-container client override — for tests. Default: built from sourceUrl + cache. */
    flatContainer?: import("./flat-container.js").FlatContainer;
}

export interface ResolvedReference {
    /** Assembly name (matches the DLL file without extension). */
    name: string;
    /** Raw PE bytes ready to feed to @carbide/core's session.addReference. */
    bytes: Uint8Array;
    /** Originating package. */
    packageId: string;
    packageVersion: string;
}

/**
 * M12 — a Roslyn analyzer asset selected from a package, ready for `session.addAnalyzer`.
 * Distinct from {@link ResolvedReference} because an analyzer is a compile-time tool: it must
 * not become a metadata reference, so the two lists are never interchangeable.
 */
export interface ResolvedAnalyzer {
    /** Assembly name (the DLL file without extension). */
    name: string;
    /** Raw PE bytes ready to feed to @carbide/core's session.addAnalyzer. */
    bytes: Uint8Array;
    /** Originating package. */
    packageId: string;
    packageVersion: string;
    /** The zip entry the bytes came from, for diagnostics that need to name the asset. */
    entry: string;
}

export interface ResolvedPackage {
    id: string;
    version: string;
    /** SHA-256 of the nupkg bytes. */
    sha256: string;
    /** Which caller(s) requested this package (ids of packages at the previous graph level). `<root>` for top-level references. */
    requestedBy: readonly string[];
    /** Direct dependencies (transitively closed elsewhere). */
    dependencies: readonly string[];
    /** Relative path of the lib/<tfm>/ folder picked. Null when nothing compatible is found. */
    libFolder: string | null;
}

export interface ResolveLock {
    schemaVersion: 1;
    generator: "carbide";
    /**
     * Present only in locks written before the file became byte-reproducible. Carbide no
     * longer emits it — a timestamp guarantees a diff on every resolve even when the graph is
     * unchanged — but locks that carry one still parse.
     */
    generatedAt?: string;
    packages: ResolvedPackage[];
    warnings: Warning[];
}

export interface ResolvedGraph {
    packages: ResolvedPackage[];
    references: ResolvedReference[];
    /** M12 — source-generator assets contributed by the resolved packages. */
    analyzers: ResolvedAnalyzer[];
    warnings: Warning[];
    lock: ResolveLock;
}
