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

   Run it at least once from a **fresh clone**, not from your working tree:

   ```powershell
   git clone . <scratch>\freshclone
   ```

   A working tree carries `obj/`, `node_modules/`, and an already-extracted ref pack, so it
   cannot see the failures that only a clean checkout hits — the restore ordering for the
   out-of-band `Carbide.System.Console` build broke exactly this way once, with a
   `NETSDK1004` that no incremental build would ever have reproduced. A contributor
   following the README gets the clean path, so it is the one worth proving.

7. **Check publish readiness.** This asks npm what each package would actually ship and
   checks the answer — license, notices, changelog, build output, the Mono-WASM `_framework`
   payload, and the extracted reference pack — plus the manifest metadata npm needs for
   provenance and a guard against prerelease `PackageReference`s in projects whose output
   ships.

   ```powershell
   node scripts/check-publish.mjs
   ```

   Generated payloads (`_framework`, the ref pack) are only checked when they exist, so run
   this *after* step 6. In the default mode a missing payload is reported as a note; step 8
   turns those notes into failures.

8. **Rewrite the sibling dependencies.** `@carbide/cli` resolves `@carbide/core`,
   `@carbide/msbuild-lite`, and `@carbide/nuget` through `file:` references so the workspace
   works without a registry. Those cannot survive publication:

   ```powershell
   node scripts/prepare-publish.mjs
   node scripts/check-publish.mjs --release
   ```

   `--release` is the strict form: it fails if any local dependency spec remains **and** if
   any generated payload was skipped rather than verified. A silent skip is exactly how an
   incomplete tarball would reach the registry, so this must be clean before publishing.

9. **Rehearse the install.** Every suite in this repository runs against the *workspace*, not
   against what a consumer receives. Pack the tarballs and install them into a scratch
   directory to close that gap before anything reaches a registry:

   ```powershell
   npm pack --pack-destination <scratch>\tarballs   # in each package directory
   ```

   Then, in a fresh directory whose `package.json` points every `@carbide/*` dependency **and
   override** at the packed tarballs, run `npm install` and check:

   - all five packages install, and `@carbide/refs-net10.0`'s `postinstall` extracts the ref
     pack (~167 assemblies);
   - `@carbide/core`'s `_framework` payload is present;
   - `carbide --version`, `carbide run --source …`, `carbide validate` (exit 0 clean, exit 1
     on a compile error), and `carbide build --out …` all behave;
   - a script importing `@carbide/core` and `@carbide/core/node` resolves through the
     `exports` map and completes a build + run.

   The `overrides` block matters: without it npm resolves `@carbide/cli`'s `^0.1.0` sibling
   ranges from the registry, which is exactly what you are trying not to depend on yet.

10. **Publish** in dependency order — `msbuild-lite`, `nuget`, `refs-net10.0`, `core`, `cli`:

   ```powershell
   npm publish --access public
   ```

11. **Restore and tag.**

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
