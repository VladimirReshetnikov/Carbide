// Must match the `version` field in package.json — asserted by smoke.node.test.mjs.
// Kept as a literal (rather than read from package.json at runtime) so browser bundles
// don't need a JSON import or a loader capable of resolving one.
export const CARBIDE_VERSION = "0.1.0-dev" as const;
