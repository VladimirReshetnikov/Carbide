// Compile-item expansion: default include of `.cs` files under the project directory, plus
// explicit <Compile Include="…"/> / <Compile Remove="…"/> glob operations.
//
// Matches cs_kit._discover_sources + _expand_compile_glob semantics. Paths are normalised to
// forward slashes before matching so Windows-separator patterns work on all hosts (D51, D52).

import { readdir, stat } from "node:fs/promises";
import path from "node:path";

const EXCLUDED_DIR_NAMES = new Set(["bin", "obj", ".git", ".svn", ".hg"]);

/** Walk `projectDir`, return absolute paths of `.cs` files, excluding obj/bin/.*. Sorted. */
export async function discoverCsFiles(projectDir: string): Promise<string[]> {
    const out: string[] = [];
    const absRoot = path.resolve(projectDir);
    await walk(absRoot, out);
    out.sort();
    return out;
}

async function walk(dir: string, out: string[]): Promise<void> {
    let entries;
    try {
        entries = await readdir(dir, { withFileTypes: true });
    } catch {
        return;
    }
    for (const entry of entries) {
        if (entry.isDirectory()) {
            if (EXCLUDED_DIR_NAMES.has(entry.name) || entry.name.startsWith(".")) continue;
            await walk(path.join(dir, entry.name), out);
        } else if (entry.isFile() && entry.name.toLowerCase().endsWith(".cs")) {
            out.push(path.resolve(path.join(dir, entry.name)));
        }
    }
}

/** Normalise backslashes to forward slashes — MSBuild patterns often use backslashes. */
export function normaliseSlashes(input: string): string {
    return input.replace(/\\/g, "/");
}

/**
 * Expand a single MSBuild glob pattern against `projectDir`. Returns sorted absolute paths
 * of matching `.cs` files.
 *   - supports `**` (any number of directory levels including zero)
 *   - supports `*`  (any chars except `/`)
 *   - supports `?`  (exactly one char except `/`)
 *   - literal segments match case-sensitively
 *
 * Case sensitivity is deliberate and uniform across hosts. MSBuild inherits the filesystem's
 * behaviour, so the same `.csproj` resolves differently on Windows and Linux; Carbide prefers
 * a single answer everywhere, matching how it treats document paths as exact identities
 * elsewhere.
 *
 * The pattern's wildcard-free prefix is resolved against the project directory and becomes
 * the walk root, so a pattern may point outside the project — `..\Shared\*.cs`, the usual
 * shared-source idiom, is common and used to match nothing at all. Deriving the root from the
 * prefix also keeps the walk tight: `../Shared/*.cs` visits `../Shared` and nothing else.
 */
export async function expandGlob(projectDir: string, pattern: string): Promise<string[]> {
    const normalisedPattern = normaliseSlashes(pattern.trim());
    const projectAbs = path.resolve(projectDir);

    const segments = normalisedPattern.split("/");
    const wildcardIndex = segments.findIndex((s) => s.includes("*") || s.includes("?"));
    // With no wildcard at all the last segment is the filename, and everything before it is
    // the base. Otherwise the base is everything up to the first segment carrying a wildcard.
    const baseSegments = wildcardIndex < 0 ? segments.slice(0, -1) : segments.slice(0, wildcardIndex);
    const remainder = (wildcardIndex < 0 ? segments.slice(-1) : segments.slice(wildcardIndex)).join("/");
    const baseAbs = await resolveBaseDir(projectAbs, baseSegments);
    if (baseAbs === null) {
        return [];
    }

    // Walk the base once; the matcher is cheap against each candidate.
    const all = await collectAllFiles(baseAbs);
    const re = globToRegex(remainder);

    const matches = new Set<string>();
    for (const abs of all) {
        if (!abs.toLowerCase().endsWith(".cs")) continue;
        const rel = normaliseSlashes(path.relative(baseAbs, abs));
        if (re.test(rel)) {
            matches.add(abs);
        }
    }
    return [...matches].sort();
}

/**
 * Resolve a pattern's wildcard-free prefix to an absolute directory, or `null` when it does
 * not exist.
 *
 * Named segments are matched against real directory entries **case-sensitively**, rather than
 * handed to `path.resolve` and left to the filesystem. On Windows the filesystem would accept
 * `sub/` for a directory named `Sub/`, so the prefix would match case-insensitively while the
 * wildcard part of the same pattern matched case-sensitively — one pattern, two rules, and a
 * different answer than on Linux. Navigation segments (`.`, `..`) are applied as written.
 */
async function resolveBaseDir(projectAbs: string, baseSegments: readonly string[]): Promise<string | null> {
    let current = projectAbs;
    for (const segment of baseSegments) {
        if (segment === "" || segment === ".") {
            continue;
        }
        if (segment === "..") {
            current = path.dirname(current);
            continue;
        }
        let entries;
        try {
            entries = await readdir(current, { withFileTypes: true });
        } catch {
            return null;
        }
        if (!entries.some((entry) => entry.isDirectory() && entry.name === segment)) {
            return null;
        }
        current = path.join(current, segment);
    }
    return current;
}

async function collectAllFiles(dir: string): Promise<string[]> {
    const out: string[] = [];
    async function inner(d: string): Promise<void> {
        let entries;
        try {
            entries = await readdir(d, { withFileTypes: true });
        } catch {
            return;
        }
        for (const entry of entries) {
            const full = path.join(d, entry.name);
            if (entry.isDirectory()) {
                if (EXCLUDED_DIR_NAMES.has(entry.name) || entry.name.startsWith(".")) continue;
                await inner(full);
            } else if (entry.isFile()) {
                out.push(path.resolve(full));
            }
        }
    }
    await inner(dir);
    return out;
}

/**
 * Convert an MSBuild glob pattern (with `**`, `*`, `?`) to a RegExp matching the full relative
 * path (forward-slash normalised). Literal characters are escaped; `**` matches any number of
 * directory segments (possibly zero); `*` matches any sequence except `/`; `?` matches exactly
 * one character except `/`.
 *
 * `?` used to be escaped into a literal question mark, which on Windows can never match a real
 * filename — so `Include="File?.cs"` silently contributed nothing.
 */
export function globToRegex(pattern: string): RegExp {
    let re = "^";
    let i = 0;
    while (i < pattern.length) {
        const ch = pattern[i];
        if (ch === "*") {
            // Peek for `**`.
            if (pattern[i + 1] === "*") {
                // `**/` → any number of segments (possibly zero). Consume the following `/`
                // as part of the pattern so `**` at the start also matches zero segments.
                if (pattern[i + 2] === "/") {
                    re += "(?:.*/)?";
                    i += 3;
                } else {
                    re += ".*";
                    i += 2;
                }
            } else {
                re += "[^/]*";
                i++;
            }
        } else if (ch === "?") {
            re += "[^/]";
            i++;
        } else if ("\\^$.|()[]{}+".includes(ch)) {
            re += "\\" + ch;
            i++;
        } else {
            re += ch;
            i++;
        }
    }
    re += "$";
    return new RegExp(re);
}

/**
 * Apply the sequence of compile-item operations over the discovered default set. Returns the
 * final included set (absolute paths, sorted) plus per-operation provenance for the trace.
 */
export async function resolveCompileItems(
    projectDir: string,
    enableDefaultInclude: boolean,
    operations: ReadonlyArray<{ operation: "include" | "remove"; pattern: string }>,
): Promise<{
    sources: string[];
    operationMatches: Array<{ operation: "include" | "remove"; pattern: string; matchCount: number }>;
    resolved: Array<{
        file: string;
        included: boolean;
        provenance: Array<{ operation: string; pattern: string; applied: boolean }>;
    }>;
}> {
    const discovered = new Set(await discoverCsFiles(projectDir));
    const included = new Map<string, boolean>();
    const provenance = new Map<string, Array<{ operation: string; pattern: string; applied: boolean }>>();

    if (enableDefaultInclude) {
        for (const file of discovered) {
            included.set(file, true);
            const entry = provenance.get(file) ?? [];
            entry.push({ operation: "default-include", pattern: "**/*.cs", applied: true });
            provenance.set(file, entry);
        }
    }

    const operationMatches: Array<{
        operation: "include" | "remove";
        pattern: string;
        matchCount: number;
    }> = [];

    for (const op of operations) {
        const matches = await expandGlob(projectDir, op.pattern);
        operationMatches.push({ operation: op.operation, pattern: op.pattern, matchCount: matches.length });
        for (const file of matches) {
            included.set(file, op.operation === "include");
            const entry = provenance.get(file) ?? [];
            entry.push({ operation: op.operation, pattern: op.pattern, applied: true });
            provenance.set(file, entry);
        }
    }

    const sources = [...included.entries()]
        .filter(([, on]) => on)
        .map(([file]) => file)
        .sort();

    const resolved = [...provenance.entries()]
        .sort(([a], [b]) => a.localeCompare(b))
        .map(([file, prov]) => ({ file, included: included.get(file) ?? false, provenance: prov }));

    return { sources, operationMatches, resolved };
}

export { stat as statForTests };
