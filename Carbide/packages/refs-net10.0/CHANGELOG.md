# Changelog — `@carbide/refs-net10.0`

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
package follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). It ships
reference assemblies and a manifest rather than a TypeScript surface, so it has no entry in
[`api/`](../../../api/README.md).

## [Unreleased]

## [0.1.0] - 2026-08-04

First published release.

### Added

- **Reference pack build** (`scripts/build.mjs`, also wired as `postinstall`): downloads the
  pinned `Microsoft.NETCore.App.Ref` package, extracts `ref/net10.0/*.dll`, and writes
  `ref-manifest.json` describing the extracted set.
- **Stable compile-time surface.** Installing this package alongside `@carbide/core` makes
  Carbide compile against the untrimmed .NET 10 reference assemblies, so the API surface a
  program compiles against no longer follows the runtime's trim decisions.
- **Upstream notices.** `THIRD_PARTY_NOTICES.md` and `third-party/dotnet/` carry the
  redistributed material's own terms; the extracted assemblies remain under those terms and
  are not relicensed by Carbide's Apache-2.0 license.

[Unreleased]: https://github.com/VladimirReshetnikov/Carbide/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/VladimirReshetnikov/Carbide/releases/tag/v0.1.0
