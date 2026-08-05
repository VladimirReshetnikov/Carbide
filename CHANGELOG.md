# Changelog

All notable changes to the Carbide repository are documented here. Per-package detail lives
in each package's own changelog:

- [`@carbide/core`](Carbide/packages/core/CHANGELOG.md)
- [`@carbide/cli`](Carbide/packages/cli/CHANGELOG.md)
- [`@carbide/msbuild-lite`](Carbide/packages/msbuild-lite/CHANGELOG.md)
- [`@carbide/nuget`](Carbide/packages/nuget/CHANGELOG.md)
- [`@carbide/refs-net10.0`](Carbide/packages/refs-net10.0/CHANGELOG.md)

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the
published packages follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html). All
published packages are released in lock-step at a single version.

`Carbide.UI` packages are private (`0.0.0-dev.0`) and are not part of this release train.

## [Unreleased]

### Added

- **Source-generator support (M12).** Roslyn source generators now run as part of Carbide
  compilation. `session.addAnalyzer` / `project.addAnalyzer` register and attach a generator
  assembly programmatically, and `carbide build|run|validate --analyzer <path>` does the same
  from the CLI. Generated source participates in diagnostics, in the emitted assembly, and in
  execution.
- **Diagnostic analyzers run too.** The same surface runs `DiagnosticAnalyzer` implementations;
  their diagnostics join the compiler's own, so an analyzer error fails a build. Code-fix
  providers stay out of scope. See the
  [`@carbide/core` changelog](Carbide/packages/core/CHANGELOG.md) for the exact boundary.
- **`.csproj` analyzer declarations are honoured**: `<Analyzer Include="..."/>` items, and
  `<ProjectReference>` carrying `OutputItemType="Analyzer"` / `ReferenceOutputAssembly="false"`.
  This adds three additive fields to `@carbide/msbuild-lite`'s `ProjectModel` that
  `cs_kit.msbuild_lite` does not carry — see that package's changelog for the divergence.
- **Source generators shipped inside NuGet packages now run.** A package carrying `analyzers/`
  used to be refused outright, which made every mainstream package that ships a generator
  unusable. `@carbide/nuget` now selects the assets that apply and `carbide build|run|validate`
  attaches them, so a `<PackageReference>` to such a package works end to end. What could not
  be applied is reported (`MSNUGET017` / `MSNUGET018`) rather than dropped.

### Fixed

- **A dependency-version bump could publish the previous version's assemblies.** With Webcil
  packaging on, the SDK converts each assembly under `obj/<config>/<tfm>/webcil/` and reuses
  the converted image whenever it is newer than its source. A NuGet package restored earlier
  carries an earlier timestamp, so changing a `PackageReference` to a different *version* left
  the old converted image in place and shipped it — a freshly built `Carbide.Core` on top of
  the previous compiler, with no error and no change in asset names. `rm -rf publish/` does not
  help, because the staleness lives in `obj/`. `Carbide.Core.csproj` now drops the converted
  output whenever `project.assets.json` moves, which is exactly when the resolved package graph
  changes. Found while evaluating Roslyn `5.x`, where it made the first attempt report success
  while still running `4.14.0`. See the
  [drift report](Carbide/docs/drift/carbide-drift-report__2026-08-05__roslyn-5x-evaluation.md).

### Changed

- **The Roslyn `4.14.0` pin is confirmed, not merely inherited.** The `0.1.0` drift report
  asked for the `4.x` → `5.x` jump to be evaluated for `0.2.0`. It was: Roslyn `5.6.0` does not
  run on Carbide's Mono-WASM configuration at all — the first compilation dies with a
  `StackOverflowException` in `System.Threading.Volatile.ReadBarrier` recursing into itself —
  while `5.0.0` and `5.3.0` pass. `5.3.0` validates across the whole matrix and is recorded as
  a viable target, but adopting it would buy compiler currency without an upgrade path, so the
  hold stands and the decision is left open.

- Six defects in `@carbide/core`'s browser-interactive path, closing every finding from the
  [C# silent-divergence audit](Carbide/docs/reports/carbide-csharp-silent-divergence-audit__2026-08-07__c1a4f9e28b73.md):
  output from runs after the first was routed to the first run's terminal; `session.shutdown()`
  never released a program parked at a prompt; disposing a run suspended in `ReadKeyAsync`
  froze the page; a newline-less `Console.Error` prompt stayed invisible while the program
  waited for input; `Console.CancelKeyPress` handlers leaked across runs and silently vetoed
  Ctrl+C; and handle-level writes corrupted multi-byte characters split across calls. Each is
  pinned by a new browser fixture, verified failing before the fix and passing after. See the
  [`@carbide/core` changelog](Carbide/packages/core/CHANGELOG.md) for detail.

## [0.1.0] - 2026-08-04

First published release: Band A complete, Band B partial (shapes S1–S3 firm, S4 behind the
NuGet allow-list, S5 for single-plus-siblings project graphs).

### Added

- Five published packages: `@carbide/core`, `@carbide/cli`, `@carbide/msbuild-lite`,
  `@carbide/nuget`, and the opt-in reference pack `@carbide/refs-net10.0`.
- **API stability lock.** The exported TypeScript surface of every published package is
  frozen in [`api/`](api/README.md) and regenerated by `scripts/api-surface.mjs`; CI fails
  on an unrecorded change, so an API diff is always visible in review.
- **Wire-contract freeze.** The JSExport payloads between the TypeScript and C# layers are
  pinned at `SCHEMA_VERSION` 5 by golden fixtures under
  `Carbide/packages/core/test/fixtures/wire/`, and `scripts/check-wire-schema.mjs` asserts
  the TypeScript interfaces and C# DTOs still agree field-for-field.
- **Release gates.** `scripts/check-changelog.mjs` keeps every package's changelog and
  version in lock-step; `scripts/check-licenses.mjs` keeps the license and provenance
  material consistent; `scripts/check-publish.mjs` asks npm what each package would actually
  ship and checks the tarball carries its license, notices, changelog, and build output. All
  run on every pull request.
- **Publish preparation.** `scripts/prepare-publish.mjs` rewrites `@carbide/cli`'s `file:`
  sibling references to the published range (and back), so the workspace stays usable
  without a registry while published installs still resolve.
- **Reproducibility lint.** ESLint now rejects `localeCompare` and the `toLocale*` family in
  the shipped TypeScript sources. Three separate audits found `localeCompare` deciding real
  outcomes — which package version wins a tie, the ordering inside `carbide.lock.json`, and
  the order of a `carbide audit` payload — each of which would let two machines derive
  different results from identical inputs. The rule turns a repeatedly-rediscovered class of
  defect into a build failure.
- **Install rehearsal.** The release flow now packs real tarballs and installs them into a
  scratch project before anything reaches a registry — every other suite runs against the
  workspace rather than against what a consumer receives. The 0.1.0 candidate was verified
  this way: all five packages install, the ref pack's `postinstall` extracts, and both the
  `carbide` binary and a direct `@carbide/core` import build and run.
- [`RELEASING.md`](RELEASING.md) — the publish procedure and the compatibility rules that
  apply from 0.1.0 onward.
- The first [upstream-drift report](Carbide/docs/drift/carbide-drift-report__2026-08-04__release-0.1.0.md),
  which architecture §11 requires. It found and fixed preview `Microsoft.Extensions.*`
  packages shipping inside `@carbide/core`'s runtime payload, and records the deliberate
  hold at Roslyn `4.14.0` now that `5.x` has shipped.

[Unreleased]: https://github.com/VladimirReshetnikov/Carbide/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/VladimirReshetnikov/Carbide/releases/tag/v0.1.0
