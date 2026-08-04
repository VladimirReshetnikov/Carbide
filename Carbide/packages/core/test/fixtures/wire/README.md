# Frozen wire payloads

Golden JSON payloads for the JSExport boundary between `@carbide/core`'s TypeScript layer
and `Carbide.Core` (C#). They are the M7 compatibility freeze for the wire contract, and are
exercised by [`../../node/wire-compat.test.mjs`](../../node/wire-compat.test.mjs).

Each file is named `<shape>.v<schemaVersion>.json` and holds one payload exactly as it
travels across the boundary. Files are **append-only**: an existing file records what a
released Carbide emitted, so editing one rewrites history rather than describing a change.
When the wire shape changes, bump `SCHEMA_VERSION` in `src/ts/interop/schema.ts` (and in
lock-step on the C# side) and add new `.v<N>.json` files beside the old ones.

Directions:

- `*-result*.json` and `diagnostics*.json` travel **C# → TypeScript** and are parsed by
  `parseRunResult` / `parseBuildResult` / `parseDiagnostics`. The test asserts the current
  parsers still accept every version inside the documented acceptance window.
- `*-request*.json` travel **TypeScript → C#**. The test asserts they still match the
  exported request interfaces field-for-field; `scripts/check-wire-schema.mjs` separately
  asserts the C# DTOs on the far side carry the same fields.

The `v4` payloads exist because the TypeScript parsers deliberately accept one back-version
(`SCHEMA_VERSION - 1`) so a partially rebuilt tree fails loudly on real mismatches rather
than on the one-step transition.

## License

Part of Carbide, licensed under the repository's [Apache License 2.0](../../../../../../LICENSE),
with copyright held collectively by Carbide Contributors.
