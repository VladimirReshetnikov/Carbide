// Deterministic string ordering.
//
// `String.prototype.localeCompare` must not be used to order anything Carbide emits. It is
// collation-based, so its answer depends on the host's ICU locale data — `"z".localeCompare("ä")`
// is `1` under `en` and `-1` under `sv` — and it collates punctuation and case rather than
// comparing code units, so `a.b` and `ab` move relative to capitalised names. Either property
// alone is enough to make the same inputs produce different output on different machines,
// which defeats reproducible builds and turns a re-run into a spurious diff.
//
// `scripts/check-locale-sensitivity.mjs` and the repository ESLint config keep it out.

/** Ordinal (code-unit) comparison, the same ordering `Array.prototype.sort` uses by default. */
export function compareOrdinal(a: string, b: string): number {
    return a < b ? -1 : a > b ? 1 : 0;
}
