// Read a nupkg's nuspec file: <id>, <version>, <dependencies>, and the full file listing.
// Uses the same minimal XML walker @carbide/msbuild-lite ships (reached via its exported
// types is inconvenient; we keep a small private parser here — nuspec XML is even simpler
// than .csproj).

import { listEntries, readEntry, ZipParseError, type ZipEntry } from "./zip.js";
import type { PackageReference } from "./types.js";

export interface NuspecDependency {
    id: string;
    versionRange: string;
    /** The `targetFramework` attribute of the enclosing `<group>`, if any. */
    targetFramework: string | null;
}

export interface NuspecDependencyGroup {
    /** Null for the flat `<dependencies><dependency/></dependencies>` form. */
    targetFramework: string | null;
    dependencies: { id: string; versionRange: string }[];
}

export interface NuspecInfo {
    id: string;
    version: string;
    /** All files listed in the nupkg, lowercased-path for matching. */
    entries: readonly ZipEntry[];
    /** Dependencies flattened from all <group>s and the flat form. */
    dependencies: NuspecDependency[];
    /**
     * All dependency groups as declared, INCLUDING empty ones. An empty group at
     * `targetFramework="net6.0"` conveys "this package supports net6.0 with zero
     * transitive deps" — a signal the resolver must see, because falling back
     * to a non-empty netstandard1.x group would drag in unwanted packages.
     */
    dependencyGroups: readonly NuspecDependencyGroup[];
}

/** Read a nupkg and return its metadata. */
export async function readNuspec(nupkg: Uint8Array): Promise<NuspecInfo> {
    const entries = listEntries(nupkg);
    const nuspecEntry = entries.find((e) => e.name.toLowerCase().endsWith(".nuspec") && !e.name.includes("/"));
    if (!nuspecEntry) {
        throw new ZipParseError("No .nuspec file at nupkg root.");
    }
    const xml = new TextDecoder("utf-8").decode(await readEntry(nupkg, nuspecEntry));
    const { id, version, dependencies, dependencyGroups } = parseNuspecXml(xml);
    return { id, version, entries, dependencies, dependencyGroups };
}

/** Convert NuspecDependency entries into PackageReferences for the resolver. */
export function nuspecDepsToPackageRefs(deps: readonly NuspecDependency[]): PackageReference[] {
    const seen = new Map<string, string>();
    for (const d of deps) {
        const existing = seen.get(d.id);
        if (!existing) {
            seen.set(d.id, d.versionRange);
        }
    }
    return [...seen.entries()].map(([id, versionRange]) => ({ id, versionRange }));
}

/** Parse the nuspec XML. Hand-rolled because nuspec uses deep namespaces we don't care about. */
function parseNuspecXml(xml: string): {
    id: string;
    version: string;
    dependencies: NuspecDependency[];
    dependencyGroups: NuspecDependencyGroup[];
} {
    // Strip BOM.
    let src = xml.charCodeAt(0) === 0xfeff ? xml.slice(1) : xml;

    const id = extractElementText(src, "id");
    const version = extractElementText(src, "version");
    if (!id || !version) {
        throw new Error(`Malformed nuspec: missing <id> or <version>.`);
    }
    const dependencies: NuspecDependency[] = [];
    const dependencyGroups: NuspecDependencyGroup[] = [];
    for (const group of findAll(src, "group")) {
        const tfm = attr(group.openTag, "targetFramework");
        const groupDeps: { id: string; versionRange: string }[] = [];
        for (const dep of findAll(group.inner, "dependency")) {
            const depId = attr(dep.openTag, "id");
            const depVersion = attr(dep.openTag, "version");
            if (depId) {
                dependencies.push({ id: depId, versionRange: depVersion ?? "", targetFramework: tfm });
                groupDeps.push({ id: depId, versionRange: depVersion ?? "" });
            }
        }
        // Empty groups are preserved — they mean "this TFM is supported with zero deps."
        dependencyGroups.push({ targetFramework: tfm, dependencies: groupDeps });
    }
    // Flat <dependencies><dependency .../></dependencies> form.
    for (const depsNode of findAll(src, "dependencies")) {
        // Skip blocks that use the grouped form — groups were already handled above.
        // (NuGet's schema is effectively either/or; mixing is not observed in practice.)
        if (depsNode.inner.includes("<group")) continue;
        if (!depsNode.inner.includes("<dependency")) continue;
        const flatDeps: { id: string; versionRange: string }[] = [];
        for (const dep of findAll(depsNode.inner, "dependency")) {
            const depId = attr(dep.openTag, "id");
            const depVersion = attr(dep.openTag, "version");
            if (depId) {
                dependencies.push({ id: depId, versionRange: depVersion ?? "", targetFramework: null });
                flatDeps.push({ id: depId, versionRange: depVersion ?? "" });
            }
        }
        if (flatDeps.length > 0) {
            dependencyGroups.push({ targetFramework: null, dependencies: flatDeps });
        }
    }
    return { id, version, dependencies, dependencyGroups };
}

function extractElementText(xml: string, name: string): string | null {
    const re = new RegExp(`<${name}\\b[^>]*>([\\s\\S]*?)</${name}>`);
    const m = re.exec(xml);
    if (!m) return null;
    return m[1].trim();
}

interface Found {
    openTag: string;
    inner: string;
}

/**
 * Find all occurrences of <name ...>...</name> or <name .../>, in document order.
 * A single regex handles both forms so self-closing tags don't get re-matched as
 * spurious open/close pairs that gobble up sibling content.
 */
function findAll(xml: string, name: string): Found[] {
    const out: Found[] = [];
    // Alternative 1: self-closing — capture attrs, then `/>`.
    // Alternative 2: open/close — capture attrs, then `>`, then (lazy) inner, then `</name>`.
    const re = new RegExp(
        `<${name}\\b([^>]*?)(?:(/)>|>([\\s\\S]*?)</${name}>)`,
        "g",
    );
    for (let m = re.exec(xml); m !== null; m = re.exec(xml)) {
        const attrs = m[1];
        const isSelfClosing = m[2] === "/";
        if (isSelfClosing) {
            out.push({ openTag: `<${name}${attrs}/>`, inner: "" });
        } else {
            out.push({ openTag: `<${name}${attrs}>`, inner: m[3] ?? "" });
        }
    }
    return out;
}

function attr(openTag: string, name: string): string | null {
    const re = new RegExp(`\\b${name}\\s*=\\s*(?:"([^"]*)"|'([^']*)')`);
    const m = re.exec(openTag);
    if (!m) return null;
    return (m[1] ?? m[2]) ?? null;
}
