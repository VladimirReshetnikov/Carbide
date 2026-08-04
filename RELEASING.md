# Releasing Carbide

Carbide publishes five packages in lock-step at a single version:

| Package | Directory |
| --- | --- |
| `@carbide/core` | `Carbide/packages/core` |
| `@carbide/cli` | `Carbide/packages/cli` |
| `@carbide/msbuild-lite` | `Carbide/packages/msbuild-lite` |
| `@carbide/nuget` | `Carbide/packages/nuget` |
| `@carbide/refs-net10.0` | `Carbide/packages/refs-net10.0` |

`Carbide.UI` packages are private (`0.0.0-dev.0`) and are deliberately outside this train.

## Compatibility rules

From `0.1.0` onward the published surface is frozen and gated. Three contracts move
together, and each has a machine check:

| Contract | Recorded in | Gate |
| --- | --- | --- |
| TypeScript exports, CLI flags, exit codes | [`api/`](api/README.md) | `node scripts/api-surface.mjs` |
| JSExport wire payloads (`SCHEMA_VERSION`) | `Carbide/packages/core/test/fixtures/wire/` | `node scripts/check-wire-schema.mjs` + `wire-compat.test.mjs` |
| Versions and release notes | `CHANGELOG.md` files | `node scripts/check-changelog.mjs` |
| Tarball contents and manifest metadata | each package's `files` allow-list | `node scripts/check-publish.mjs` |

While the version is `0.x`:

- A **removal** or an incompatible narrowing of anything in `api/` is breaking, and takes a
  minor bump (`0.1.0` → `0.2.0`).
- An **addition** takes a patch bump (`0.1.0` → `0.1.1`).
- A **wire-shape change** bumps `SCHEMA_VERSION` on both sides in lock-step, adds a new
  `.v<N>.json` payload beside the existing ones (they are append-only), and widens the C#
  `ValidateSchemaVersion` accept-list. The TypeScript parsers accept exactly one
  back-version, so a runtime and a TypeScript layer may never differ by more than one step.

## Cutting a release

1. **Land the work.** Every change that touches a published surface must already carry its
   regenerated `api/` report and its `CHANGELOG.md` entry under `## [Unreleased]` — CI
   refuses the pull request otherwise.

2. **Set the version.** Bump `version` in all five `package.json` files and the
   `CARBIDE_VERSION` literal in `Carbide/packages/core/src/ts/version.ts` (it is a literal
   because browser bundles cannot import `package.json`).

3. **Promote the changelogs.** In each package's `CHANGELOG.md` and in the repository
   `CHANGELOG.md`, turn `## [Unreleased]` into `## [x.y.z] - YYYY-MM-DD`, add a fresh empty
   `## [Unreleased]` above it, and add the link definitions at the bottom.

4. **Run the gates.**

   ```powershell
   node scripts/check-licenses.mjs
   node scripts/check-changelog.mjs
   node scripts/check-wire-schema.mjs
   npm run lint
   ```

5. **Rebuild and refreeze.** Build the pure-TypeScript packages in dependency order, then
   regenerate the API reports — the reports embed the version, so they change on every
   release.

   ```powershell
   node scripts/build-ts-packages.mjs
   node scripts/api-surface.mjs --write
   ```

6. **Validate for real.** Run the full build and test flow from
   [`README.md`](README.md#build-and-validation) — the WASM publish, the core Node and
   browser suites, the CLI suite, and the Carbide.UI launcher suite. A release is not cut
   from a green fast gate alone.

7. **Check publish readiness.** This asks npm what each package would actually ship and
   checks the answer — license, notices, changelog, build output, and the Mono-WASM
   `_framework` payload, plus the manifest metadata npm needs for provenance.

   ```powershell
   node scripts/check-publish.mjs
   ```

   Run it *after* step 6, so the `_framework` slice is present rather than reported as
   skipped.

8. **Rewrite the sibling dependencies.** `@carbide/cli` resolves `@carbide/core`,
   `@carbide/msbuild-lite`, and `@carbide/nuget` through `file:` references so the workspace
   works without a registry. Those cannot survive publication:

   ```powershell
   node scripts/prepare-publish.mjs
   node scripts/check-publish.mjs --release
   ```

   `--release` fails if any local spec remains.

9. **Publish** in dependency order — `msbuild-lite`, `nuget`, `refs-net10.0`, `core`, `cli`:

   ```powershell
   npm publish --access public
   ```

10. **Restore and tag.**

    ```powershell
    node scripts/prepare-publish.mjs --restore
    git tag -a v0.1.0 -m "Carbide 0.1.0"
    git push origin v0.1.0
    ```

    The restore is a byte-clean round trip — `git diff` should be empty afterwards. Do not
    commit the rewritten manifests; `main` keeps the `file:` references.

## After the release

Open the next `## [Unreleased]` sections and leave them empty. The API reports stay at the
released version until the next bump; a pull request that changes them without a changelog
entry is exactly what the gate is there to catch.

## License

This document is part of Carbide and is licensed under the repository's
[Apache License 2.0](LICENSE), with copyright held collectively by Carbide Contributors.
