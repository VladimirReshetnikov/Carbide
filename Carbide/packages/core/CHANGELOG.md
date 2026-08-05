# Changelog — `@carbide/core`

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
package follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The exported
surface frozen by this release is recorded in
[`api/carbide-core.api.md`](../../../api/carbide-core.api.md).

## [Unreleased]

### Added

- **Source generators (M12).** `CarbideSession.addAnalyzer(bytes, name?)` registers a Roslyn
  source-generator assembly and returns an `AnalyzerHandle`; `Project.addAnalyzer(handle)`
  attaches it, and every subsequent `getDiagnostics`, `build`, `run`, and `runInteractive`
  runs the generator and folds its output into the compilation. `CarbideSession.removeAnalyzer`
  detaches it again.

  Both `IIncrementalGenerator` and `ISourceGenerator` are supported, including
  post-initialization output and syntax providers that consult the semantic model. Generators
  are compile-time tools: the generator assembly is never added to the compilation's
  references, so its own types stay invisible to the compiled program, and a generator
  attached to one project does not affect its siblings.

  A DLL carrying no usable generator is refused at `addAnalyzer` rather than accepted and
  silently contributing nothing. A generator that throws is reported as CS8785 by Roslyn's own
  driver; a driver-level failure is reported as `CARBIDE_GEN001`. Carbide does not pre-screen
  generators for filesystem or network access — there is no reliable static test for it, and
  the browser runtime has no filesystem to reach, so an offending generator surfaces as a
  diagnostic naming its exception.

- **Diagnostic analyzers (M12).** The same `addAnalyzer` surface runs `DiagnosticAnalyzer`
  implementations: Carbide tells generators and analyzers apart by what the assembly contains,
  and an assembly may carry both. Analyzers run over the generated compilation and their
  diagnostics join the compiler's own, so an analyzer rule at error severity fails a `build`
  exactly as a compiler error does.

  An analyzer that throws is reported as `CARBIDE_GEN002` and a failure of the driver itself
  as `CARBIDE_GEN003` — both warnings, so one broken rule costs its own results rather than
  the build. Analyzers run with `concurrentAnalysis: false`: a single-threaded runtime has no
  parallelism to win. Both settings were measured on Node and in headless Chromium and both
  work, so that is a conservative default rather than a workaround.

  Not covered: code-fix providers (Carbide has no editor to apply a fix in) and `<Analyzer>`
  items declared in a `.csproj`. Generators and analyzers shipped inside NuGet packages *are*
  covered — see the `@carbide/nuget` changelog.

### Fixed

- **Interactive runs after the first wrote into the first run's terminal.** Mono-WASM caches
  the function object a `[JSImport]` resolves to, and the bridge published a fresh
  `Carbide.Terminal.write` per run, so the C# side kept calling the first run's closure. A
  host showed a working first terminal and permanently silent ones after it, while every
  `RunResult.stdOut` stayed correct. The bridge members are now installed once with stable
  identities, and `runInteractive` swaps the sink behind them.
- **`CarbideSession.shutdown()` mid-run left a program parked at a prompt.** Session teardown
  dropped the project entries and disposed the Roslyn workspace without releasing the run, so
  `exitPromise` never resolved. It now performs the same interactive teardown
  `TerminalSession.dispose()` already did. `dispose()` itself was unaffected.
- **Disposing a run suspended in `CarbideConsole.ReadKeyAsync` froze the page.** The key-mode
  wait completes immediately once input closes, and the read loop had no end-of-input check,
  so it spun — which on single-threaded Mono-WASM starves the JS event loop entirely. The read
  now ends: `OperationCanceledException` when the run is being torn down,
  `InvalidOperationException` when input merely ran out.
- **A newline-less prompt on `Console.Error` stayed invisible while the program waited for
  input.** Only `Console.Out` was flushed before an input suspension, so the prompt appeared
  retroactively — after the input that answered it. Both streams are flushed now.
- **`Console.CancelKeyPress` handlers leaked between runs.** The forked `System.Console` holds
  the handler chain in a static field that outlives a run's collectible `AssemblyLoadContext`,
  so a handler left behind that set `e.Cancel = true` silently vetoed Ctrl+C for every later
  run on the page. Every run path now clears the chain at teardown.
- **`Console.OpenStandardOutput()` corrupted multi-byte characters split across writes.** Each
  write was decoded with a stateless `Encoding.UTF8.GetString`, so a UTF-8 sequence spanning
  two calls became two invalid fragments. The stream now carries a stateful `Decoder`, and
  resolves any dangling partial sequence when it is disposed. `Console.Write` goes through the
  `StreamWriter` path and was never affected.

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

- **Webcil packaging (M8).** `<WasmEnableWebcil>true</WasmEnableWebcil>`: every managed
  assembly in `_framework/` now ships as a `.wasm` file wrapping a Webcil image, so a
  deployment no longer serves any `.dll` content type — the CDN and corporate-proxy problem
  that motivated Webcil. Carbide reads the container itself for compile-time metadata:
  Webcil *replaces* the PE headers rather than wrapping them, so `PEReader` and
  `MetadataReference.CreateFromImage` cannot be used. The new reader unwraps the WebAssembly
  module, walks the Webcil section table, and builds the reference from the ECMA-335
  metadata root. This is what keeps browser compilation working at all: `BrowserHostAdapter`
  has no ref pack, so the browser always takes the runtime-assembly path.

### Fixed

- **A user-declared synchronous `Main` is no longer displaced by an unrelated async
  sibling.** T2.1 added a substitution so that Roslyn's *synthesised* wrapper for
  `async Task Main` — which deadlocks on single-threaded Mono-WASM — is bypassed in favour of
  the underlying async method. The search ran for every non-awaitable entry point, though,
  including a real `static void Main`: a single unrelated helper such as
  `static async Task WarmUpAsync()` in the same class was invoked *instead of* `Main`, with
  `success: true`, empty stdout, and no diagnostic. `carbide build` emitted a PE whose entry
  point was `Main` while `carbide run` on the same sources executed something else, and the
  `Task<int>` variant replaced the program's exit code. The substitution now applies only when
  the CLR's entry point is compiler-generated, which is exactly the case it was written for.
  Candidate ordering is also pinned, since `Type.GetMethods` order is unspecified.
- **`CarbideConsole` and stock `Console` no longer disagree about the terminal's size.** The
  TS layer pushes the initial geometry with a priming `NotifyResize` *before* the run's
  `TerminalInputState` exists, so the value was dropped and `Cols`/`Rows` kept their 80×24
  placeholders for the whole run unless the user happened to resize. The T3-forked
  `Console.WindowWidth` asks xterm directly and was correct, so in a 120×40 terminal
  `CarbideConsole.WindowWidth` returned 80 while `Console.WindowWidth` returned 120. The
  state now seeds from the same live source the fork uses, which removes the ordering
  dependency instead of trying to sequence the two calls. Every interactive fixture built its
  mock at exactly 80×24 — the same value as the fallback — so the suite could not distinguish
  "delivered" from "dropped"; a new 120×40 fixture now covers it.
- **Shift+Tab is decoded as Shift+Tab, not Shift+F2.** The terminfo shim omitted the back-tab
  entry (`kcbt`, `CSI Z`), so xterm.js's Shift+Tab fell through to `KeyParser`'s SCO-style
  single-letter branch, where `Z` maps to F2. Reverse-tab-order navigation in a user's TUI
  silently triggered whatever F2 was bound to — a wrong key rather than an unrecognised one.

### Changed

- **`ProjectOptions.languageVersion` validates.** A value Roslyn cannot parse used to fall
  through to the default, so `"lastest"` compiled as if nothing had been asked for.
  `createProject` now throws with the accepted spellings listed, matching `csc`'s CS1617.
- **`ProjectOptions.targetFramework` and `.rootNamespace` are documented for what they
  actually do.** `targetFramework` selects a NuGet package's `lib/<tfm>/` folder but never
  the compile-time reference set, which is always net10.0; `rootNamespace` is carried but has
  no compiler effect, because C# has no compiler-level root namespace. Neither behaviour
  changed — both were previously undocumented on the public type.
- `Microsoft.Extensions.Logging.Abstractions` (and the
  `Microsoft.Extensions.DependencyInjection.Abstractions` it pulls in) moved from
  `10.0.0-preview.5.25277.114` to the stable `10.0.6`. Both ship as assemblies inside the
  `_framework` payload, so a published Carbide was carrying preview builds of a released
  library. The vendored upstream notices moved with them
  (`third-party/dotnet-extensions-preview/` → `third-party/dotnet-extensions/`), and
  `scripts/check-publish.mjs` now fails on any prerelease `PackageReference` in a project
  whose output ships.

### Notes

- Session lifetime is now pinned by tests: `shutdown()` is idempotent, a shut-down session
  refuses to create projects or register references, shutdown invalidates outstanding
  reference handles, and work already in flight still completes. Every other suite calls
  `shutdown()` in teardown, so the happy path was exercised constantly while these
  properties — the ones callers actually lean on — were asserted nowhere.

- Source generators and analyzers are not supported.
- The `@carbide/refs-net10.0` pack still ships plain PEs. Shipping it in Webcil form as well
  would need a PE→Webcil converter in its build for a case no host exercises: Node reads the
  pack from disk, and the browser does not consume it.
- The `MSCAP00*` advisories report the bypass rather than capturing the escaped bytes. The
  runtime's file-descriptor layer is not reachable through a supported extension point:
  neither the emscripten `print`/`printErr` overlays nor a `preRun` hook fire for these
  writes on the Node host.
- Synchronous console input (`Console.ReadKey(bool)`, `Console.In.ReadLine()`) throws a
  pointed `NotSupportedException` directing callers to `runInteractive` plus the async APIs.

[Unreleased]: https://github.com/VladimirReshetnikov/Carbide/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/VladimirReshetnikov/Carbide/releases/tag/v0.1.0
