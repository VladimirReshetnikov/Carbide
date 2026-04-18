// NuGet version-range subset. See M6 §3 M6.3 and D73 (pre-release allowed).
// Supported:
//   1.2.3                — exact pin, equivalent to [1.2.3,1.2.3].
//   [1.0.0,2.0.0)        — standard bracketed range.
//   (1.0.0,)             — open upper bound.
//   [1.0.0,]             — open upper bound (inclusive lower).
//   [1.0.0,1.0.0]        — exact pin via range form.
//   1.0.0-preview.2      — pre-release (full identity).
// Refused (MSNUGET001):
//   1.*   1.2.*   *      — floating versions.

export interface Version {
    major: number;
    minor: number;
    patch: number;
    revision: number;
    /** Empty string for release; "alpha", "beta.3", etc. for pre-release. */
    preRelease: string;
    /** Raw string as parsed; preserved so string equality survives parse→format. */
    raw: string;
}

export interface VersionRange {
    lower: Version | null;
    lowerInclusive: boolean;
    upper: Version | null;
    upperInclusive: boolean;
    raw: string;
}

export class VersionParseError extends Error {
    constructor(message: string, public readonly code: string = "MSNUGET000") {
        super(message);
        this.name = "VersionParseError";
    }
}

/** Parse a version string. Rejects floating versions. */
export function parseVersion(s: string): Version {
    const trimmed = s.trim();
    if (trimmed.length === 0) throw new VersionParseError("Empty version string.");
    if (trimmed.includes("*")) {
        throw new VersionParseError(
            `Floating version '${trimmed}' is not supported in M6 (see MSNUGET001).`,
            "MSNUGET001",
        );
    }
    // Separate pre-release suffix.
    const dashIdx = trimmed.indexOf("-");
    const core = dashIdx < 0 ? trimmed : trimmed.slice(0, dashIdx);
    const preRelease = dashIdx < 0 ? "" : trimmed.slice(dashIdx + 1);
    const parts = core.split(".");
    if (parts.length < 1 || parts.length > 4) {
        throw new VersionParseError(`Malformed version '${trimmed}': expected 1 to 4 numeric components.`);
    }
    const nums = parts.map((p) => {
        if (!/^\d+$/.test(p)) {
            throw new VersionParseError(`Malformed version '${trimmed}': '${p}' is not numeric.`);
        }
        return parseInt(p, 10);
    });
    while (nums.length < 4) nums.push(0);
    return {
        major: nums[0],
        minor: nums[1],
        patch: nums[2],
        revision: nums[3],
        preRelease,
        raw: trimmed,
    };
}

/** Compare two versions. Returns < 0, 0, or > 0. Pre-release ordering follows SemVer. */
export function compareVersion(a: Version, b: Version): number {
    if (a.major !== b.major) return a.major - b.major;
    if (a.minor !== b.minor) return a.minor - b.minor;
    if (a.patch !== b.patch) return a.patch - b.patch;
    if (a.revision !== b.revision) return a.revision - b.revision;
    // Pre-release compare: a release version sorts after a pre-release.
    if (a.preRelease === "" && b.preRelease === "") return 0;
    if (a.preRelease === "") return 1; // a is release, b is pre-release → a > b.
    if (b.preRelease === "") return -1;
    return comparePreRelease(a.preRelease, b.preRelease);
}

function comparePreRelease(a: string, b: string): number {
    const aParts = a.split(".");
    const bParts = b.split(".");
    const n = Math.max(aParts.length, bParts.length);
    for (let i = 0; i < n; i++) {
        const av = aParts[i];
        const bv = bParts[i];
        if (av === undefined) return -1;
        if (bv === undefined) return 1;
        const aNum = /^\d+$/.test(av) ? parseInt(av, 10) : null;
        const bNum = /^\d+$/.test(bv) ? parseInt(bv, 10) : null;
        if (aNum !== null && bNum !== null) {
            if (aNum !== bNum) return aNum - bNum;
        } else if (aNum !== null) {
            return -1; // numeric identifiers sort below alphanumeric
        } else if (bNum !== null) {
            return 1;
        } else {
            const cmp = av.localeCompare(bv);
            if (cmp !== 0) return cmp;
        }
    }
    return 0;
}

/** Equality of two Version records (ignores `raw`). */
export function versionEq(a: Version, b: Version): boolean {
    return compareVersion(a, b) === 0;
}

/** Parse a version-range string. Bare "1.2.3" is treated as a lower-inclusive minimum (NuGet convention: "1.2.3" means ≥ 1.2.3, preferring that exact version when available). */
export function parseRange(s: string): VersionRange {
    const raw = s.trim();
    if (raw.length === 0) throw new VersionParseError("Empty version range.");
    const first = raw[0];
    if (first === "[" || first === "(") {
        // Bracket form.
        const last = raw[raw.length - 1];
        if (last !== "]" && last !== ")") {
            throw new VersionParseError(`Malformed bracketed range: '${raw}'.`);
        }
        const inner = raw.slice(1, -1).trim();
        const commaIdx = inner.indexOf(",");
        if (commaIdx < 0) {
            // No comma → exact match, e.g. [1.2.3].
            const v = parseVersion(inner);
            return { lower: v, lowerInclusive: true, upper: v, upperInclusive: true, raw };
        }
        const left = inner.slice(0, commaIdx).trim();
        const right = inner.slice(commaIdx + 1).trim();
        const lower = left ? parseVersion(left) : null;
        const upper = right ? parseVersion(right) : null;
        return {
            lower,
            lowerInclusive: first === "[",
            upper,
            upperInclusive: last === "]",
            raw,
        };
    }
    // Bare version → ≥ version (inclusive lower, open upper). NuGet convention.
    const v = parseVersion(raw);
    return { lower: v, lowerInclusive: true, upper: null, upperInclusive: false, raw };
}

/** True if `v` satisfies `range`. */
export function contains(range: VersionRange, v: Version): boolean {
    if (range.lower) {
        const cmp = compareVersion(v, range.lower);
        if (range.lowerInclusive ? cmp < 0 : cmp <= 0) return false;
    }
    if (range.upper) {
        const cmp = compareVersion(v, range.upper);
        if (range.upperInclusive ? cmp > 0 : cmp >= 0) return false;
    }
    return true;
}

/**
 * Pick the lowest version from `available` that satisfies `range`. NuGet's default:
 * "nearest version" within the range. Returns null if nothing satisfies.
 */
export function bestMatch(range: VersionRange, available: readonly Version[]): Version | null {
    const sorted = [...available].sort(compareVersion);
    for (const v of sorted) {
        if (contains(range, v)) return v;
    }
    return null;
}
