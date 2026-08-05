# Changelog — `@carbide/cli`

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
package follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The command,
flag, exit-code, and error-category surface frozen by this release is recorded in
[`api/carbide-cli.api.md`](../../../api/carbide-cli.api.md).

## [Unreleased]

### Added

- **`--analyzer <path>` on `build`, `run`, and `validate`.** Registers a Roslyn
  source-generator DLL and attaches it to the project being compiled. Repeatable, and scoped
  exactly like `--ref`: in `--project` mode it attaches to the root project only, so naming a
  root csproj does not silently reconfigure the libraries it references. A DLL carrying no
  usable source generator is refused rather than compiled against as if it had contributed
  nothing.
- **Source generators from resolved NuGet packages are attached automatically.** Unlike
  `--analyzer`, where the user named a file and a mistake should stop the build, an asset
  picked out of a dependency may legitimately carry nothing Carbide runs — that is reported as
  an `MSNUGET018` warning and the build continues.
- **`<Analyzer Include="..."/>` items from a `.csproj`** are read and attached. A path that
  cannot be read, or an assembly carrying nothing runnable, is reported as `MSPROJ013` and does
  not fail the build — the rest of the project is still buildable.
- **`<ProjectReference OutputItemType="Analyzer" ReferenceOutputAssembly="false"/>`**, the
  standard idiom for a source generator built alongside the project that uses it. The producer
  is built by the graph as before; `OutputItemType="Analyzer"` attaches its output as an
  analyzer and `ReferenceOutputAssembly="false"` keeps it off the consumer's API surface. The
  two are independent, matching MSBuild, and are decided per consumer — the same producer can
  be an ordinary reference to one project and an analyzer to another. A producer declared this
  way that carries no analyzer is reported as `MSPROJ012`.

  Note the boundary: building a Roslyn source generator *with Carbide* needs the
  `Microsoft.CodeAnalysis` reference assemblies, which Carbide does not supply to compilations.
  In practice the generator project is built by the .NET SDK and consumed through
  `<Analyzer Include>` or `--analyzer`.

## [0.1.0] - 2026-08-04

First published release.

### Added

- **Commands.** `carbide build`, `carbide run`, `carbide validate`, `carbide audit`, and
  `carbide tree`, plus top-level `--help` / `--version`.
- **Inputs.** Direct sources with repeatable `--source`, project files with `--project`, and
  `--scratch` to layer ad-hoc sources on top of a project.
- **Outputs.** `--out <dir>` writes `<AssemblyName>.dll` and `.pdb`; `--assembly-name`,
  `--target-framework`, `--no-debug`, and repeatable `--ref` control the compilation.
- **Multi-project graphs.** `--project` walks `<ProjectReference>` edges, compiles
  leaves-first, feeds sibling PEs back as metadata references, and reports diagnostics
  attributed to the sub-project that produced them. Reference cycles and `AssemblyName`
  collisions are hard errors.
- **NuGet flags.** `--lock`, `--no-lock-write`, `--offline`, `--nuget-source`, and
  `--allow-list-mode` drive `@carbide/nuget` for `<PackageReference>` restore and
  `carbide.lock.json` replay.
- **Output formats.** `--format json` (default, sentinel-framed so program output cannot
  corrupt it) and `--format human`.
- **Verbosity.** `--verbose` / `-v`, `--quiet` / `-q`, and `--log-level`.
- **Stable exit-code taxonomy.** `0` success, `1` compile errors or reference cycle, `2` I/O
  or internal error, `3` unsupported flag combination, `4` NuGet policy refusal, `5` NuGet
  network or cache miss — paired with a closed set of `error.category` values in the JSON
  payload.
- **`MSPROJ011`.** A project declaring `<TargetFramework>net8.0</TargetFramework>` now gets a
  warning: the TFM selects which `lib/<tfm>/` folder is taken from a NuGet package, but never
  the compile-time reference set, which is always net10.0. Such a project can bind APIs
  introduced after net8.0 and so compile here while failing on a real net8.0 SDK.
- **Capture-bypass advisories.** `carbide run` routes `@carbide/core`'s `MSCAP001` /
  `MSCAP002` advisories into the JSON payload's `warnings` array (and to stderr under
  `--format human`). They are kept separate from compile diagnostics, so a program that both
  throws and bypasses capture is still reported as a runtime failure with its
  `uncaughtException`, not as a compile failure.
- **Process-level safety net.** Failures raised from detached async paths are routed through
  the same classifier as thrown errors, so they still produce a structured payload and a
  truthful exit code rather than Node's raw unhandled-rejection dump.

[Unreleased]: https://github.com/VladimirReshetnikov/Carbide/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/VladimirReshetnikov/Carbide/releases/tag/v0.1.0
