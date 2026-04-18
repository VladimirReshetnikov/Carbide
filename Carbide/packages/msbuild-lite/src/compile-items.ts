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
 * of matching `.cs` files. Matches cs_kit._expand_compile_glob:
 *   - supports `**` (any number of directory levels including zero)
 *   - supports `*`  (any chars except `/`)
 *   - literal segments match case-sensitively
 */
export async function expandGlob(projectDir: string, pattern: string): Promise<string[]> {
    const normalisedPattern = normaliseSlashes(pattern.trim());
    const projectAbs = path.resolve(projectDir);

    // Walk the whole tree once; the matcher is cheap against each candidate.
    const all = await collectAllFiles(projectAbs);
    const re = globToRegex(normalisedPattern);

    const matches = new Set<string>();
    for (const abs of all) {
        if (!abs.toLowerCase().endsWith(".cs")) continue;
        const rel = normaliseSlashes(path.relative(projectAbs, abs));
        if (re.test(rel)) {
            matches.add(abs);
        }
    }
    return [...matches].sort();
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
 * Convert an MSBuild glob pattern (with `**`, `*`) to a RegExp matching the full relative
 * path (forward-slash normalised). Literal characters are escaped; `**` matches any number of
 * directory segments (possibly zero); `*` matches any sequence except `/`.
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
        } else if ("\\^$.|?()[]{}+".includes(ch)) {
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
