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
    SAFETY_NATIVE: "MSNUGET015",
    SAFETY_TARGETS: "MSNUGET016",
    SAFETY_ANALYZERS: "MSNUGET017",
    SAFETY_GENERATORS: "MSNUGET018",
    SAFETY_UNKNOWN: "MSNUGET019",
    ALLOWLIST_ADVISORY: "MSNUGET020",
    ALLOWLIST_REFUSED: "MSNUGET021",
    CACHE_MISS_OFFLINE: "MSNUGET030",
    CACHE_READ_ERROR: "MSNUGET031",
    INTEGRITY_MISMATCH: "MSNUGET040",
} as const;
