// MSNUGET warning code registry. See carbide-M6-detailed-plan §5 D76.
//
// 000-009: parse/format.
// 010-014: resolution (nearest-wins, etc.)
// 015-019: safety refusals.
// 020-029: allow-list advisory/refusal.
// 030-039: cache/offline.
// 040-049: integrity (SHA mismatch).
// 050+:    reserved.

export const MSNUGET_CODES = {
    PARSE_ERROR: "MSNUGET000",
    FLOATING_VERSION_UNSUPPORTED: "MSNUGET001",
    NEAREST_WINS_TIE: "MSNUGET010",
    /**
     * A package resolved, but none of its `lib/<tfm>/` folders is compatible with the target
     * framework, so it contributes no references. Without this the failure surfaces far from
     * its cause: the build reports CS0246 against the user's own source with no hint that a
     * package silently supplied nothing.
     */
    NO_COMPATIBLE_LIB_FOLDER: "MSNUGET011",
    /**
     * A package declares `<dependencies>` groups but none targets the framework being
     * resolved, so it contributes no transitive dependencies. Reported rather than assumed:
     * the alternative — merging every group — silently pulled packages meant for other
     * frameworks into the graph.
     */
    NO_COMPATIBLE_DEPENDENCY_GROUP: "MSNUGET012",
    SAFETY_NATIVE: "MSNUGET015",
    SAFETY_TARGETS: "MSNUGET016",
    /**
     * A package carries an analyzer Carbide could not place: a layout outside NuGet's
     * `analyzers/dotnet/[roslyn<X.Y>/][<lang>/]` convention, or only `roslyn<X.Y>` folders
     * newer than the Roslyn version Carbide compiles with.
     *
     * Before M12 this was a refusal that took the whole package down. Now the package's
     * `lib/` assets are used as normal and only the unplaceable analyzer is reported — but it
     * *is* reported, because a generator that never runs otherwise surfaces as a compile error
     * about a type that was supposed to be generated, with nothing pointing at the package.
     */
    SAFETY_ANALYZERS: "MSNUGET017",
    /**
     * A package's analyzer assets were selected and loaded, but carry neither a source
     * generator nor a diagnostic analyzer — a code-fix-only assembly, say. Reported rather
     * than swallowed for the same reason as MSNUGET017: "loaded and contributed nothing" has
     * to be visible. Raised per package, not per asset: shipping a code-fix assembly beside
     * the generator is the normal layout, and warning on each would fire every build.
     */
    ANALYZER_NO_GENERATOR: "MSNUGET018",
    SAFETY_UNKNOWN: "MSNUGET019",
    ALLOWLIST_ADVISORY: "MSNUGET020",
    ALLOWLIST_REFUSED: "MSNUGET021",
    CACHE_MISS_OFFLINE: "MSNUGET030",
    CACHE_READ_ERROR: "MSNUGET031",
    INTEGRITY_MISMATCH: "MSNUGET040",
} as const;
