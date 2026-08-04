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

[Unreleased]: https://github.com/VladimirReshetnikov/Carbide/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/VladimirReshetnikov/Carbide/releases/tag/v0.1.0
