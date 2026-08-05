# Changelog — `@carbide/nuget`

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
package follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The exported
surface frozen by this release is recorded in
[`api/carbide-nuget.api.md`](../../../api/carbide-nuget.api.md).

## [Unreleased]

### Added

- **Analyzer asset selection.** `selectAnalyzerAssets(entries)` picks the Roslyn analyzer
  assets that apply to a C# compilation, following NuGet's
  `analyzers/dotnet/[roslyn<X.Y>/][<lang>/]` layout: the highest `roslyn` folder
  `CARBIDE_ROSLYN_VERSION` can load wins and is used alone, otherwise the unversioned layout,
  with VB and F# assets skipped. `resolve()` returns them as `ResolvedGraph.analyzers`
  (`ResolvedAnalyzer[]`), kept separate from `references` because an analyzer must never
  become a metadata reference.
- `MSNUGET_CODES.ANALYZER_NO_GENERATOR` (`MSNUGET018`), for a package whose analyzer assets
  load but carry no source generator. Raised per package rather than per asset: shipping a
  code-fix or diagnostic assembly beside the generator is the normal layout (CommunityToolkit.Mvvm
  does exactly that), and warning on each would fire every build and teach callers to ignore
  the code.

  Selection was checked against the real layouts of `System.Text.Json`,
  `Microsoft.Extensions.Logging.Abstractions`, `CommunityToolkit.Mvvm`, and `Newtonsoft.Json`,
  and their generators verified to load and instantiate. Satellite resource assemblies
  (`.../<culture>/Foo.resources.dll`) are ignored outright — Microsoft packages ship thirteen
  per Roslyn folder, and counting them as unplaceable analyzers produced 39 spurious
  MSNUGET017 entries for `System.Text.Json` alone.

### Changed

- **A package carrying `analyzers/` is no longer refused.** `MSNUGET017` used to reject the
  whole package, which made every mainstream package shipping a source generator unusable.
  The assets are now consumed; `MSNUGET017` remains, narrowed to a **warning** for assets
  Carbide could not place — an unrecognised layout, or one available only in `roslyn` folders
  newer than this host. The package's `lib/` references are unaffected either way. The
  warning is not optional: a generator that never ran otherwise surfaces as a compile error
  about a type nobody wrote.
- `compareOrdinal` moved to a shared `ordinal.ts` rather than being redefined per module.
  Locale-sensitive comparison has slipped into this package four times across three audits;
  one canonical helper is one place for it to be right.

## [0.1.0] - 2026-08-04

First published release.

### Added

- **`resolve`** — nearest-wins transitive resolution over a `PackageReference` set, driven
  by a NuGet v3 flat-container client.
- **Package reading** without an unzip dependency: a minimal zip reader plus a `.nuspec`
  parser that turns dependency groups into further package references.
- **Version handling.** SemVer 2 parsing and comparison, NuGet version-range syntax
  (inclusive/exclusive bounds, floating ranges), and best-match selection.
- **TFM compatibility** matrix with `lib/` folder selection: compatible-folder collection
  and nearest-TFM choice for the target framework.
- **Allow-list policy.** Ten seeded managed-only packages (Newtonsoft.Json, YamlDotNet,
  CsvHelper, Humanizer.Core, NodaTime, Scriban, Handlebars.Net, Serilog,
  Serilog.Sinks.Console, FluentAssertions) with `strict`, `advisory`, and `off` modes.
  Refusals throw `AllowListRefusedError`.
- **Safety refusals** for packages carrying native assets, MSBuild `.targets`/`.props`,
  analyzers, or source generators — `SafetyRefusalError` with an `MSNUGET*` code.
- **Filesystem cache** at `~/.carbide/nuget-cache/` (overridable with
  `CARBIDE_NUGET_CACHE_DIR`), content-addressed by SHA-256 with per-entry metadata.
- **Lock file.** `buildLock` / `writeLock` / `readLock` produce and replay
  `carbide.lock.json`; `--offline` resolution against the cache raises
  `OfflineCacheMissError` rather than reaching the network.
- **Structured warnings.** `MSNUGET_CODES` names every diagnostic the resolver can emit.

### Fixed

- **Pre-release labels now compare case-insensitively**, matching NuGet's
  `StringComparer.OrdinalIgnoreCase`. `1.0.0-alpha` and `1.0.0-ALPHA` are one version, not
  two; previously the resolver could carry both and report a same-depth conflict that did
  not exist.
- **Version ordering is no longer locale-dependent.** The comparator used
  `String.localeCompare`, whose collation varies with the host's locale data
  (`"z".localeCompare("ä")` is `1` under `en` and `-1` under `sv`). The same inputs could
  therefore resolve to different package graphs on different machines, quietly undermining
  `carbide.lock.json`'s reproducibility. Comparison is now ordinal with ASCII case folding —
  SemVer restricts identifiers to `[0-9A-Za-z-]`, so this is both sufficient and immune to
  locale-specific casing rules. The resolver's remaining `localeCompare` fallback is gone too.
- **SemVer 2 build metadata is parsed and ignored for precedence.** `1.0.0+build` used to
  fail with a confusing "'0+build' is not numeric", and `1.0.0-beta+exp` compared as greater
  than `1.0.0-beta` instead of equal. Metadata is stripped before the pre-release split, so a
  hyphen inside it (`1.0.0-beta+exp-1`) no longer leaks into the label. `Version` gains a
  `buildMetadata` field carrying it for round-tripping.
- The module header claimed a bare `1.2.3` was "an exact pin, equivalent to `[1.2.3,1.2.3]`",
  contradicting both the code and NuGet, where it means `>= 1.2.3`.
- **The TFM fallback chain reaches every compatible framework.** It stopped at `net6.0` and
  `netstandard2.0`, so a package shipping only `lib/net5.0/`, `lib/netcoreapp3.1/`, or
  `lib/netstandard1.x/` — all common among libraries that have not been repackaged in years —
  matched no folder. Such a package still resolved; it just contributed no references, and
  the build then reported CS0246 against the user's own source with nothing pointing back at
  the package. The chain now runs `net<target>` → … → `net5.0` → `netcoreapp3.1` → … →
  `netcoreapp1.0` → `netstandard2.1` → … → `netstandard1.0`. .NET Framework assets
  (`net472`, `net48`) stay incompatible, which is correct for a net5.0-or-later target. A
  `netstandard` target's own chain also bottomed out at 2.0, leaving `netstandard1.x` targets
  with no compatible folders at all.
- **`MSNUGET011` reports a package that contributes no references**, naming the `lib/` folders
  that were present, so the cause is visible where it happens rather than inferred from a
  compile error later.
- **Dependency-group selection understands long-form target frameworks.** A great many
  published nuspecs write `.NETStandard2.0` / `.NETFramework4.7.2` rather than the short
  folder-name form. Those labels parsed as unrecognised, so *every* group of such a package
  looked incompatible at once and the resolver fell into a fallback that merged them all —
  pulling .NET Framework-only dependencies into a `net10.0` build.
- **No applicable dependency group now means no dependencies**, which is also NuGet's answer,
  and is reported as `MSNUGET012` naming the groups that were declared. The previous fallback
  merged every group "so we at least try something".
- **Group selection and `lib/` folder selection share one compatibility chain**, so a package's
  assets and its transitive dependencies can no longer be chosen under different rules. They
  disagreed about `netcoreapp`.
- **`carbide.lock.json` is byte-reproducible.** Package ordering ran through `localeCompare`,
  which orders differently from ordinal (punctuation and case are collated) *and* varies with
  the host's locale data — so two developers could commit different byte orderings of the same
  graph, in the one artifact whose purpose is reproducibility. Ordering is now ordinal, and the
  `generatedAt` timestamp is no longer written: it guaranteed a diff on every resolve even when
  nothing changed, nothing in Carbide reads it, and git already records when the file changed.
  `ResolveLock.generatedAt` is now optional so previously written locks still parse.

[Unreleased]: https://github.com/VladimirReshetnikov/Carbide/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/VladimirReshetnikov/Carbide/releases/tag/v0.1.0
