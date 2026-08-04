# Changelog — `@carbide/msbuild-lite`

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
package follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The exported
surface frozen by this release is recorded in
[`api/carbide-msbuild-lite.api.md`](../../../api/carbide-msbuild-lite.api.md).

## [Unreleased]

## [0.1.0] - 2026-08-04

First published release.

### Added

- **`parseCsproj`** — reads a single `.csproj`, walks `<PropertyGroup>` and `<ItemGroup>`,
  expands compile-item globs against the accumulated property bag, and returns a
  `ProjectModel`.
- **Recognised properties.** `TargetFramework`, `Nullable`, `LangVersion`,
  `ImplicitUsings`, `DefineConstants`, `AssemblyName`, `RootNamespace`, plus the reserved
  `MSBuild*` properties needed for path substitution.
- **Item capture.** `Compile` includes and removes with default-glob handling
  (`EnableDefaultCompileItems`), `PackageReference`, and `ProjectReference`.
- **Bounded evaluation.** Simple `Condition` expressions, property substitution, and a
  `Directory.Build.props` ancestor walk whose properties feed the evaluator before the
  `.csproj` itself.
- **`<Import Project="…"/>`** with conditions, variable substitution, nesting, and cycle
  detection. A missing import target logs `MSBLITE024` rather than failing the parse.
- **Explicit refusals.** `<Target>`, `<Task>`, `<UsingTask>`, `<Choose>`, and
  `<ItemDefinitionGroup>` emit `MSBLITE020`–`MSBLITE023` and `MSBLITE028`;
  `Directory.Build.targets` is discovered but not ingested (`MSBLITE027`). Nothing is
  silently ignored — every unsupported construct produces a diagnostic code.

[Unreleased]: https://github.com/VladimirReshetnikov/Carbide/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/VladimirReshetnikov/Carbide/releases/tag/v0.1.0
