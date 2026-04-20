# Carbide — xterm.js interactive console plan (Phases T1–T4)

- Created (UTC): 2026-04-19T23:34:41Z
- Repository HEAD: e9e9e23e1e146a2509a6e1cf7dc0b61b81090977

Status: parent implementation plan for the xterm.js-backed interactive browser terminal feature. Written on top of the two feasibility reports that established the verdict and scope ceiling:

- [`reports/carbide-xterm-interactive-console-feasibility`](../reports/carbide-xterm-interactive-console-feasibility__2026-04-19__21-55-15-000000.md) — primary feasibility evaluation (mine).
- [`reports/carbide-browser-xterm-console-feasibility`](../reports/carbide-browser-xterm-console-feasibility__2026-04-19__22-01-41__06bf6d9b78c7.md) — independent second opinion; primary source for the T3 "fork `System.Console.dll`" path and the third-party-DLL scope ceiling that bounds T2.

Read the feasibility report first. This plan assumes the reader knows the `Console.SetOut`/`Console.SetError` work but `Console.SetIn` throws on browser (Carbide already reflection-patches it); that `ConsolePal.Browser.cs` throws PNS for most interactive APIs; that `Console.WriteLine` bytes already transit through a `TextWriter` we own; and that xterm.js is a pure byte-pipe plus ANSI parser on the JS side.

Audience: repository owner Vladimir, and future contributors picking up phases.

Scope: extend `src/Carbide` so browser-hosted C# console programs can drive an embedded xterm.js terminal with behavior that approximates `conhost.exe` on modern Windows (VT-enabled) — including live stdout/stderr streaming, ANSI passthrough, line-mode stdin, key-mode stdin, `Console.ForegroundColor` et al., `Console.WindowWidth`, `Console.SetCursorPosition`, `Console.Title`, `Console.Clear`, and Ctrl+C semantics. Phases T1–T2 cover the fast path; T3 is the runtime workstream that buys pre-compiled-library compatibility; T4 is compat-test hardening.

Band labelling: T1 and T2 are Band A (additive surface in `@carbide/core`, no runtime fork). T3 is Band C (runtime workstream — a new sibling BCL csproj inside Carbide's `_framework/` ship). T4 is Band A-to-B depending on what T3 scope lands.

## 1. Context

### 1.1 What the feasibility reports settled

- **xterm.js is not the limiting factor.** It already provides VT parsing, SGR, alt screen, mouse, cursor, resize, scrollback, and parser hooks. Carbide is a byte pipe; xterm.js does the rendering.
- **Mono-WASM browser `System.Console` is.** `ConsolePal.Browser.cs` throws PNS for `Console.In`, `ReadKey`, `TreatControlCAsInput`, `ForegroundColor`/`BackgroundColor`/`ResetColor`, `CursorSize`/`CursorVisible`/`GetCursorPosition`/`SetCursorPosition`, `Title`, `Beep`, `BufferWidth`/`Height`, `WindowWidth`/`Height`, `SetBufferSize`, `SetWindowSize`, `SetWindowPosition`, `InputEncoding` getter. `Console.SetOut` / `Console.SetError` are clean; `Console.SetIn` is PNS-guarded but Carbide already reflection-patches `s_in`/`_in`.
- **Mono-WASM main-thread single-threaded model is the central execution constraint.** Synchronous blocking reads (`Console.ReadLine()`, `Console.ReadKey()`) on the main thread deadlock the xterm event pump. Workarounds in increasing order of infrastructure cost: (a) cooperative async — user code `await`s a Carbide reader; (b) Mono-WASM's `[JSImport]` + `Task<T>` (same mechanism as (a), with the asyncify unwind hidden behind the await); (c) worker-hosted Mono-WASM + `SharedArrayBuffer` + `Atomics.wait` (needs COOP/COEP host-page headers).
- **Tier 2 has a scope ceiling.** `CarbideConsole.*Async` only reaches code Carbide compiles from source. Pre-compiled NuGet libraries (Spectre.Console, ReadLine.NET, Serilog.Sinks.Console, …) call `Console.ReadKey` / `Console.ForegroundColor` directly from their own IL, and a sibling `CarbideConsole` static can't intercept that. The honest answer to "arbitrary console libraries work" is a forked `System.Console.dll` — Phase T3.
- **Carbide already ships `System.Console.dll`** as a standalone managed assembly in `packages/core/src/bin/Release/net10.0/publish/wwwroot/_framework/`. Replacing it with a VT-first fork that reuses `ConsolePal.Unix` + `KeyParser` + `StdInReader` + `TerminalFormatStrings` is strictly cleaner than an IL weaver over user assemblies.

### 1.2 Relationship to existing Carbide phases

- Builds on **U1** (CLI UX sharpening — verbosity, sentinel-framed output, error taxonomy) and **U2** (program argv/stdin forwarding). U2's `RunOptions { args, stdin }` and the `Console.In` reflection-patch are the exact seams T1/T2 extend.
- Independent of **M10–M12** (Band C stretch). Source generators / analyzers / Webcil are not required for T1–T3; T4 may touch compat-test packaging but doesn't depend on M10+.
- **`System.Console.dll` fork in T3** is a new Band C entry. Treat as an M-series-scale effort; the plan calls it T3 because it belongs to the terminal feature's phasing rather than to the independent roadmap sequence.

### 1.3 Shape of the work

```text
  ┌───────────── Phase T1 ─────────────┐
  │  Streaming output + terminal        │
  │  session API. `runInteractive`,     │
  │  writers, emscripten print override │
  └───────────┬────────────────────────┘
              ▼
  ┌───────────── Phase T2 ─────────────┐
  │  Cooperative async input +          │
  │  CarbideConsole. Line + key mode.  │
  │  Resize + Ctrl+C + Clear + color.  │
  └───────────┬────────────────────────┘
              ▼
  ┌───────────── Phase T3 ─────────────┐  (optional, conditional on scope)
  │  Forked System.Console.dll for     │
  │  browser-wasm. Reuse ConsolePal.   │
  │  Unix. Pre-compiled-library parity. │
  └───────────┬────────────────────────┘
              ▼
  ┌───────────── Phase T4 ─────────────┐
  │  Compat tests and hardening:       │
  │  Spectre.Console, ReadLine.NET,    │
  │  alt screen, mouse, isolation,     │
  │  regression net.                    │
  └────────────────────────────────────┘
```

T1 and T2 can land on the trunk independently of T3/T4. The T2 → T3 decision is deferred to a phase-0 gate (§3.0 below) — if pre-compiled-library coverage is not in scope, T3 is skipped, T4 runs at reduced compat-fixture scope, and the plan ends there.

## 2. Invariants (hold across all phases)

These are the non-negotiables that every phase preserves; each phase's acceptance re-asserts them.

- **T-I1. `Project.run()` is unchanged.** The existing non-interactive, buffered-capture run path is preserved byte-for-byte. Interactive mode is strictly a new API (`runInteractive`), not a mode flag on `run`. Existing `@carbide/core` tests stay green.
- **T-I2. Interop schema bumps are forward-only with a transition window.** The C# validator accepts the previous and new schema versions simultaneously so a partially-updated tree (TS built against schema N+1, C# still on schema N) does not hard-fail during bring-up.
- **T-I3. No new runtime-global mutable state on `CarbideSession`.** Terminal bindings live on the project, not the session; a session can have at most one active interactive run per project. Multi-session processes (the Node acceptance fixtures do this) stay independent.
- **T-I4. The Node host does not grow a terminal path.** Node is the CLI/automation host; interactive terminal APIs are browser-only. Calling `runInteractive` from a `NodeHostAdapter`-backed session throws a clear "browser-only" exception up front.
- **T-I5. ANSI escape passthrough must be transparent.** Bytes the user program writes — including arbitrary CSI/SGR/OSC — reach `terminal.write` unchanged. Carbide does not parse, filter, or rewrite user output.
- **T-I6. `Console.OpenStandardOutput()` stops being a bypass.** Starting in T1, the emscripten `print`/`printErr` overrides catch runtime-side writes; user code that calls `Console.OpenStandardOutput()` and writes bytes reaches the same terminal as `Console.WriteLine`. (This also closes U1's stdout-bypass footnote for free inside interactive mode.)
- **T-I7. Ctrl+C is a first-class input signal.** User programs get a policy knob (byte vs event delivery) that matches conhost: `TreatControlCAsInput == true` → bytes in stdin; otherwise → `CancelKeyPress` fires and the run's `CancellationToken` trips.
- **T-I8. Teardown is deterministic.** Disposing a `TerminalSession` unwires JS bridges, restores `Console.Out`/`Console.Error`/`Console.In`, and removes `print`/`printErr` overrides in a fixed order. No "await the GC" pattern.
- **T-I9. No `System.Console.dll` fork until T3 explicitly lands.** T1/T2 only touch `Carbide.Core.csproj` and the TS side; the stock published `System.Console.dll` stays intact. When T3 lands, it's a separate sibling csproj whose output replaces `System.Console.dll` in `_framework/` at publish time.
- **T-I10. xterm.js is a peer dependency, never a direct dependency.** The host page chooses versions; Carbide's `package.json` lists `@xterm/xterm` as a peer so bundlers don't double-resolve.

## 3. Phase gates and dependencies

### 3.0 Phase-0 decision (before any phase starts)

Non-engineering decision, owner-call. Documented as a single line in `drift/README.md` at the start of work:

> Does the terminal feature need to work transparently with pre-compiled NuGet libraries that call `Console.ReadKey` / `Console.ForegroundColor` / `Console.WindowWidth` directly (Spectre.Console, ReadLine.NET, Serilog.Sinks.Console, etc.)?

- **If no** → T1 + T2 is the full ship. T3 is deferred indefinitely. T4 runs with the tier-2 compat set (original user source only).
- **If yes** → T1 + T2 are stepping stones; T3 is the real deliverable. T4 runs with the full compat set (tier-2 + pre-compiled-library fixtures).

Both branches of this gate are valid; there is no "correct" answer. The decision only affects the stop point.

### 3.1 Inter-phase dependencies

- T1 depends on: none inside this plan.
- T2 depends on: T1's `TerminalSession` API, `StreamingStdOutWriter`, and `[JSImport]` surface. The T2 reader consumes the `[JSImport]`-backed bridge T1 establishes.
- T3 depends on: T1 (JS bridge exists) + T2 (key parser, line editor, resize cache all feed the forked ConsolePal). Can start in parallel with T2's later half only if the forked `System.Console.dll` is kept behind a feature flag (`CARBIDE_TERMINAL_FORKED_BCL=1`) during bring-up.
- T4 depends on: all prior phases that landed.

## 4. Phase T1 — streaming output + terminal session API

**Goal.** First working browser-interactive demo. The user writes `Console.WriteLine("\x1b[1;33mhello\x1b[0m");` in the editor; the text renders in xterm.js yellow-bold as the program runs. Also closes the long-standing U1-era "raw bytes leak to browser devtools console instead of the captured stream" footgun.

### 4.1 Acceptance

**TS surface.**

- New `project.runInteractive(options)` returns a `TerminalSession` handle. `options.terminal` is the xterm.js `Terminal` instance (externally constructed + `open()`ed); Carbide does not create or mount the DOM.
- `TerminalSession.exitPromise` resolves to a `RunResult`-shaped object when the entry point returns or throws.
- `TerminalSession.dispose()` tears down bridges even before exit. Idempotent.
- Calling `runInteractive` from a Node-backed session throws immediately: `"Interactive terminals require the browser host adapter."`

**Output semantics.**

- `Console.Write('x')` in the user program reaches `terminal.write("x")` within a bounded flush window (buffered on C# side; flush threshold ~32 ms or ~4 KB, whichever comes first).
- `Console.WriteLine("hello")` reaches `terminal.write("hello\n")`.
- `Console.Error.WriteLine("boom")` reaches `terminal.write(stylizedWrap("boom\n"))` where `stylizedWrap` applies the caller-chosen SGR (`plain` / `dim` / `red`; default `plain`).
- ANSI bytes in user output (`Console.Write("\x1b[31mred\x1b[0m")`) reach xterm.js unchanged.
- **`Console.OpenStandardOutput()` bypass is closed.** Raw byte writes through the `BrowserConsoleStream` — and raw writes from the runtime itself — also reach xterm.js (not the browser devtools console). This is achieved via emscripten `print`/`printErr` overrides wired at boot.

**Input.** None in T1. `Console.In` is disconnected in interactive mode just as it is in `project.run()` today. `Console.ReadLine()` returns `""` if reached (pre-seeded empty reader), matching `project.run()`'s default behaviour.

**Non-functional.**

- No regression to `project.run()`. Its tests stay green.
- Node + NodeHostAdapter paths are untouched.
- The existing browser smoke-fixture set (`hello.html`, `multi-document.html`, `build-roundtrip.html`, `user-reference.html`) stays green; one new fixture is added.
- Teardown (`session.dispose()`) is deterministic and leaves no dangling `[JSImport]` listeners.

### 4.2 Execution order

**T1.1 — Interop schema + TS types.** Bumps `SCHEMA_VERSION` from 3 → 4. Adds `RunInteractiveOptions`, `TerminalSessionHandle`, and the bridge shape (`globalThis.Carbide.Terminal.*`). Valid schemas: 3 or 4.

**T1.2 — C# streaming writer.** New `packages/core/src/Terminal/StreamingStdOutWriter.cs` — a `TextWriter` subclass with a pooled `char[]` buffer, time-bounded flush (monotonic timestamp), and a single `[JSImport]` call site per flush. Sibling `StreamingStdErrWriter.cs` forwards to `writeErr` with an optional SGR prefix/suffix.

**T1.3 — C# JS-bridge surface.** New `packages/core/src/Terminal/CarbideTerminalInterop.cs` holds the `[JSImport]` + `[JSExport]` pair. Imports: `write`, `writeErr`. Exports: `DisposeTerminal` (for future phases; stub in T1).

**T1.4 — C# interactive run path.** New `packages/core/src/Terminal/TerminalRun.cs` parallel to `ProjectCompiler.RunAsync`. Installs streaming writers, flips the bridge into "attached" mode, invokes the entry point, flushes, tears down.

**T1.5 — `CompilationInterop` JSExport.** New `RunInteractiveAsync(projectId, optionsJson)` returning a `Task<string>` (final JSON payload). The TS side already knows how to parse `RunResult` JSON; the interactive path reuses that shape with an added `invocation` field mirroring U2's CLI-trailer additive field.

**T1.6 — TS `TerminalSession`.** New `packages/core/src/ts/terminal/session.ts` with the minimal session abstraction: boot the bridge, `await` the C# exit promise, expose `dispose`. Initial bridge installs `globalThis.Carbide.Terminal = { write, writeErr }` pointing at the caller's xterm instance.

**T1.7 — Browser host adapter overlay.** `BrowserHostAdapter` grows a `resolveRuntimeConfigOverlays(): Promise<Partial<DotnetModule>>` method that returns `{ print, printErr }` when an interactive session is active. `bootRuntime` wires those into the `DotnetModule` before `create()`. When no interactive session is active, the overlay is absent and emscripten's defaults apply (matches today's behaviour).

**T1.8 — Browser test fixture.** New `packages/core/test/browser/interactive-hello.html` + `interactive-hello.spec.mjs` driving a simple program that emits text + ANSI SGR and asserting xterm's parsed buffer state through the `Terminal.buffer.active` API.

**T1.9 — Docs.** `packages/core/README.md` gains a "Browser interactive terminal" section; `docs/Carbide-Current-State-Guide.md` gains a "Interactive terminal (T1)" subsection; `docs/drift/README.md` gains a T1 entry.

### 4.3 Deliverables by layer

| Layer | File | Change |
|---|---|---|
| `@carbide/core` TS | `src/ts/interop/schema.ts` | Bump `SCHEMA_VERSION` to 4; add `RunInteractiveOptionsRequest`. |
| | `src/ts/terminal/index.ts` | **New.** Public exports: `runInteractive`, `TerminalSession`, `InteractiveRunOptions`. |
| | `src/ts/terminal/session.ts` | **New.** Session shell. |
| | `src/ts/terminal/bridge.ts` | **New.** Installs `globalThis.Carbide.Terminal.{write,writeErr}`; teardown. |
| | `src/ts/project.ts` | Add `runInteractive(options)`. No change to `run()`. |
| | `src/ts/host/browser/browser-adapter.ts` | Add `resolveRuntimeConfigOverlays()`; hold a nullable `_terminalSink`. |
| | `src/ts/host/adapter.ts` | Optional `resolveRuntimeConfigOverlays?(): Promise<Partial<DotnetModule>>`. |
| | `src/ts/host/node/node-adapter.ts` | Implement `resolveRuntimeConfigOverlays` as a no-op. |
| | `src/ts/runtime/boot.ts` | Consume the overlay; pass `print`/`printErr` into `builder.withConfig(...)`. |
| | `src/ts/runtime/dotnet-types.ts` | Extend `DotnetModule`/`MonoConfig` with `print?`, `printErr?`. Add `RunInteractiveAsync` to `CarbideInteropExports`. |
| | `src/ts/types.ts` | Export `InteractiveRunOptions`, `TerminalSession`. |
| `@carbide/core` C# | `src/CompilationInterop.cs` | Add `RunInteractiveAsync`. |
| | `src/Terminal/CarbideTerminalInterop.cs` | **New.** `[JSImport]` / `[JSExport]` pair. |
| | `src/Terminal/StreamingStdOutWriter.cs` | **New.** |
| | `src/Terminal/StreamingStdErrWriter.cs` | **New.** |
| | `src/Terminal/TerminalRun.cs` | **New.** Parallel run path. |
| | `src/Services/SessionSolutions.cs` | Expose `RunInteractiveAsync(projectId, options)`. |
| Tests | `packages/core/test/browser/interactive-hello.html` | **New.** Fixture page. |
| | `packages/core/test/browser/interactive-hello.spec.mjs` | **New.** Playwright: assert `Terminal.buffer.active` contents + SGR attrs. |
| | `packages/core/test/browser/interactive-ansi.spec.mjs` | **New.** Asserts SGR passthrough (red/bold/etc.). |
| | `packages/core/test/browser/interactive-openstdout.spec.mjs` | **New.** Asserts `Console.OpenStandardOutput().Write(...)` bytes reach xterm (closes the U1 bypass footnote). |
| Docs | `packages/core/README.md` | New "Browser interactive terminal" section. |
| | `docs/Carbide-Current-State-Guide.md` | New "Interactive terminal (T1)" subsection. |
| | `docs/drift/README.md` | T1 drift entry. |

### 4.4 Size budget (indicative)

- ~200–300 LOC C# (`StreamingStdOutWriter` + `StreamingStdErrWriter` + `TerminalRun` + interop surface).
- ~150–250 LOC TS (`session.ts` + `bridge.ts` + `terminal/index.ts` + adapter overlay).
- 3 new Playwright fixtures.
- 1 schema-version bump.
- No new npm packages.

### 4.5 Design decisions (D-series — T1)

- **DT-1. Separate API (`runInteractive`) rather than a `run({ interactive: true })` mode flag.** Matches T-I1. The existing `RunResult` type stays usable; the interactive callers get a richer session handle that `run()` can't return.
- **DT-2. xterm.js is a peer dep; Carbide never owns the `Terminal` instance.** Host page owns construction, DOM mounting, addons (Fit, Web-Links, WebGL), and teardown of the DOM node. Carbide owns the bridge. Matches T-I10.
- **DT-3. Output is time-buffered, not write-forwarded.** Per-char `Console.Write('x')` in a tight loop would otherwise cross the JS/WASM boundary N times. A ~32 ms / ~4 KB flush window keeps the boundary-crossing cost to a fraction of a millisecond per character in the worst case. The 32 ms figure is the xterm refresh cadence under normal load; humans don't notice it.
- **DT-4. `printErr` override is wired at boot time, not at interactive-session start.** The emscripten `print`/`printErr` knobs live on the `DotnetModule` config and are consulted once per runtime. A session-activated toggle would require reloading the runtime. Instead, the overlay always installs a multiplexer function; the multiplexer forwards to `console.log` when no terminal is attached, and to the attached terminal otherwise. One-time wiring, zero-cost when inactive.
- **DT-5. `StreamingStdErrWriter` applies SGR on the C# side, not in JS.** Keeps the JS bridge byte-agnostic; keeps parity with conhost's per-stream color default (cmd.exe gives stderr no color by default, but PowerShell-host paints it red — the `stderrStyle` option exposes both via ANSI SGR). The C# writer is the single chokepoint where the stream-identifier is known for sure.
- **DT-6. `invocation` field additive on the interactive `RunResult`.** Schema version bumps from 3 → 4 because the JSExport name is new, but the response shape is a superset of today's `RunResult` with `invocation: { args, stdinBytes, terminalKind: "xterm" }`. Existing parsers ignore unknown fields and stay working.
- **DT-7. Overlay method is optional on the host adapter interface.** `resolveRuntimeConfigOverlays?()` with `?` keeps the adapter interface backward-compatible; `bootRuntime` checks for presence before calling.

### 4.6 Risks (R-series — T1)

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| RT-1 | Mono-WASM's `[JSImport]` with string payload has non-trivial per-call overhead that the 32ms/4KB buffer hides but doesn't eliminate | Medium | Low | Pooled `char[]` buffer; single import call per flush; Playwright perf fixture emits 1MB of text and asserts < 300 ms wall-time. |
| RT-2 | xterm's `Terminal.write` returns before render; assertions on buffer state in a test race the render | Low | Medium | Use xterm's `onWriteParsed` or await `terminal.buffer.active.baseY` stabilization; don't assert synchronously after `session.exitPromise`. |
| RT-3 | Emscripten `print` override is invoked in contexts other than `BrowserConsoleStream` (runtime diagnostics, unmanaged asserts); these would pollute the terminal | Medium | Low | Multiplexer filters on a tag byte if Carbide wants to split user vs runtime; for T1, accept that the terminal receives both and document. This is strictly better than today's "bytes go to devtools". |
| RT-4 | `TerminalSession.dispose` races the entry point's final flush, losing tail output | Low | Medium | Dispose flow: (1) set a "tearing down" flag that blocks new writes; (2) await pending flush; (3) `Console.SetOut` back to the old value; (4) null the bridge pointer. The C# side unwinds in reverse. |
| RT-5 | Carbide's Playwright harness doesn't ship xterm.js, so fixtures break Playwright's browser download | Very low | Low | Vendor a pinned `@xterm/xterm` build under `packages/core/test/browser/vendor/` and load via `<script>`. No package change. |
| RT-6 | BrowserHostAdapter's new optional method breaks existing adapter implementers downstream | Very low | Medium | Interface method is optional (`resolveRuntimeConfigOverlays?`), and adapter abstract is unchanged. Node and Browser adapters implement explicitly; external implementers default to absent. |

### 4.7 Out of scope for T1

| Item | Owning phase |
|---|---|
| Any stdin | T2. |
| `Console.ReadKey` / line mode | T2. |
| `Console.ForegroundColor` et al. | T2. |
| `Console.WindowWidth` cache | T2. |
| Worker + SAB | T3 optional. |
| Third-party-library compat | T3. |

## 5. Phase T2 — cooperative async input + `CarbideConsole`

**Goal.** Make interactive terminals actually interactive. Line-mode `ReadLine`, key-mode `ReadKey`, color, cursor, window-size, title, clear, Ctrl+C. All via `CarbideConsole.*Async` and a `BrowserTerminalReader`. Covers original user source; pre-compiled libraries remain a T3 concern.

### 5.1 Acceptance

**`CarbideConsole` C# API.** New `Carbide.Terminal.CarbideConsole` exposes:

- `Task<string?> ReadLineAsync(CancellationToken ct = default)`
- `Task<ConsoleKeyInfo> ReadKeyAsync(bool intercept = false, CancellationToken ct = default)`
- `ConsoleColor ForegroundColor { get; set; }`
- `ConsoleColor BackgroundColor { get; set; }`
- `void ResetColor()`
- `int WindowWidth { get; }`, `int WindowHeight { get; }`
- `int BufferWidth => WindowWidth;`, `int BufferHeight => WindowHeight;` (browser-terminal buffer == window)
- `void SetCursorPosition(int left, int top)`
- `(int Left, int Top) GetCursorPosition()` — DSR-based; best-effort
- `bool CursorVisible { get; set; }` — DECTCEM
- `string Title { set; }` — OSC 0
- `void Clear()` — `\x1b[2J\x1b[H`
- `event EventHandler<(int Cols, int Rows)>? TerminalResized`
- `event ConsoleCancelEventHandler? CancelKeyPress`
- `bool TreatControlCAsInput { get; set; }`
- `void WriteRaw(string sequence)` — escape hatch

**`Console.In` via reflection-patched reader.** A `BrowserTerminalReader : TextReader` is installed into `Console._in` at session start (reuses Carbide's existing `SetConsoleInField` machinery). `Console.In.ReadLineAsync()` therefore works through the stock BCL path. Synchronous `Console.In.ReadLine()` / `Console.ReadLine()` / `Console.Read()` throw a pointed `NotSupportedException` with a message telling the user to use `Console.In.ReadLineAsync()` or `CarbideConsole.ReadLineAsync()`.

**Line mode.** A JS-side local-echo line editor (either `local-echo` MIT fork or ~150 LOC hand-roll) handles the UX: backspace, left/right arrows within the current line, Enter commits. Up/down arrow history is a stretch feature — ship a flag-gated stub that's either "no history" or "N-entry ring buffer"; don't block T2 on it.

**Key mode.** `CarbideConsole.ReadKeyAsync` returns a `ConsoleKeyInfo` with `.KeyChar` + `.Key` + `.Modifiers`. Implementation ports [`KeyParser.cs`](../../packages/core/../../lib/dotnet/runtime/src/libraries/System.Console/src/System/IO/KeyParser.cs) verbatim (~400 LOC), fed by raw bytes from xterm's `onData` (CSI `\x1b[A`, SS3 `\x1bOP`, modifier-encoded forms). The bridge's key-mode toggle stops the JS-side line editor from eating arrow keys; all bytes pass through.

**Colors + cursor + title + clear.** All implemented by emitting the right ANSI sequence on `Console.Out`. `ForegroundColor`/`BackgroundColor` caches the current `ConsoleColor` value and emits the matching SGR escape on set; `ResetColor` emits `\x1b[39;49m`. `SetCursorPosition(x, y)` emits `\x1b[<y+1>;<x+1>H`. `Title = ...` emits `\x1b]0;<title>\x07`. `Clear` emits `\x1b[2J\x1b[H`. `CursorVisible = false/true` emits `\x1b[?25l`/`\x1b[?25h`.

**Window size.** JS side pushes `cols`/`rows` across via `[JSExport] NotifyResize(cols, rows)` at boot *and* on every `terminal.onResize`. C# caches the values in a static; `CarbideConsole.WindowWidth`/`Height` returns them.

**Ctrl+C.** When `CarbideConsole.TreatControlCAsInput == false` (default), the JS bridge intercepts `\x03` from `onData` and calls `[JSExport] DeliverSignal("SIGINT")`. The C# side fires `CancelKeyPress` (synchronous handler chain) and trips the run's `CancellationToken`. When the flag is `true`, the bridge passes `\x03` through as an input byte.

**`Console.*` pointed errors.** Synchronous callers get a clear message pointing at the async variant. A helper `ConsoleSurfaceGuard.ThrowForSyncCall(string member)` produces consistent wording.

**Non-functional.**

- T1's fixtures stay green; T2 does not regress streaming-output behaviour.
- Node path untouched.
- `DocumentPath` ordering from the existing `ProjectCompiler` is preserved (T2 doesn't touch source-set machinery).

### 5.2 Execution order

**T2.1 — `BrowserTerminalReader`.** `packages/core/src/Terminal/BrowserTerminalReader.cs` — a `TextReader` backed by a `TaskCompletionSource<string?>`. JS pushes a full line via `DeliverStdIn`; the TCS resolves. Synchronous methods throw via `ConsoleSurfaceGuard`.

**T2.2 — JS-side line editor.** `packages/core/src/ts/terminal/line-editor.ts`. State machine: accumulate chars, handle backspace + left/right, on Enter flush to `DeliverStdIn`, write the echoed char back to `terminal.write` as typed (local echo). Toggled off when the C# side is in key mode.

**T2.3 — Key parser port.** `packages/core/src/Terminal/KeyParser.cs` — direct verbatim port of the upstream Unix `KeyParser`, with the `TerminalFormatStrings` dependency replaced by a minimal hard-coded xterm-capability table. Ships with the upstream unit-test set adapted to xUnit.

**T2.4 — `CarbideConsole`.** `packages/core/src/Terminal/CarbideConsole.cs` implements the static API listed in §5.1. All emission goes through `Console.Out` (which is T1's `StreamingStdOutWriter`). State (current fg/bg colors, cached cols/rows, cursor-visible, TreatControlCAsInput) lives in static fields owned by the `TerminalRun` lifetime.

**T2.5 — Resize + signal JSExports.** Extend `CarbideTerminalInterop` with:

```csharp
[JSExport] public static void DeliverStdIn(string projectId, string data);
[JSExport] public static void NotifyResize(int cols, int rows);
[JSExport] public static void DeliverSignal(string signalName);     // "SIGINT"
[JSExport] public static void SetKeyMode(bool enabled);              // JS → C# echo policy
```

**T2.6 — Bridge extension.** `session.ts` grows `writeInput(data)`, `resize(cols, rows)`, `deliverSignal(name)`, and the `onData`/`onResize` subscriptions on the xterm instance. The line editor and key-mode toggle drive `writeInput` at either per-line or per-byte granularity.

**T2.7 — Reflection-patched `Console.In`.** `TerminalRun` installs the `BrowserTerminalReader` via the same `SetConsoleInField` path U2 introduced. Restore in `finally`.

**T2.8 — `Console.*` guard shims.** A partial class on `Console` is not possible (sealed BCL surface); a Carbide-side diagnostic analyzer that warns on synchronous `Console.ReadLine`/`ReadKey` calls in Carbide-compiled source is a stretch. For T2, a helpful *runtime* message is enough: `BrowserTerminalReader.Read()` and `.ReadLine()` throw; `ConsolePal`'s `ReadKey` already throws PNS; we intercept the PNS at `CarbideConsole.ReadKeyAsync` — user code that hits `Console.ReadKey` from source Carbide compiles gets the PNS message with a pointer to `CarbideConsole.ReadKeyAsync`.

**T2.9 — Fixtures.** `interactive-readline.spec.mjs` (paste `"abc\n"` → `ReadLineAsync` returns `"abc"`), `interactive-readkey.spec.mjs` (arrow keys → `ConsoleKey.UpArrow` etc.), `interactive-color-api.spec.mjs` (`CarbideConsole.ForegroundColor = Red` → xterm attr), `interactive-resize.spec.mjs` (viewport resize → `WindowWidth` reflects), `interactive-ctrlc.spec.mjs` (send `\x03` → `CancelKeyPress` handler fires).

**T2.10 — Docs.** Current-state guide: T2 section listing the supported `CarbideConsole` surface and the PNS-translation rules for `Console.*`. Drift entry.

### 5.3 Deliverables by layer

| Layer | File | Change |
|---|---|---|
| `@carbide/core` C# | `src/Terminal/CarbideConsole.cs` | **New.** |
| | `src/Terminal/BrowserTerminalReader.cs` | **New.** |
| | `src/Terminal/KeyParser.cs` | **New.** Port from `lib/dotnet/runtime`. |
| | `src/Terminal/CarbideTerminalInterop.cs` | Extend with `DeliverStdIn`, `NotifyResize`, `DeliverSignal`, `SetKeyMode`. |
| | `src/Terminal/TerminalRun.cs` | Install reader; wire resize/signal; restore in `finally`. |
| | `src/Terminal/ConsoleSurfaceGuard.cs` | **New.** Shared sync-call error message helper. |
| | `src/Services/SessionSolutions.cs` | Plumb new JSExports. |
| | `src/CompilationInterop.cs` | Schema version 4 → 5. |
| `@carbide/core` TS | `src/ts/terminal/line-editor.ts` | **New.** |
| | `src/ts/terminal/key-encoder.ts` | **New.** JS-side byte → C#-ready payload (thin). |
| | `src/ts/terminal/session.ts` | Extend: `writeInput`, `resize`, `deliverSignal`, subscribe to `onData`/`onResize`. |
| | `src/ts/terminal/bridge.ts` | Extend `globalThis.Carbide.Terminal` with input-side functions. |
| | `src/ts/interop/schema.ts` | Bump to 5. |
| | `src/ts/types.ts` | Export new option types (e.g. `ctrlCMode`, `stderrStyle` were in T1; history-ring options added here). |
| `@carbide/refs-net10.0` | Terminal ref assembly | **New** sibling ref `.dll` exposing `Carbide.Terminal.CarbideConsole`. Lets user-source `using Carbide.Terminal;` resolve at compile time even though the implementation lives in `@carbide/core`. |
| Tests | `test/browser/interactive-readline.spec.mjs` | **New.** |
| | `test/browser/interactive-readkey.spec.mjs` | **New.** |
| | `test/browser/interactive-color-api.spec.mjs` | **New.** |
| | `test/browser/interactive-resize.spec.mjs` | **New.** |
| | `test/browser/interactive-ctrlc.spec.mjs` | **New.** |
| | `test/browser/interactive-title-clear.spec.mjs` | **New.** `Title`, `Clear`, `CursorVisible`. |
| | `test/node/key-parser.test.mjs` | **New.** `KeyParser` port unit tests (host-side). |
| Docs | `docs/Carbide-Current-State-Guide.md` | T2 section: `CarbideConsole` surface + PNS translation table. |
| | `docs/drift/README.md` | T2 entry. |
| | `packages/core/README.md` | "Interactive terminal (T2)" subsection. |

### 5.4 Size budget (indicative)

- ~500–800 LOC C# (`CarbideConsole` + `BrowserTerminalReader` + `KeyParser` port + `ConsoleSurfaceGuard` + interop additions).
- ~300–500 LOC TS (line editor + key encoder + session + bridge extensions).
- ~300 LOC ref-pack sibling for `Carbide.Terminal.CarbideConsole` (mostly auto-generatable from the implementation's public surface).
- 6 new Playwright fixtures + 1 Node unit-test file.
- 1 schema bump.
- 1 new `@carbide/refs-net10.0` sibling ref-DLL.

### 5.5 Design decisions (T2)

- **DT-8. Synchronous `Console.ReadLine` / `Console.ReadKey` throw instead of block.** Blocking the main thread deadlocks the xterm event pump (T-I requirement). A pointed exception with a migration target is strictly better UX than a hang.
- **DT-9. The line editor lives in JS, not C#.** Keyboard latency must be invisible; each keystroke round-tripping to C# for echo would cost ~1–2 ms in practice. JS-side local echo stays sub-frame. C# only sees the committed line.
- **DT-10. `KeyParser` is ported verbatim from upstream.** Reinventing xterm/VT key-escape decoding is a bug factory; the upstream implementation has years of production hardening and an excellent test set. Port + translate `TerminalFormatStrings` inputs; keep the algorithm intact.
- **DT-11. Window size is cached on the C# side, not queried live per access.** `CarbideConsole.WindowWidth` is a hot read in tight `while (width > 0)` loops. Caching + `[JSExport] NotifyResize` updates gives O(1) reads with bounded staleness (one update per real resize event).
- **DT-12. `GetCursorPosition` is best-effort, not required for T2 acceptance.** DSR `\x1b[6n` reply interleaves with user stdin and needs a pre-filter that intercepts `\x1b[<row>;<col>R` before it reaches `DeliverStdIn`. Implementable in T2 but non-trivial; if the DSR pre-filter doesn't land cleanly, ship `GetCursorPosition` throwing `NotSupportedException` and defer to T3.
- **DT-13. `TreatControlCAsInput` default is `false`.** Matches .NET on Windows and Unix. Programs written for a console get the event, not the byte, unless they opt in.
- **DT-14. `CancelKeyPress` is synchronous.** Matches the .NET BCL contract. The JS `DeliverSignal` call is fire-and-forget from JS's perspective; C# runs the handler chain synchronously on its current continuation.
- **DT-15. History ring is flag-gated.** Small-value feature that can bloat the line-editor scope if done generically. Default: no history. Flag: `{ lineEditor: { history: N } }` — simple integer ring size.
- **DT-16. `WriteRaw` is an escape hatch, not the happy path.** Advanced users who need a VT sequence `CarbideConsole` doesn't cover (alt screen, bracketed paste, specific cursor styles) can emit it directly without waiting for an API. Documented as "bring your own ANSI."
- **DT-17. Ref-pack ships a `Carbide.Terminal.ref.dll`.** Otherwise `using Carbide.Terminal;` from user source fails at Roslyn-compile time because Roslyn resolves metadata against the ref-pack, not the runtime assemblies.

### 5.6 Risks (T2)

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| RT-7 | `BrowserTerminalReader` is not installed soon enough for a user program that reads on the first line of `Main` | Low | Medium | Install is part of `TerminalRun`'s setup, before `entryPoint.Invoke`. Unit-test that asserts "first `Console.In.ReadLineAsync()` in a program receives a line pushed by the test harness". |
| RT-8 | JS-side line editor doesn't handle paste (multi-line data arrives in one `onData` callback) | Medium | Low | Paste delivers `\r` or `\n` separators — flush committed lines progressively as separators are seen; trailing partial line stays buffered. |
| RT-9 | `KeyParser` port subtly misbehaves for modifier-encoded forms (`\x1b[1;5A` = Ctrl+Up) | Medium | Low | Verbatim port + upstream tests; any test deltas are bugs to fix, not behavior to redesign. |
| RT-10 | `CarbideConsole.ForegroundColor` setter bloats output bytes (one SGR per assignment) | Low | Very low | That's how conhost and xterm-terminal programs work. Not a correctness issue. |
| RT-11 | Ref-pack build order complicates CI (core depends on terminal ref-pack DLL at compile time) | Low | Low | Terminal ref-pack is a sibling of `@carbide/refs-net10.0` that builds independently; no circular dep. |
| RT-12 | DSR `GetCursorPosition` reply is consumed by the user's `ReadLineAsync` | Medium | Low | Per DT-12, DSR pre-filter sits in the JS bridge between `onData` and `DeliverStdIn`; if the filter gets too invasive, ship `GetCursorPosition` as "not supported" and move on. |
| RT-13 | Ctrl+C byte path (`TreatControlCAsInput = true`) races the event path if the flag is flipped mid-run | Low | Low | The flag read is on the C# side, under a lock; one code path per byte. Flipping mid-byte can drop one signal; document. |
| RT-14 | `CancelKeyPress` handler chain throws and leaves the terminal in a weird state | Medium | Medium | Wrap the chain in a try/catch inside `CarbideTerminalInterop.DeliverSignal`; log the inner exception to `stdErrCapture`; tear down. |

### 5.7 Out of scope for T2

| Item | Owning phase |
|---|---|
| Pre-compiled-library compatibility | T3. |
| Worker + SAB for truly-sync `ReadKey` | T3 optional. |
| Alt-screen + mouse fixtures | T4 (user code can already do it via `WriteRaw`). |
| `Console.Beep` — no convenient browser equivalent | Dropped; document. |

## 6. Phase T3 — forked `System.Console.dll` (conditional)

**Goal.** `Console.ReadKey`, `Console.ForegroundColor`, `Console.WindowWidth`, `Console.SetCursorPosition`, `Console.Title`, `Console.Clear`, `Console.CancelKeyPress` all work on pre-compiled libraries that Carbide never sees source for. Strict parity for the supported subset. Optional worker + SAB for true-sync `ReadKey` behind a capability flag.

**Conditional.** Skipped entirely if the §3.0 gate decides pre-compiled-library coverage is not in scope.

### 6.1 Acceptance

**Forked `System.Console.dll` in `_framework/`.** Sibling csproj under `packages/core-bcl/System.Console/` that targets `browser-wasm`, produces a `System.Console.dll` with the same public surface as the stock BCL, and ships into `packages/core/src/bin/Release/net10.0/publish/wwwroot/_framework/` at publish time (replaces the stock file of the same name).

**Reused from upstream `ConsolePal.Unix` + helpers (no algorithmic reinvention):**

- `KeyParser` — move the T2 port into this csproj (or share it via linked-file).
- `StdInReader` — line-editing + buffered reads.
- `TerminalFormatStrings` — ANSI capability table.
- Cursor/window-size caches.
- SGR emission helpers.

**Browser-specific substitutes:**

- `read(0, ...)` → a `[JSImport]` that awaits a byte batch from the JS bridge.
- `write(1, ...)` / `write(2, ...)` → `[JSImport]` calls into the bridge (share T1 infrastructure).
- `ioctl(TIOCGWINSZ)` → the cached `cols`/`rows` plumbed into the static by `NotifyResize`.
- POSIX signal registration → replaced by explicit `[JSExport] DeliverSignal` handlers the C# side exposes; `SIGINT` → `CancelKeyPress` chain inside this assembly.
- Terminfo database discovery → baked-in xterm-256color table (hard-coded; matches what xterm.js emulates).
- Native terminal initialization → no-op at boot; lazy on first use.

**Public surface unchanged.** `Console.ReadKey(bool)` returns a real `ConsoleKeyInfo` synchronously from the program's perspective — in Option-A (cooperative async) mode it's not *really* synchronous from the runtime's perspective (the C# async state machine pumps while waiting), but the user program sees a blocking call returning a value. In Option-B (worker + SAB) mode it's truly synchronous. Feature flag `CARBIDE_TERMINAL_SYNC_READS` picks the behavior. Default: Option-A.

**No IL weaver over user code.** User code stays unchanged. The fork gets its effect by replacing the runtime assembly the user code links against.

**Third-party library fixtures pass:**

- `Spectre.Console` — renders a `Rule`, a `Tree`, a `Progress`, and a `Prompt.Ask<string>`.
- `ReadLine.NET` (Tornado-Andy's) — `ReadLine.Read()` with history + auto-completion.
- `Serilog.Sinks.Console` — color-themed output.
- `Sharprompt` — interactive prompts.
- `ConsoleTables` — tabular output with unicode borders.

### 6.2 Execution order

**T3.1 — Scaffold sibling csproj.** `packages/core-bcl/System.Console/Carbide.System.Console.csproj` + `Directory.Build.props` pin. Targets `browser-wasm`, `<AssemblyName>System.Console</AssemblyName>`, same strong-name/public-key as the stock BCL to stop the runtime complaining about identity. `PackageReference` to `Microsoft.NETCore.App.Ref` for compile-time metadata.

**T3.2 — Bring in upstream source.** Vendored or linked-file includes from `lib/dotnet/runtime/src/libraries/System.Console/src/` — `Console.cs`, `ConsoleKey*.cs`, `ConsoleModifiers.cs`, `ConsoleColor.cs`, `ConsoleCancelEventArgs.cs`, `ConsoleSpecialKey.cs`, `IO/StdInReader.cs`, `IO/KeyParser.cs`, `TermInfo*.cs` (minus the database-discovery pieces), `TerminalFormatStrings.cs`.

**T3.3 — Write `ConsolePal.Browser.cs`.** Replaces upstream's PNS-heavy stub with a real implementation modeled on `ConsolePal.Unix.cs`. Critically:

- `OpenStandardInput` / `OpenStandardOutput` / `OpenStandardError` return real `Stream`s backed by the JS bridge.
- `GetOrCreateReader` returns a `StdInReader`-backed reader.
- `ForegroundColor` / `BackgroundColor` / `ResetColor` emit ANSI SGR on `Out`.
- `BufferWidth`/`Height`, `WindowWidth`/`Height` return the cached values.
- `SetCursorPosition`, `CursorVisible`, `Title`, `Clear` emit the relevant ANSI.
- `ReadKey(bool)` calls into `StdInReader.ReadKey` — which uses `KeyParser` internally and reads bytes from the JS bridge.
- `TreatControlCAsInput` honored; signal-vs-byte routing enforced here.

**T3.4 — JS-bridge primitive layer.** Carbide exposes a narrow JS bridge that *this* assembly calls. Concretely: the assembly's `[JSImport]`s resolve against the same `globalThis.Carbide.Terminal.*` surface T1 established — the bridge is the same; the consumer is the forked BCL instead of `CarbideConsole`.

**T3.5 — Publish plumbing.** `packages/core`'s `dotnet publish` step depends on `packages/core-bcl/System.Console` being built first. A `PreBuildEvent` (or MSBuild `BeforeTargets="ResolveAssemblyReferences"`) copies the fork's output over the stock `System.Console.dll` in the `_framework/` drop. Sanity-check hash at the end of publish: a smoke test asserts the shipped `System.Console.dll` is the fork, not the stock.

**T3.6 — `CarbideConsole` deprecation path.** With the fork active, most `CarbideConsole.*` members become thin redirects to `Console.*`. `CarbideConsole.ReadKeyAsync` remains (non-BCL surface; better ergonomics for `await`-aware callers). `CarbideConsole.WriteRaw` remains. Color/cursor/size APIs become `[Obsolete("Use Console.X.")]` thin wrappers over the forked `Console.*` — keep working for a transition, deprecated.

**T3.7 — Option-B (worker + SAB) behind a flag.** `CARBIDE_TERMINAL_SYNC_READS=1` causes `@carbide/core`'s boot path to spin Mono-WASM up in a Web Worker, back-channel stdin through a `SharedArrayBuffer` ring. Requires host page to have served with COOP/COEP headers; if the runtime detects the absence at boot, falls back to Option-A with a diagnostic warning. Gate sits at `bootRuntime`.

**T3.8 — Third-party library smokes.** Playwright fixtures that:

- Build a small `Spectre.Console`-using program, run it, assert the xterm buffer contains the expected glyphs / colors.
- Build a `ReadLine.NET` program, paste input, assert line editing works.
- Build a `Serilog.Sinks.Console` program, assert colored level prefixes render.

These fixtures depend on Carbide's NuGet allow-list accepting these packages or on pre-compiled DLLs vendored under `packages/core/test/fixtures/`.

**T3.9 — Docs.** Current-state guide: T3 section. Explain the fork, what it replaces, what invariants change (none on the user API surface; users observe that `Console.ReadKey` now works). Drift entry.

### 6.3 Deliverables by layer

| Layer | File | Change |
|---|---|---|
| New sibling csproj | `packages/core-bcl/System.Console/Carbide.System.Console.csproj` | **New.** |
| | `packages/core-bcl/System.Console/src/ConsolePal.Browser.cs` | **New.** Modeled on `ConsolePal.Unix.cs`. |
| | `packages/core-bcl/System.Console/src/Interop.JS.cs` | **New.** `[JSImport]`s for read/write/resize/signal. |
| | (vendored) `packages/core-bcl/System.Console/src/ported/...` | Source-linked from `lib/dotnet/runtime/src/libraries/System.Console/src/`. |
| `@carbide/core` C# | `src/Terminal/TerminalRun.cs` | Toggle between tier-2 bridge and tier-3 bridge based on `CARBIDE_TERMINAL_FORKED_BCL`. |
| | `src/Terminal/CarbideConsole.cs` | Add `[Obsolete]` on duplicated members; keep `ReadKeyAsync`, `WriteRaw`. |
| Build pipeline | `packages/core/src/Carbide.Core.csproj` | Depends on the sibling project; publish step replaces `_framework/System.Console.dll`. |
| | `packages/core/scripts/check-fork-shipped.mjs` | **New.** Post-publish smoke. |
| TS | `src/ts/runtime/boot.ts` | Feature-flag `CARBIDE_TERMINAL_SYNC_READS`; worker-boot alternate path. |
| | `src/ts/terminal/worker-bridge.ts` | **New.** COOP/COEP + SAB + worker message glue (only when SYNC flag is on). |
| Tests | `test/browser/interactive-spectre-console.spec.mjs` | **New.** |
| | `test/browser/interactive-readline-net.spec.mjs` | **New.** |
| | `test/browser/interactive-serilog-console.spec.mjs` | **New.** |
| | `test/browser/interactive-sharprompt.spec.mjs` | **New.** |
| | `test/browser/interactive-console-tables.spec.mjs` | **New.** |
| | `test/browser/interactive-console-readkey-sync.spec.mjs` | **New.** Only runs under COOP/COEP test harness. |
| | `test/node/system-console-fork-smoke.test.mjs` | **New.** Asserts the fork replaced the stock in `_framework/`. |
| Docs | `docs/Carbide-Current-State-Guide.md` | T3 section: what the fork replaces; compat matrix. |
| | `docs/drift/README.md` | T3 entry. |

### 6.4 Size budget (indicative)

- ~2–4k LOC net across the sibling csproj: most is source-linked from `lib/dotnet/runtime/...`; the hand-written piece is the new `ConsolePal.Browser.cs` (~600–1000 LOC) and the JS-bridge primitive `[JSImport]` surface (~100–200 LOC).
- ~300 LOC of build-pipeline plumbing (publish-step copy, post-publish smoke).
- ~400 LOC of TS for the optional worker + SAB boot path (only if T3.7 lands).
- 6–8 new Playwright compat fixtures.
- One new sibling csproj in the tree; no new npm package (the fork ships inside `@carbide/core`).
- One optional gated capability (worker mode).

### 6.5 Design decisions (T3)

- **DT-18. Fork `System.Console.dll`, not user-code IL.** Independent report's framing is decisive. Forking the BCL assembly is less invasive than an IL weaver; it preserves user debugging, doesn't touch user assemblies, and gives third-party libraries coverage for free.
- **DT-19. Strong-name and public-key match the stock BCL.** Otherwise Mono-WASM's assembly-identity resolution complains and user code compiled against the ref-pack's `System.Console` metadata refuses to bind at runtime.
- **DT-20. Source-link from `lib/dotnet/runtime`, don't copy.** Lets us re-sync with upstream on BCL minor revisions without a manual merge. MIT license is compatible. The specific files (`Console.cs`, `KeyParser.cs`, etc.) are stable surfaces.
- **DT-21. The JS bridge is shared with T1/T2.** Don't build a second `globalThis.Carbide.Terminal.*` for the fork. Same bridge, additional consumer. Keeps the JS surface minimal and keeps `CarbideConsole` and `System.Console` in sync by construction.
- **DT-22. Option-A (cooperative async) is the default; Option-B (worker + SAB) is a capability flag.** COOP/COEP headers are a hosting concern that breaks iframe embeds, analytics, and cross-origin scripts. Default-off is the responsible choice. The flag is for embedders who have full control over their host page.
- **DT-23. `CarbideConsole.*` stays, but most members become obsolete.** Existing user code written against T2's `CarbideConsole.ForegroundColor` keeps compiling and running; an `[Obsolete]` warning points at `Console.ForegroundColor`. `CarbideConsole.ReadKeyAsync` stays non-obsolete because it's an async-first API that the BCL doesn't provide.
- **DT-24. Post-publish smoke asserts the fork shipped.** Catches a broken build-order, a missing replace-step, or a publish that regresses to the stock file. Cheap; preventive.
- **DT-25. Terminfo is hard-coded to xterm-256color.** xterm.js emulates xterm-256color. Runtime discovery of terminfo is pointless here; the fixed table keeps the fork's code path simple and deterministic.
- **DT-26. `CancelKeyPress` and `TreatControlCAsInput` live inside the fork, not in `@carbide/core`.** They're semantics of the signal path, not of the terminal infrastructure. `CarbideConsole.CancelKeyPress` becomes a forwarder to `Console.CancelKeyPress`.

### 6.6 Risks (T3)

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| RT-15 | Strong-name identity mismatch prevents Mono-WASM from loading the fork | High | High | Explicit KeyFile pinned to the BCL's public key; unit test asserts `Assembly.GetName().GetPublicKeyToken()` matches stock. The BCL public key is public; no secret needed. |
| RT-16 | `PackageReference` to `Microsoft.NETCore.App.Ref` is refused by Carbide's NuGet allow-list | Low | Low | This csproj is built outside Carbide's bounded resolver (it's part of `@carbide/core`'s own build, same path `Carbide.Core.csproj` uses). No conflict. |
| RT-17 | Source-linking breaks on upstream file rename / restructure | Medium | Medium | Pin a specific commit hash in the source-link paths; treat `lib/dotnet/runtime` updates as a deliberate refresh step, not automatic. |
| RT-18 | Mono-WASM's linker trims the forked `System.Console.dll` members the test fixtures don't exercise | Medium | High | `<PublishTrimmed>false</PublishTrimmed>` on the sibling csproj; mirror `@carbide/core`'s trim-off discipline. |
| RT-19 | Worker + SAB boot detects COOP/COEP absent but Carbide doesn't gracefully fall back | Low | Medium | Boot checks `crossOriginIsolated` on `window`; if false, the `CARBIDE_TERMINAL_SYNC_READS=1` request is refused with a diagnostic and T3 falls to Option-A. |
| RT-20 | Spectre.Console's resize behavior assumes SIGWINCH is available | Medium | Low | Spectre.Console polls `Console.WindowWidth` per render frame; the cached value + `NotifyResize` push is enough. Spectre.Console doesn't actually register a signal handler on any platform. |
| RT-21 | `Console.ReadKey(intercept: true)` and xterm's `Terminal.onData` have off-by-one timing: the echo happens before the intercept flag is seen | Low | Low | Key-mode echo is suppressed at the JS line-editor layer for the duration of a pending `ReadKey`; the intercept flag is the same signal that disables local echo. |
| RT-22 | BCL minor revision lands an `internal` API change that breaks source-linked files | Medium | Medium | Vendor tags pin to specific revisions; re-sync is an explicit step with a test pass. |

### 6.7 Out of scope for T3

| Item | Reason |
|---|---|
| Real stdin pipe from outside the tab | Browser model forbids it; no filesystem/pipe. |
| Child processes from user code | Mono-WASM has no process model. |
| `Console.Beep(int, int)` — dual-frequency beep is non-ANSI | Drop; `Beep()` without args can emit `\x07` if anyone asks. |
| Screen reader / a11y beyond what xterm provides | Upstream xterm.js feature, not ours. |
| Win32 Console API parity (`MoveBufferArea`, separate buffer) | Browser terminal buffer ≠ Win32 console buffer; docs call this out. |

## 7. Phase T4 — hardening and compat tests

**Goal.** A regression net. If T3 didn't land, T4 runs at reduced scope against tier-2 source; if T3 did land, T4 is the compat-fixture stress phase.

### 7.1 Acceptance

- A stable set of browser smoke fixtures covers every `CarbideConsole` (and, post-T3, `Console`) method that T1/T2/T3 promised.
- A lifecycle-isolation contract is documented: what happens to the runtime when you dispose a `TerminalSession` and call `runInteractive` again on the same project? Same session, multiple sequential runs — what's the expected `Assembly.Load` accumulation behavior?
- Reference documentation for all supported ANSI sequences (inherited from xterm.js docs; our docs point out what Carbide's ConsolePal emits).
- Alt-screen and mouse-mode fixtures — user code can enter alt-screen, install mouse tracking, read mouse-event bytes through `Console.In.ReadAsync`. These work already via raw bytes in T1; T4 just adds the smoke tests.

### 7.2 Execution order

**T4.1 — Compat-fixture inventory.** Enumerate the required fixtures from T1 + T2 + (optionally) T3; cross-check against scenarios in the usability report.

**T4.2 — Isolation + lifecycle contract.** Document the "recreate the session between interactive runs" rule as a first-class contract in the current-state guide. Investigate Mono-WASM `AssemblyLoadContext` collectibility for a future removal of the rule; if collectibility works on interpreter mode, pull it into the `TerminalRun` teardown.

**T4.3 — Perf fixture.** A specific Playwright fixture that emits 5 MB of text and asserts the `terminal.buffer.active` reaches the expected last line within 2 s of `session.exitPromise` resolving. Guards against regressions in the flush-window tuning.

**T4.4 — Alt-screen + mouse fixtures.** Paste-through verification that `\x1b[?1049h`/`\x1b[?1049l` work, mouse enable/disable/mouse-byte-read-back, bracketed-paste.

**T4.5 — Release-note integration.** Runs at the end of every phase, not only T4, but codified here: every phase writes a drift entry + current-state-guide section + gets a release-note blurb. T4 checks the paper trail is complete.

**T4.6 — Docs pass.** Full end-to-end: current-state guide has a chapter on interactive terminal; README has a quickstart; architecture doc has the bridge's layer-ownership entry.

### 7.3 Size budget (indicative)

- ~6–10 new Playwright fixtures depending on T3 status.
- ~400 LOC of test plumbing (shared xterm-state-assertion helpers).
- Documentation-heavy; most of this phase is narrative and curation, not new code.

### 7.4 Risks (T4)

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| RT-23 | Assembly-load accumulation across many interactive runs shows up as a silent leak in long sessions | Medium | Medium | Document "recreate session between runs"; investigate collectible ALC; add a Playwright fixture that asserts bounded growth over ~50 sequential runs. |
| RT-24 | Alt-screen programs leak into the main screen on forced teardown | Low | Low | Teardown emits `\x1b[?1049l` unconditionally before tearing the bridge down. |

## 8. Cross-cutting concerns

### 8.1 Interop schema evolution

Bumps, chronologically:

- After U2: `SCHEMA_VERSION = 3` (the baseline this plan starts from).
- T1: `SCHEMA_VERSION = 4`. Adds `RunInteractiveAsync` JSExport; adds `{write, writeErr, DisposeTerminal}` bridge shape.
- T2: `SCHEMA_VERSION = 5`. Adds `{DeliverStdIn, NotifyResize, DeliverSignal, SetKeyMode}` JSExports.
- T3: `SCHEMA_VERSION = 6`. Widens the bridge with read/write primitives the forked BCL consumes. Only bumps if T3 lands.

Each bump keeps the previous version accepted by the C# validator for one subsequent phase — a phase-N TS client against a phase-(N-1) C# side is the only transition we permit, and both sides must move to phase N before phase N+1 ships.

### 8.2 Host adapter contract

Optional method added in T1:

```ts
interface HostAdapter {
    // ...existing...
    resolveRuntimeConfigOverlays?(): Promise<Partial<DotnetModule>>;
}
```

`BrowserHostAdapter` implements it; `NodeHostAdapter` either doesn't implement (interface sees `undefined`) or returns `Promise.resolve({})`. The consumer in `bootRuntime` uses `adapter.resolveRuntimeConfigOverlays?.() ?? Promise.resolve(null)`.

### 8.3 Ref-pack changes

- T1: none.
- T2: **new** `Carbide.Terminal.ref.dll` shipped alongside `@carbide/refs-net10.0`. Keep the ref-pack package name stable; add the new DLL to its dll-list manifest.
- T3: none (fork of `System.Console.dll` doesn't change the compile-time reference metadata; stock ref-pack continues to apply).

### 8.4 Browser-host testing infrastructure

- All interactive fixtures vendor xterm.js under `packages/core/test/browser/vendor/xterm/`. Pinned version. One-time refresh step documented.
- Playwright assertions use `terminal.buffer.active`, `terminal.buffer.active.getLine(n)`, `line.getCell(col)`. The helper library lives at `packages/core/test/browser/helpers/terminal-assertions.mjs`.
- A shared `interactive-harness.html` loads xterm.js, constructs a `Terminal`, opens it into `#term`, and exports a `runCarbide(programSource) → RunResult` helper that each fixture reuses.

### 8.5 Docs structure additions

- New subsection in `docs/Carbide-Current-State-Guide.md` — "Interactive terminal (T1)", "Interactive terminal (T2 — `CarbideConsole`)", "Interactive terminal (T3 — `System.Console` parity)". Each subsection is current-state-of-feature, not a change log.
- New drift entries per phase, with the "documented deviation" format already used for U1/U2.
- `packages/core/README.md` quickstart — one "embed xterm + run an interactive program" example with all the boilerplate.

## 9. Design decisions (cross-cutting)

- **DT-25 (cross). Terminal work lives at `Carbide.Terminal.*`, not inside `Carbide.Core.*`.** Matches the Near-style layer ownership discipline. `Carbide.Core` stays the runtime/compiler; `Carbide.Terminal` is a sibling feature with its own headless seams (internal `IBridge` interface in C#, injectable for tests).
- **DT-26 (cross). The bridge is the only JS-visible surface.** No C# code calls JS outside `CarbideTerminalInterop`; no JS code reaches into the runtime outside the bridge. This keeps the refactor boundary clean and the interop surface testable.
- **DT-27 (cross). Terminal sessions are per-project, not per-session.** `CarbideSession` can own multiple projects; each project can have at most one active `TerminalSession`. A second `runInteractive` on the same project while one is live throws.
- **DT-28 (cross). All phases ship behind the same public TS entry (`project.runInteractive`).** No capability flags in the public API. T3's worker-mode toggle is an environment variable / build-time flag, not a TS option — users don't know or care about it.
- **DT-29 (cross). Every phase's drift entry uses the same "what this deviates from conhost" format.** Reader sees one list of deviations across phases, not three.
- **DT-30 (cross). Refactoring existing `ProjectCompiler.RunAsync` is out of scope.** `TerminalRun` is a sibling code path. No shared mutable state between `RunAsync` and `TerminalRun`; if they share a helper, it's pulled into a static. Minimizes regression risk in the non-interactive path.

## 10. Overall risks (cross-phase)

| # | Risk | Impact | Mitigation |
|---|---|---|---|
| RT-25 | Feature creep: "while we're in there, let's add X" during T2 or T3 | Medium | Each phase's acceptance is literal; PRs outside it get deferred to "follow-ups" (§11). |
| RT-26 | `@xterm/xterm` major version breaks the API we use | Low | Peer dep with a bounded range (`^5.5`); `terminal.write`, `onData`, `onResize`, `buffer.active` are the only call sites — small surface to re-validate on upgrade. |
| RT-27 | A downstream embedder builds against T2 schema 5, then Carbide bumps to schema 6 in T3 | Medium | Schema-transition window: C# accepts N−1 *and* N simultaneously for one phase after each bump. Documented in the interop schema comment block. |
| RT-28 | The "forked `System.Console.dll` replaces stock" step clobbers an unrelated fix in a later .NET 10 servicing release | Low | Re-sync discipline in T3.2 with a pinned upstream commit; re-sync is a phase of its own when triggered. |
| RT-29 | A phase lands partially (T2.1–T2.6 merge, T2.7 stalls) and leaves a half-wired feature on trunk | Medium | Feature flag at the `project.runInteractive` entry: while the phase is in-flight, `runInteractive` throws `"T2 line mode in progress; use T1 only"`. Remove the flag at phase close. |

## 11. Follow-ups discovered during planning

- **Bracketed-paste mode.** `\x1b[?2004h` / `\x1b[?2004l`. Some user programs want to distinguish pasted-in text from typed-in text. Can ship as an option on the line editor in T2 or defer to T4 polish.
- **Mouse SGR vs legacy encoding.** xterm supports both; our docs should point out which modes are preferred.
- **Integrated GIF-ish session recording.** xterm's buffer + a timestamp trail could power "replay my interactive session." Not in scope; worth noting.
- **Editor prompt kit.** A higher-level API like `CarbideConsole.Prompt.Ask<T>` over `ReadLineAsync` would save every user re-writing the same input loop. Defer to a `@carbide/prompts` package if there's demand.
- **Language-line-edit bindings.** Emacs-style (`Ctrl+A` / `Ctrl+E` / `Ctrl+K`) vs vi-style line editing. The hand-roll in T2 does basic left/right/backspace; Emacs shortcuts are a follow-up.
- **Visual runtime-state inspector.** A developer-mode overlay showing "C# is waiting on `ReadLineAsync`", "JS line editor holds 3 chars", etc. Purely a debugging aid.
- **Canvas / WebGL renderer benchmark.** xterm ships both; T4 could characterize which one performs better on the test fixtures.

## 12. Links

- **Feasibility — primary**: [`reports/carbide-xterm-interactive-console-feasibility__2026-04-19__21-55-15-000000.md`](../reports/carbide-xterm-interactive-console-feasibility__2026-04-19__21-55-15-000000.md).
- **Feasibility — independent review**: [`reports/carbide-browser-xterm-console-feasibility__2026-04-19__22-01-41__06bf6d9b78c7.md`](../reports/carbide-browser-xterm-console-feasibility__2026-04-19__22-01-41__06bf6d9b78c7.md).
- **Upstream `ConsolePal.Browser.cs`** (what T3 replaces): `lib/dotnet/runtime/src/libraries/System.Console/src/System/ConsolePal.Browser.cs`.
- **Upstream `ConsolePal.Unix.cs`** (what T3 reuses): `lib/dotnet/runtime/src/libraries/System.Console/src/System/ConsolePal.Unix.cs`.
- **Upstream `KeyParser.cs`** (T2 port, T3 reuses): `lib/dotnet/runtime/src/libraries/System.Console/src/System/IO/KeyParser.cs`.
- **Parent usability plan (U1–U3)**: [`carbide-post-m9-usability-remediation-plan__2026-04-19__05-30-00-000000.md`](carbide-post-m9-usability-remediation-plan__2026-04-19__05-30-00-000000.md) — T1/T2 build on top of U1's sentinel-framed output and U2's `RunOptions` wiring.
- **Current-state guide**: [`../Carbide-Current-State-Guide.md`](../Carbide-Current-State-Guide.md).
- **Architecture plan**: [`carbide-architecture-and-implementation-plan__2026-04-17__16-16-47-000000.md`](carbide-architecture-and-implementation-plan__2026-04-17__16-16-47-000000.md).

---

Vladimir — the staging is: land T1 for the immediate demo and the free stdout-bypass fix; decide the §3.0 gate before T2 closes; run T2 behind the new `CarbideConsole.*` surface to unlock real interactive work on your own source; then either stop (if the gate says no pre-compiled-library coverage) or go into T3 for the forked `System.Console.dll`. T4 is the net underneath whichever tier is the final deliverable. Every piece of the plan has a precedent in the existing Carbide phasing (Hosting adapters, reflection-patched `Console.*`, JSExport/JSImport surface evolution, schema bumps with transition windows) — nothing here asks Carbide to grow a new category of capability.
