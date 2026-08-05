// Ordinal string comparison.
//
// `localeCompare` is banned in this package's shipped sources (see eslint.config.mjs): its
// collation is locale-dependent, and every ordering it decided here — lock-file entry order,
// asset selection — is an output that has to be identical on every machine. It slipped in
// four times across three audits before the lint rule existed; one canonical helper keeps the
// remaining call sites from drifting apart.

export function compareOrdinal(a: string, b: string): number {
    return a < b ? -1 : a > b ? 1 : 0;
}
