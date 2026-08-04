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
    SAFETY_ANALYZERS: "MSNUGET017",
    // MSNUGET018 (SAFETY_GENERATORS) was reserved for a finer-grained refusal that told
    // generators apart from other analyzers. Carbide currently rejects `/^analyzers\//`
    // wholesale under MSNUGET017, so the 018 distinction isn't observable — removed
    // rather than kept as a promised but unfulfilled warning (review R1 M3). When
    // analyzer execution lands (T4+), restore this code with real generator detection.
    SAFETY_UNKNOWN: "MSNUGET019",
    ALLOWLIST_ADVISORY: "MSNUGET020",
    ALLOWLIST_REFUSED: "MSNUGET021",
    CACHE_MISS_OFFLINE: "MSNUGET030",
    CACHE_READ_ERROR: "MSNUGET031",
    INTEGRITY_MISMATCH: "MSNUGET040",
} as const;
