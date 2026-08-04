# Changelog — `@carbide/core`

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
package follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The exported
surface frozen by this release is recorded in
[`api/carbide-core.api.md`](../../../api/carbide-core.api.md).

## [Unreleased]

## [0.1.0] - 2026-08-04

First published release.

### Added

- **Dual-host runtime.** `CarbideSession.initializeAsync` boots Roslyn on a Mono-WASM .NET
  runtime through a host adapter: `BrowserHostAdapter` (fetches `_framework/` assets over
  HTTP) and `NodeHostAdapter` (file or local-HTTP asset delivery, exported from
  `@carbide/core/node`).
- **Project API.** `Project` manages sources by caller-supplied logical path
  (`addSource` / `updateSource` / `removeSource`), returns Roslyn diagnostics with path and
  line/column attribution, emits deterministic PE + portable-PDB bytes from `build()`, and
  executes programs with `run()`.
- **Entry-point support** for top-level statements, `Main()`, `Task`, `Task<int>`,
  `ValueTask`, and `ValueTask<int>`.
- **User references.** `CarbideSession.addReference` registers assembly bytes as
  session-scoped handles that `Project.addReference` attaches; handles are validated
  synchronously and invalidated on removal or session shutdown.
- **`runAssembly`** executes previously emitted PE bytes with runtime-loadable dependency
  assemblies, in a collectible `AssemblyLoadContext`.
- **Program argv and stdin forwarding** through `RunOptions`.
- **Interactive terminal.** `Project.runInteractive({ terminal })` streams stdout and stderr
  into an xterm.js-shaped terminal while the program runs, forwards keystrokes and resize
  events back, and exposes `Carbide.Terminal.CarbideConsole` for async console input, colors,
  cursor control, window geometry, `TreatControlCAsInput`, and `CancelKeyPress`. A Carbide-
  forked `System.Console.dll` is overlaid on the Mono-WASM runtime so pre-compiled libraries
  calling stock `Console.*` APIs get the same behavior.
- **Sideloaded reference packs** via `CarbideOptions.sideload`, resolved through the host
  adapter, so an installed ref-pack sibling supplies the compile-time API surface.
- **Node asset server** (`startAssetServer`) for serving `_framework/` assets to a browser
  or to the Node adapter's HTTP delivery mode.
- **Versioned wire contract.** `SCHEMA_VERSION` (currently `5`) travels on every JSExport
  payload; a mismatch throws `CarbideSchemaError` rather than silently accepting a malformed
  shape. The parsers accept exactly one back-version so a partially rebuilt tree fails on
  real mismatches, not on the transition.
- `RunAssemblyOptionsRequest` is now a declared type on the `./interop/schema` entry point,
  alongside the other request shapes.
- **Capture-bypass advisories.** Output written through `Console.OpenStandardOutput()` /
  `OpenStandardError()` never reaches Carbide's `Console.SetOut` capture — Mono-WASM sends
  it down the file-descriptor path, so it lands on the host process's real stdio and is
  absent from the returned `RunResult`. `run()` now reports every such call site as an
  `MSCAP001` warning in `RunResult.diagnostics`, and `Console.OpenStandardInput()` (which
  never observes the `stdin` Carbide seeds through `Console.In`) as `MSCAP002`. Detection is
  symbol-bound, so a user-defined method that happens to share the name is not flagged.
  `MSCAP001` is suppressed for `runInteractive`, where the terminal bridge does receive
  handle-level writes.

### Notes

- Webcil mode is off (`<WasmEnableWebcil>false</WasmEnableWebcil>`); source generators and
  analyzers are not supported.
- The `MSCAP00*` advisories report the bypass rather than capturing the escaped bytes. The
  runtime's file-descriptor layer is not reachable through a supported extension point:
  neither the emscripten `print`/`printErr` overlays nor a `preRun` hook fire for these
  writes on the Node host.
- Synchronous console input (`Console.ReadKey(bool)`, `Console.In.ReadLine()`) throws a
  pointed `NotSupportedException` directing callers to `runInteractive` plus the async APIs.

[Unreleased]: https://github.com/VladimirReshetnikov/Carbide/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/VladimirReshetnikov/Carbide/releases/tag/v0.1.0
