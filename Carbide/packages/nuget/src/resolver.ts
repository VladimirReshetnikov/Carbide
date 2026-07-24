// Graph walker with nearest-wins conflict resolution. See M6 §3 M6.7 and D67.

import { getEntry, isAllowed } from "./allowlist.js";
import type { Cache } from "./cache.js";
import { createFlatContainer, type FlatContainer } from "./flat-container.js";
import { openDefaultCache, openCache } from "./cache.js";
import { buildLock } from "./lock.js";
import { readNuspec, type NuspecDependencyGroup } from "./nuspec.js";
import { checkSafety } from "./safety.js";
import {
    collectLibFolders,
    parseTfm,
    pickBestLibFolder,
    type Tfm,
} from "./tfm-compat.js";
import type {
    PackageReference,
    ResolveOptions,
    ResolvedGraph,
    ResolvedPackage,
    ResolvedReference,
    Warning,
} from "./types.js";
import { readEntry } from "./zip.js";
import {
    bestMatch,
    compareVersion,
    parseRange,
    parseVersion,
    VersionParseError,
    type Version,
} from "./version-range.js";
import { MSNUGET_CODES } from "./warnings.js";

/** Resolve a set of top-level PackageReferences into a graph of packages + DLL bytes. */
export async function resolve(
    packages: readonly PackageReference[],
    opts: ResolveOptions = {},
): Promise<ResolvedGraph> {
    const warnings: Warning[] = [];
    const mode = opts.allowListMode ?? "strict";
    const tfmLabel = opts.targetFramework ?? "net10.0";
    const tfm = parseTfm(tfmLabel);
    if (!tfm) {
        throw new Error(`Target framework '${tfmLabel}' is not supported for NuGet resolution.`);
    }

    const cache = opts.cache ?? (opts.cacheDir ? openCache(opts.cacheDir) : openDefaultCache());
    const flatContainer =
        opts.flatContainer ??
        createFlatContainer({
            sourceUrl: opts.sourceUrl,
            cache,
            fetch: opts.fetch ?? globalThis.fetch,
            offline: opts.offline,
        });

    // Lock-file replay short-circuits the graph walk entirely.
    if (opts.lock) {
        // Review R1 M1 / R2 §8: the allow-list and safety checks used to be bypassed
        // when resolve replayed a lock file, which meant a lock generated under one
        // policy could carry a disallowed / unsafe package into a stricter session
        // and still land untouched (only the content hash was verified). Thread the
        // same gates through replay — content hashes prove identity, not policy.
        return replayLock(opts.lock, tfm, cache, flatContainer, mode, warnings);
    }

    // Fresh graph walk.
    const resolved = new Map<string, ResolvedEntry>(); // key = id lowercase
    const queue: QueueEntry[] = packages.map((p) => ({ ref: p, depth: 0, requestedBy: "<root>" }));

    while (queue.length > 0) {
        const next = queue.shift()!;
        const idKey = next.ref.id.toLowerCase();

        // Allow-list gate (first encounter only).
        if (!resolved.has(idKey)) {
            const allowResult = applyAllowList(next.ref.id, mode, warnings);
            if (allowResult === "refused") {
                throw new AllowListRefusedError(next.ref.id);
            }
        }

        // Pick the concrete version to resolve this reference to.
        const version = await pickVersion(next.ref, flatContainer);
        const existing = resolved.get(idKey);
        if (existing) {
            // Nearest-wins: keep the shallower-depth entry.
            if (next.depth < existing.depth) {
                warnings.push({
                    code: MSNUGET_CODES.NEAREST_WINS_TIE,
                    message: `Package '${existing.package.id}' resolved at depth ${existing.depth} (${existing.package.version}); replaced by depth ${next.depth} (${version.raw}).`,
                    severity: "info",
                });
                resolved.delete(idKey);
            } else if (next.depth === existing.depth && existing.package.version !== version.raw) {
                // Same depth, different version — semantically higher wins. Falls back to
                // lexicographic compare only when one side fails to parse (rare; malformed
                // pre-release labels).
                let keepNewer: boolean;
                try {
                    const existingParsed = parseVersion(existing.package.version);
                    keepNewer = compareVersion(version, existingParsed) > 0;
                } catch {
                    keepNewer = version.raw.localeCompare(existing.package.version) > 0;
                }
                if (keepNewer) {
                    warnings.push({
                        code: MSNUGET_CODES.NEAREST_WINS_TIE,
                        message: `Same-depth tie for '${existing.package.id}': ${existing.package.version} vs ${version.raw}; keeping ${version.raw}.`,
                        severity: "warning",
                    });
                    resolved.delete(idKey);
                } else {
                    // Keep the existing entry.
                    appendRequestedBy(existing, next.requestedBy);
                    continue;
                }
            } else {
                appendRequestedBy(existing, next.requestedBy);
                continue;
            }
        }

        // Fetch the nupkg bytes.
        const { bytes, sha256 } = await flatContainer.downloadNupkg(next.ref.id, version.raw);
        const nuspec = await readNuspec(bytes);
        const entryNames = nuspec.entries.map((e) => e.name);

        // Safety gate.
        const safetyResult = checkSafety(nuspec.id, nuspec.version, entryNames);
        if (safetyResult.kind === "refused") {
            throw new SafetyRefusalError(safetyResult.code, safetyResult.message);
        }

        // Pick the lib folder.
        const libFolders = collectLibFolders(entryNames);
        const picked = pickBestLibFolder(tfm, libFolders);
        const libDlls = picked
            ? entryNames.filter((e) => e.toLowerCase().startsWith(`lib/${picked.toLowerCase()}/`) && e.toLowerCase().endsWith(".dll"))
            : [];

        const pkgEntry: ResolvedEntry = {
            depth: next.depth,
            bytes,
            nuspec,
            sha256,
            libFolder: picked,
            libDllEntries: libDlls,
            package: {
                id: nuspec.id,
                version: nuspec.version,
                sha256,
                requestedBy: [next.requestedBy],
                dependencies: nuspec.dependencies.map((d) => d.id),
                libFolder: picked,
            },
        };
        resolved.set(idKey, pkgEntry);

        // Enqueue dependencies. Pick the best-matching <group> (including empty ones),
        // then enqueue each direct dep. Empty groups mean "supported TFM, zero deps"
        // — they must be preferred over distant non-empty groups.
        const depsForTfm = selectDependenciesForTfm(nuspec.dependencyGroups, tfm);
        for (const dep of depsForTfm) {
            queue.push({
                ref: { id: dep.id, versionRange: dep.versionRange },
                depth: next.depth + 1,
                requestedBy: nuspec.id,
            });
        }
    }

    // Materialise ResolvedReference list — read each package's lib DLL bytes once.
    const references: ResolvedReference[] = [];
    for (const entry of resolved.values()) {
        for (const dllName of entry.libDllEntries) {
            const zipEntry = entry.nuspec.entries.find((e) => e.name === dllName);
            if (!zipEntry) continue;
            const dllBytes = await readEntry(entry.bytes, zipEntry);
            const name = dllName.substring(dllName.lastIndexOf("/") + 1).replace(/\.dll$/i, "");
            references.push({
                name,
                bytes: dllBytes,
                packageId: entry.nuspec.id,
                packageVersion: entry.nuspec.version,
            });
        }
    }

    const packagesOut = [...resolved.values()].map((e) => e.package);
    const lock = buildLock(packagesOut, warnings);

    return { packages: packagesOut, references, warnings, lock };
}

export class AllowListRefusedError extends Error {
    constructor(public readonly packageId: string) {
        super(
            `Package '${packageId}' is not in Carbide's allow-list (MSNUGET021). ` +
                `Pass --allow-list-mode advisory to warn-and-continue or off to disable entirely.`,
        );
        this.name = "AllowListRefusedError";
    }
}

export class SafetyRefusalError extends Error {
    constructor(public readonly code: string, message: string) {
        super(message);
        this.name = "SafetyRefusalError";
    }
}

interface QueueEntry {
    ref: PackageReference;
    depth: number;
    requestedBy: string;
}

interface ResolvedEntry {
    depth: number;
    bytes: Uint8Array;
    nuspec: Awaited<ReturnType<typeof readNuspec>>;
    sha256: string;
    libFolder: string | null;
    libDllEntries: string[];
    package: ResolvedPackage;
}

function applyAllowList(id: string, mode: ResolveOptions["allowListMode"], warnings: Warning[]): "ok" | "refused" {
    if (mode === "off") return "ok";
    if (isAllowed(id)) return "ok";
    if (mode === "advisory" || (mode as string | undefined) === undefined) {
        // Advisory: warn but allow.
        if (mode === "advisory") {
            const entry = getEntry(id);
            warnings.push({
                code: MSNUGET_CODES.ALLOWLIST_ADVISORY,
                message: `Package '${id}' is not in Carbide's allow-list (advisory mode). ${
                    entry ? `See ${entry.source}.` : ""
                }`.trim(),
                severity: "warning",
            });
            return "ok";
        }
    }
    // strict (default) and anything else → refused.
    return "refused";
}

async function pickVersion(ref: PackageReference, flatContainer: FlatContainer): Promise<Version> {
    let range;
    try {
        range = parseRange(ref.versionRange);
    } catch (err) {
        if (err instanceof VersionParseError) throw err;
        throw new VersionParseError(
            `Cannot parse version range '${ref.versionRange}' for package '${ref.id}': ${(err as Error).message}`,
        );
    }
    const versionsRaw = await flatContainer.listVersions(ref.id);
    const versions: Version[] = [];
    for (const raw of versionsRaw) {
        try {
            versions.push(parseVersion(raw));
        } catch {
            // Ignore unparseable nupkg labels (rare; usually malformed pre-release tags).
        }
    }
    if (versions.length === 0) {
        throw new Error(`No versions returned for package '${ref.id}'.`);
    }
    // If range has no explicit bounds (bare version), `parseRange` gives lower-inclusive =
    // the bare value, open upper. That matches the "≥ X" NuGet convention. We still prefer
    // the lowest version that matches (NuGet default).
    const match = bestMatch(range, versions);
    if (!match) {
        const listTail = versions
            .slice(-5)
            .map((v) => v.raw)
            .join(", ");
        throw new Error(
            `No version of '${ref.id}' satisfies '${ref.versionRange}'. Latest available: ${listTail}.`,
        );
    }
    return match;
}

function selectDependenciesForTfm(
    groups: readonly NuspecDependencyGroup[],
    tfm: Tfm,
): readonly { id: string; versionRange: string }[] {
    // Score each <group> (including empty ones) against the target TFM.
    let best: { group: NuspecDependencyGroup; score: number } | null = null;
    for (const group of groups) {
        const score = tfmMatchScore(group.targetFramework, tfm);
        if (score === null) continue;
        if (!best || score > best.score) {
            best = { group, score };
        }
    }
    if (best) return best.group.dependencies;
    // No compatible group — flatten all direct deps so we at least try something.
    return groups.flatMap((g) => g.dependencies);
}

function tfmMatchScore(label: string | null, target: Tfm): number | null {
    if (label === null) return 0; // untyped group: usable but lowest priority.
    const parsed = parseTfm(label);
    if (!parsed) return null;
    if (parsed.family !== target.family && !(parsed.family === "netstandard" && target.family === "net")) {
        return null;
    }
    if (parsed.version > target.version) return null;
    // Higher score = closer to target. Subtract the gap.
    return 1000 - (target.version - parsed.version);
}

function appendRequestedBy(entry: ResolvedEntry, requestedBy: string): void {
    if (!entry.package.requestedBy.includes(requestedBy)) {
        entry.package.requestedBy = [...entry.package.requestedBy, requestedBy];
    }
}

async function replayLock(
    lock: ResolveOptions["lock"] & object,
    tfm: Tfm,
    cache: Cache,
    flatContainer: FlatContainer,
    mode: Exclude<ResolveOptions["allowListMode"], undefined>,
    warnings: Warning[],
): Promise<ResolvedGraph> {
    const packages: ResolvedPackage[] = [];
    const references: ResolvedReference[] = [];

    for (const p of lock.packages) {
        // Review R1 M1 / R2 §8 — policy gates also apply to lock replay. A stale/malicious
        // lock can only carry a package as far as the current policy permits. Content
        // hashes are checked below; they prove byte identity, not policy compliance.
        if (applyAllowList(p.id, mode, warnings) === "refused") {
            throw new AllowListRefusedError(p.id);
        }

        const { bytes, sha256 } = await flatContainer.downloadNupkg(p.id, p.version);
        if (sha256 !== p.sha256) {
            throw new Error(
                `Integrity mismatch for '${p.id}@${p.version}' (${MSNUGET_CODES.INTEGRITY_MISMATCH}): expected sha256 ${p.sha256}, got ${sha256}.`,
            );
        }
        const nuspec = await readNuspec(bytes);
        const entryNames = nuspec.entries.map((e) => e.name);
        const safetyResult = checkSafety(nuspec.id, nuspec.version, entryNames);
        if (safetyResult.kind === "refused") {
            throw new SafetyRefusalError(safetyResult.code, safetyResult.message);
        }
        const libFolders = collectLibFolders(entryNames);
        const picked = pickBestLibFolder(tfm, libFolders);
        packages.push({ ...p, libFolder: picked });

        if (picked) {
            const libDlls = entryNames.filter(
                (e) => e.toLowerCase().startsWith(`lib/${picked.toLowerCase()}/`) && e.toLowerCase().endsWith(".dll"),
            );
            for (const dllName of libDlls) {
                const zipEntry = nuspec.entries.find((e) => e.name === dllName);
                if (!zipEntry) continue;
                const dllBytes = await readEntry(bytes, zipEntry);
                const name = dllName.substring(dllName.lastIndexOf("/") + 1).replace(/\.dll$/i, "");
                references.push({
                    name,
                    bytes: dllBytes,
                    packageId: p.id,
                    packageVersion: p.version,
                });
            }
        }
    }

    warnings.push(...lock.warnings);
    return { packages, references, warnings, lock };
}
