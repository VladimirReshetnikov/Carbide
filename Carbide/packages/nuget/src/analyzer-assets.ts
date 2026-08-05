// Selects the Roslyn analyzer assets a package contributes, following NuGet's
// `analyzers/dotnet/[roslyn<X.Y>/][<lang>/]` convention.
//
// Carbide runs source generators (see @carbide/core's addAnalyzer), so these assets are
// consumed rather than refused. What is *not* consumed still has to be visible: a package
// whose generator silently fails to apply shows up as a compile error about a type that was
// supposed to be generated, a long way from the actual cause.

import { compareOrdinal } from "./ordinal.js";

/** An analyzer asset selected from a package, as a zip entry path. */
export interface AnalyzerAssetSelection {
    /** Entry paths to extract and hand to the generator host, in ordinal order. */
    entries: string[];
    /**
     * Entries under `analyzers/` that Carbide did not recognise well enough to select from.
     * Reported as MSNUGET017 so a generator that never ran is never silent.
     */
    unrecognised: string[];
}

/**
 * The Roslyn version Carbide's compiler is built against. Analyzer assets are published per
 * Roslyn version because the analyzer API surface moves; a package's `roslyn4.8/` folder must
 * not be loaded by a 5.3 host if the package also ships a folder matching what we have.
 *
 * Kept here rather than imported from `@carbide/core` because this package must stay free of
 * a dependency on the runtime half — but the two have to agree, so
 * `test/unit/roslyn-version-pin.test.mjs` reads the version straight out of
 * `Carbide.Core.csproj` and fails if they drift.
 */
export const CARBIDE_ROSLYN_VERSION = { major: 5, minor: 3 } as const;

/** Language sub-folders whose assets apply to a C# compilation. */
const CSHARP_LANGUAGE_FOLDERS = new Set(["cs", "csharp"]);

/** Language sub-folders that exist and are simply not ours. Ignored, never reported. */
const OTHER_LANGUAGE_FOLDERS = new Set(["vb", "visualbasic", "fs", "fsharp"]);

interface ParsedAssetPath {
    entry: string;
    /** Roslyn version folder, or null for the unversioned layout. */
    roslyn: { major: number; minor: number } | null;
    /** Language folder, or null when the asset sits directly under the version/dotnet folder. */
    language: string | null;
}

/**
 * Choose the analyzer assets that apply to a C# compilation on this host.
 *
 * Selection mirrors the SDK: among the `roslyn<X.Y>` folders, take the highest that the host
 * can load, and use only that folder's assets. When no versioned folder qualifies, fall back
 * to the unversioned layout. Assets for other languages are skipped without comment; anything
 * under `analyzers/` that does not fit the convention is returned in `unrecognised` so the
 * caller can say so rather than drop it.
 */
export function selectAnalyzerAssets(
    entryNames: readonly string[],
    roslynVersion: { major: number; minor: number } = CARBIDE_ROSLYN_VERSION,
): AnalyzerAssetSelection {
    const parsed: ParsedAssetPath[] = [];
    const unrecognised: string[] = [];

    for (const raw of entryNames) {
        const normalised = raw.replace(/\\/g, "/");
        if (!/^analyzers\//i.test(normalised)) continue;
        // Directory entries carry a trailing slash in some packages; they say nothing.
        if (normalised.endsWith("/")) continue;
        // Satellite assemblies (`.../<culture>/Foo.resources.dll`) are localised strings for
        // the analyzer's own diagnostic messages, not analyzers. Every Microsoft package ships
        // a dozen-plus of them per roslyn folder — reporting those as unplaceable analyzers
        // buries the real signal and trains callers to ignore MSNUGET017. Carbide loads
        // analyzers from bytes, so satellites are unavailable either way and messages come out
        // in the neutral culture.
        if (/\.resources\.dll$/i.test(normalised)) continue;

        const parsedPath = parseAnalyzerPath(normalised);
        if (parsedPath === null) {
            // Only .dll entries matter for "did we miss an analyzer?" — a stray .pdb, .xml, or
            // .props beside the assembly is not itself an analyzer and reporting it would
            // train callers to ignore the warning.
            if (/\.dll$/i.test(normalised)) {
                unrecognised.push(raw);
            }
            continue;
        }
        if (parsedPath.language !== null && !CSHARP_LANGUAGE_FOLDERS.has(parsedPath.language)) {
            // A VB or F# analyzer in a package we are consuming from C# is not a gap.
            if (!OTHER_LANGUAGE_FOLDERS.has(parsedPath.language) && /\.dll$/i.test(normalised)) {
                unrecognised.push(raw);
            }
            continue;
        }
        if (!/\.dll$/i.test(normalised)) continue;
        parsed.push({ ...parsedPath, entry: raw });
    }

    if (parsed.length === 0) {
        return { entries: [], unrecognised: unrecognised.sort(compareOrdinal) };
    }

    // Highest roslyn<X.Y> folder this host can load, if any qualifies.
    let best: { major: number; minor: number } | null = null;
    for (const candidate of parsed) {
        const version = candidate.roslyn;
        if (version === null) continue;
        if (!versionAtMost(version, roslynVersion)) continue;
        if (best === null || compareVersion(version, best) > 0) {
            best = version;
        }
    }

    const chosen = parsed.filter((candidate) =>
        best === null ? candidate.roslyn === null : candidate.roslyn !== null && compareVersion(candidate.roslyn, best) === 0,
    );

    // A package that ships ONLY roslyn folders newer than this host contributes nothing —
    // `chosen` is empty because no unversioned fallback exists. Say so through `unrecognised`
    // rather than returning an empty selection that reads as "no analyzers here".
    if (chosen.length === 0) {
        for (const candidate of parsed) {
            unrecognised.push(candidate.entry);
        }
        return { entries: [], unrecognised: unrecognised.sort(compareOrdinal) };
    }

    return {
        entries: chosen.map((candidate) => candidate.entry).sort(compareOrdinal),
        unrecognised: unrecognised.sort(compareOrdinal),
    };
}

/**
 * Parse `analyzers/dotnet/[roslyn<X.Y>/][<lang>/]<file>`. Returns null for anything else,
 * including the `analyzers/<lang>/` layout that predates the `dotnet` segment — Carbide does
 * not guess at layouts it has not seen.
 */
function parseAnalyzerPath(entry: string): { roslyn: { major: number; minor: number } | null; language: string | null } | null {
    const segments = entry.split("/");
    // analyzers / dotnet / ... / file
    if (segments.length < 3) return null;
    if (segments[1].toLowerCase() !== "dotnet") return null;

    // Between `dotnet` and the file name there are 0, 1, or 2 segments.
    const middle = segments.slice(2, segments.length - 1);
    if (middle.length > 2) return null;

    let roslyn: { major: number; minor: number } | null = null;
    let language: string | null = null;

    for (let i = 0; i < middle.length; i++) {
        const segment = middle[i].toLowerCase();
        const version = parseRoslynFolder(segment);
        if (version !== null) {
            // A roslyn folder is only meaningful as the first segment after `dotnet`.
            if (i !== 0 || roslyn !== null) return null;
            roslyn = version;
            continue;
        }
        if (language !== null) return null;
        language = segment;
    }

    return { roslyn, language };
}

function parseRoslynFolder(segment: string): { major: number; minor: number } | null {
    const match = /^roslyn(\d+)(?:\.(\d+))?$/.exec(segment);
    if (!match) return null;
    return { major: Number(match[1]), minor: match[2] === undefined ? 0 : Number(match[2]) };
}

function compareVersion(a: { major: number; minor: number }, b: { major: number; minor: number }): number {
    if (a.major !== b.major) return a.major < b.major ? -1 : 1;
    if (a.minor !== b.minor) return a.minor < b.minor ? -1 : 1;
    return 0;
}

function versionAtMost(a: { major: number; minor: number }, b: { major: number; minor: number }): boolean {
    return compareVersion(a, b) <= 0;
}
