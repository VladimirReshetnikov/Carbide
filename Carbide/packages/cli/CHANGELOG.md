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
