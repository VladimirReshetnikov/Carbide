# Changelog — `@carbide/nuget`

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
package follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The exported
surface frozen by this release is recorded in
[`api/carbide-nuget.api.md`](../../../api/carbide-nuget.api.md).

## [Unreleased]

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

[Unreleased]: https://github.com/VladimirReshetnikov/Carbide/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/VladimirReshetnikov/Carbide/releases/tag/v0.1.0
