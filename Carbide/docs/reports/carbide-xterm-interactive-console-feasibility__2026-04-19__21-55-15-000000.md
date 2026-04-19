# Feasibility: running Carbide C# console apps in the browser with xterm.js and conhost-like `System.Console` / ANSI behavior

- Created (UTC): 2026-04-19T21:55:15Z
- Repository HEAD: 43db73bda4ae735ad00fe7c40caab66f203d9dd0
- Status: Feasibility report (design evaluation, not an implementation plan).
- Audience: Vladimir; future Carbide contributors considering a browser-terminal story.
- Scope: evaluate what it would take to extend `src/Carbide` so user C# console programs executed in the browser can drive an embedded [xterm.js](https://xtermjs.org/) instance with behavior that approximates running the same program under `conhost.exe` on Windows desktop. Includes `System.Console` surface, ANSI / VT sequences, stdin (line and key mode), window size, color, cursor, and Ctrl+C.
- Related code:
  - [`packages/core/src/Services/ProjectCompiler.cs`](../../packages/core/src/Services/ProjectCompiler.cs) — where stdout/stderr/stdin are wired today.
  - [`packages/core/src/CompilationInterop.cs`](../../packages/core/src/CompilationInterop.cs) — JSExport boundary.
  - [`packages/core/src/ts/host/browser/browser-adapter.ts`](../../packages/core/src/ts/host/browser/browser-adapter.ts) — browser host adapter.
  - [`packages/core/src/ts/project.ts`](../../packages/core/src/ts/project.ts) — public `Project.run()` API.
  - [`packages/core/src/ts/runtime/boot.ts`](../../packages/core/src/ts/runtime/boot.ts) — Mono-WASM boot.
- Related docs:
  - [Carbide Current-State Guide](../Carbide-Current-State-Guide.md)
  - [Carbide usability report](Carbide-Usability-Report.md)
  - [Carbide–Avalonia browser GUI integration research](../research/avalonia-ui/carbide-avalonia-browser-gui-integration__2026-04-18__21-52-50-185670__57c69d8c45e3.md)
  - [Carbide–pwsh subset feasibility](../research/pwsh/carbide-pwsh-subset-feasibility__2026-04-19__10-30-00-000000__a7c3d4e9f1b2.md)
  - [Carbide JS-interop bridge proposal](../proposals/carbide-js-interop-bridge-proposal__2026-04-18__22-00-00-000000__a2d2955163d1.md)
- External references (not vendored):
  - `lib/dotnet/runtime/src/libraries/System.Console/src/System/ConsolePal.Browser.cs` — authoritative `System.Console` browser surface.
  - `lib/dotnet/runtime/src/libraries/System.Console/src/System/Console.cs` — `SetIn`/`SetOut`/`SetError` contracts.

## 1. Request

> Suppose we want to extend `src/Carbide` to be able to run C# console apps in browser with interactive I/O via xterm.js embedded on the web page, and we want most of `System.Console` API and ANSI/VT work similar to running the app in conhost.exe on Windows desktop.

Three scope words pinned before analysis:

- **"interactive I/O"** — live, byte-at-a-time stdin *and* stdout, not the existing `project.run()` model that buffers stdout with `Console.SetOut(StringWriter)` and returns the entire transcript after the entry point returns.
- **"xterm.js embedded"** — a terminal widget in the host web page. Carbide speaks "write these bytes to the terminal" and "here's the user's keystrokes as bytes" across the JS/WASM boundary; xterm.js owns rendering, ANSI parsing, cursor, color, and resize.
- **"similar to conhost.exe on Windows desktop"** — what modern Windows conhost gives C# programs today (Windows 10 22H2+ / Windows 11): working `Console.WriteLine`, `Console.ReadLine`, `Console.ReadKey`, `Console.ForegroundColor`, `Console.SetCursorPosition`, `Console.WindowWidth`, *and* passthrough of raw ANSI escape sequences (`\x1b[31m…\x1b[0m`). Not perfect parity — a *useful* subset matching the default conhost experience.

## 2. Executive summary

| Capability | Today in Carbide | Vertical slice (1–2 weeks) | Useful (4–8 weeks) | Conhost-like (2–4 months) | Out of scope |
|---|---|---|---|---|---|
| `Console.WriteLine` bytes streamed to xterm.js as they land | buffered into `StringWriter`, emitted after run completes | ✔ custom `TextWriter` → JSImport callback | ✔ | ✔ | — |
| Plain `\x1b[...]` ANSI passthrough (colors, cursor, SGR) | works — bytes transit unchanged, but buffered | ✔ — xterm.js parses natively | ✔ | ✔ | — |
| `Console.ReadLine()` → prompt user, read a line | not supported interactively (only pre-seeded `stdin` string) | ✔ via a custom `TextReader` backed by an async JS pump | ✔ | ✔ | — |
| `Console.ReadKey(bool)` (single char, maybe modifier keys) | throws `PlatformNotSupportedException` on browser | — | ✔ with `ConsolePal` reflection patch + xterm `onData` decoder | ✔ | — |
| `Console.ForegroundColor = ConsoleColor.Red` | throws PNS | — | ✔ route through ANSI SGR on a shim writer | ✔ | — |
| `Console.SetCursorPosition(x, y)` | throws PNS | — | ✔ translate to `\x1b[y;xH` | ✔ | — |
| `Console.WindowWidth` / `Console.WindowHeight` | throws PNS | — | ✔ read from xterm.js `cols`/`rows` cache | ✔ | — |
| Window-resize awareness (`CancelKeyPress`-equivalent on SIGWINCH) | none | — | partial — xterm emits `onResize`; wire to a `Carbide.TerminalResized` event | ✔ | — |
| `Console.TreatControlCAsInput` | throws PNS | — | ✔ behavior toggle inside Carbide-owned bridge | ✔ | — |
| Ctrl+C → `Console.CancelKeyPress` / graceful shutdown | none | deliver `\x03` byte inbound; no event | partial — hook a `CancellationToken` into the bridge | ✔ | — |
| `Console.Clear()` | already routes to a `[MethodImplAttribute(InternalCall)]` that ends up as a no-op in the default WASM runtime | ✔ override → emit `\x1b[2J\x1b[H` | ✔ | ✔ | — |
| Alternate screen buffer, mouse tracking | not used today | — | — | ✔ (already emitted by user code as raw ANSI; xterm.js handles them) | — |
| `Console.Beep()` / `Beep(int, int)` | throws PNS | — | low value | possibly route to `\x07` (BEL) | dual-freq beep is not ANSI — drop it |
| `Console.In`-as-a-pipe with byte fidelity (`OpenStandardInput`) | throws PNS | — | — | partial — a `BrowserConsoleStream`-shaped read stream is feasible | — |
| Real stdin redirection (Unix-style pipe from outside the tab) | nonsensical | — | — | — | ✘ no filesystem pipe in the browser |
| `Console.Title` = window-title | throws PNS | — | — | ✔ emit `\x1b]0;title\x07` (OSC 0) | — |
| Cursor visibility, size, color | throws PNS | — | — | ✔ emit DECTCEM `\x1b[?25h/l` + DECSCUSR | — |
| Multiple concurrent interactive sessions in one tab | single session today | supported (multi-project already works) | ✔ | ✔ | — |
| SharedArrayBuffer-backed truly-synchronous stdin (no `async`) | not available (COOP/COEP not required) | — | — | feasible only under COOP/COEP | ✘ if isolation headers are off the table |

**Bottom line.** An xterm.js-backed interactive Carbide surface is **feasible and largely additive**. The core change is a new execution path (`project.runInteractive(terminal, options?)`) that swaps today's buffered capture for a live bidirectional byte pipe between xterm.js and Mono-WASM. Conhost-like parity for the high-level `System.Console` API (colors, cursor, window size) requires patching or shimming the `ConsolePal` members that throw `PlatformNotSupportedException` on browser — this is the one unavoidable piece of "reach into the runtime" work.

The single hard constraint: Mono-WASM is **single-threaded** on the main thread by default, so blocking `Console.ReadKey()` cannot be literally blocking. Three workable answers are on the table (§8.4), ordered from easiest to most invasive:

1. Cooperative async: user code `await`s a Carbide-provided reader. Easy, but breaks strict conhost parity.
2. Worker-hosted Mono-WASM with `SharedArrayBuffer` + `Atomics.wait`: gives true sync `ReadKey`. Requires COOP/COEP isolation on the host page.
3. Emscripten `ASYNCIFY` / Mono-WASM's JS-import `"Async"` mode: unwinds and restarts the C# stack across JS awaits. Works today, but costs size and speed.

The report recommends (§12) a phased plan: tier 1 ships option 1, tier 3 optionally adds option 2 behind a capability flag. Option 3 is documented but not recommended.

## 3. Today's Carbide execution model (baseline for what changes)

### 3.1 The end-to-end run path today

From `project.run()`:

1. TypeScript `Project.run()` serializes any `args`/`stdin` to JSON and calls `interop.RunAsync(projectId, runOptionsJson)` (`packages/core/src/ts/project.ts:83`).
2. C# `CompilationInterop.RunAsync` deserializes the options, dispatches to `SessionSolutions.RunAsync(projectId, args, stdin)` (`packages/core/src/CompilationInterop.cs:137`).
3. `ProjectCompiler.RunAsync(args, stdin)`:
   - Builds an in-memory PE with `TryGetErrorFreeCompilationAsync` + `EmitPeAndPdb`.
   - Loads the PE via `Assembly.Load(byte[])`.
   - Instantiates two `StringWriter`s — `stdOutCapture` and `stdErrCapture`.
   - Calls `Console.SetOut(stdOutCapture)` and `Console.SetError(stdErrCapture)`.
   - If `stdin` is non-null, sets the `Console._in` / `s_in` static field via reflection to a `StringReader` (standard `Console.SetIn` throws `PlatformNotSupportedException` on browser).
   - Invokes `entryPoint.Invoke(null, invocationArgs)` — synchronous on Mono-WASM's single thread *modulo* `Task`/`ValueTask` returns.
   - After the entry point returns (or throws), pulls `.ToString()` off the `StringWriter`s and packages into `RunResult`.
   - Restores the previous `Console.Out` / `Console.Error` / `Console._in`.
4. `RunResult` (with `stdOut`, `stdErr`, `exitCode`, duration) is JSON-serialized across the JSExport boundary and returned to TypeScript.

### 3.2 The three consequences for interactive I/O

- **Buffered-only output.** `StringWriter` holds the whole transcript in RAM. Nothing leaves C# until the entry point returns. Long-running programs, progress indicators, and prompts are invisible.
- **Pre-seeded-only input.** `stdin` is a string injected before the run starts. No interaction is possible — the user cannot type after the program is live.
- **Synchronous-returning `run`.** The TS caller sees a single `Promise<RunResult>` that resolves once after entry-point exit. There is no concept of a long-lived "session" with the user's code.

All three need to change for xterm.js to be useful. None of them is structurally unsound — `Console.SetOut` takes any `TextWriter`, `Console._in` accepts any `TextReader` (once the PNS guard is bypassed via reflection, which Carbide already does), and the run-path can be restructured to fire intermediate callbacks before returning.

### 3.3 How Carbide boots today (unchanged)

Carbide boots `dotnet.js` via `builder.withConfig(...).create()` (`packages/core/src/ts/runtime/boot.ts:50`). No `print` / `printErr` overrides are set on the `DotnetModule`. Therefore raw writes via `Console.OpenStandardOutput()` — bytes going through `BrowserConsoleStream.Write` → `Interop.Sys.Write(fd=1, ...)` — surface in the browser as `console.log` / `console.error` lines by way of emscripten's default stream routing. Those writes **bypass** `Console.SetOut` redirection entirely, which is already called out as a sharp edge in the [usability report](Carbide-Usability-Report.md). This is a tangential detail for the normal run path today; for xterm.js it becomes a design choice we must make explicit (§8.1).

## 4. xterm.js: the contract Carbide needs to speak

xterm.js is a pure-JavaScript terminal emulator that consumes bytes/strings and renders VT100 + xterm + a lot of ANSI SGR/CUP. Carbide only needs a thin sliver of the API:

**What xterm.js consumes from Carbide (output side):**

- `terminal.write(data: string | Uint8Array)` — write bytes, let xterm.js parse ANSI and render. UTF-8 by default; xterm.js decodes multi-byte sequences itself.
- `terminal.writeln(data)` — convenience for `data + "\r\n"`.
- `terminal.clear()` / `terminal.reset()` — Carbide rarely drives these; user code does it via `\x1b[2J` or `Console.Clear`.

**What Carbide consumes from xterm.js (input side):**

- `terminal.onData((data: string) => void)` — user keystrokes arrive as strings. Plain printable characters are UTF-8; control keys (arrows, F-keys, Home/End, etc.) arrive as the standard xterm escape sequences (`\x1b[A` / `\x1bOA`, etc.). `Enter` on a non-raw terminal typically delivers `\r`.
- `terminal.onResize((size: { cols: number; rows: number }) => void)` — fires whenever FitAddon (or a JS `ResizeObserver`) recomputes geometry.
- `terminal.cols`, `terminal.rows` — the current size, readable any time.
- `terminal.onKey((ev: { key: string; domEvent: KeyboardEvent }) => void)` — if Carbide ever needs to distinguish a bare `Ctrl+C` from a pasted `\x03`, `onKey` gives the raw `KeyboardEvent`. Normally unnecessary.

**Composition / lifecycle:**

- The host page creates a `new Terminal({ ... })`, `open(domElement)`s it, and typically loads `FitAddon` so the terminal tracks its container. Resize observation is host-side, not Carbide-side.
- xterm.js is locale-agnostic bytes → glyphs; Carbide never needs to speak the CSI language. We are a dumb byte pipe, and xterm.js does all the parsing and rendering.

**Addons worth knowing but not demanding:**

- `@xterm/addon-fit` — auto-resize. Host-side concern.
- `@xterm/addon-web-links` — URL click-through.
- `@xterm/addon-canvas` or `@xterm/addon-webgl` — renderer swap for speed. Host decision.

## 5. `System.Console` on Mono-WASM browser — the authoritative surface

Read directly from `lib/dotnet/runtime/src/libraries/System.Console/src/System/ConsolePal.Browser.cs`. This is the source the Mono-WASM build of System.Console uses, so it is what Carbide's user programs link against at runtime.

### 5.1 Supported (returns real values, no exceptions)

- `OpenStandardOutput()` — returns a `BrowserConsoleStream` over fd 1.
- `OpenStandardError()` — same over fd 2.
- `OutputEncoding` — UTF-8 by default; `SetConsoleOutputEncoding` works.
- `IsInputRedirectedCore()` — returns `false`.
- `IsOutputRedirectedCore()` — returns `false`.
- `IsErrorRedirectedCore()` — returns `false`.
- `KeyAvailable` — returns `false`.
- `WindowLeft`, `WindowTop` — **getters return `0`**, setters throw PNS.
- `EnsureConsoleInitialized()` — no-op.
- `Clear()` — `[MethodImpl(InternalCall)]`, routes into the runtime.

### 5.2 Throws `PlatformNotSupportedException`

- `OpenStandardInput()`, `OpenStandardInputHandle()` — stdin stream/handle.
- `GetOrCreateReader()` — this is why `Console.In` throws PNS on first access.
- `InputEncoding` getter, `SetConsoleInputEncoding`.
- `NumberLock`, `CapsLock`, `TreatControlCAsInput` (both get and set).
- `ReadKey(bool)`.
- `BackgroundColor` / `ForegroundColor` / `ResetColor()`.
- `CursorSize`, `CursorVisible`, `GetCursorPosition`, `SetCursorPosition`.
- `Title` getter/setter.
- `Beep` (both overloads).
- `MoveBufferArea`.
- `BufferWidth`, `BufferHeight`, `SetBufferSize`.
- `LargestWindowWidth`, `LargestWindowHeight`.
- `WindowLeft`/`WindowTop` setters (getters return 0).
- `WindowWidth`, `WindowHeight`.
- `SetWindowPosition`, `SetWindowSize`.

### 5.3 What `Console.SetOut` / `Console.SetIn` / `Console.SetError` do

Reading `Console.cs`:

- `Console.SetOut(TextWriter)` — **no `[UnsupportedOSPlatform("browser")]`** attribute. Works fine. Assigns the private static `s_out`; future `Console.Write*` calls go through the new writer.
- `Console.SetError(TextWriter)` — same: works fine.
- `Console.SetIn(TextReader)` — **has** `[UnsupportedOSPlatform("browser")]`. At runtime the getter `Console.In` throws PNS because it calls `ConsolePal.GetOrCreateReader()` which unconditionally throws. Carbide already works around this by writing `s_in` via reflection (`ProjectCompiler.SetConsoleInField`); once `s_in` is non-null, the `Console.In` getter returns it without calling `GetOrCreateReader`.

### 5.4 Consequence for Carbide's design

- **Output is the easy half.** Carbide can keep using `Console.SetOut` with a custom `TextWriter`; the new wrinkle is that the writer must push bytes to JS *incrementally* (not buffer them).
- **Input is the harder half.** `Console.In` and `Console.ReadLine()` can be made to work by keeping Carbide's reflection-based `s_in` install, but the `TextReader` we install must cooperatively yield to let JS deliver the next line. `Console.ReadKey` bypasses `Console.In` entirely — it goes straight to `ConsolePal.ReadKey(bool)` which throws PNS. To support it we must either patch the ConsolePal static method (reflection / method-detour) or provide a Carbide-prefixed alternative (`CarbideConsole.ReadKey()`) that user code calls.
- **Colors and cursor: redirect through ANSI.** `Console.ForegroundColor = ConsoleColor.Red` is really a programmer-level API for "make future text red." On conhost.exe the BCL translates it to either console-API calls or ANSI SGR depending on a capability check. Since xterm.js parses ANSI, we can shim the static getter/setter to emit `\x1b[31m` on the current `Console.Out`. Same for cursor, window title, etc. — the translation is mechanical.
- **Window size: cache + notify.** `Console.WindowWidth` must return xterm's current `cols`. That value changes on resize, so we need to keep it synchronized via a JS → WASM callback on `onResize`.

## 6. Interop mechanics: moving bytes across the JS/WASM boundary

Today's Carbide sends strings one way (`[JSExport]` methods return `Task<string>` or `string`). For a streaming terminal we need **both** directions, and we need the C# side to call *out* to JS, not just the other way around. Three tools are available:

### 6.1 `[JSImport]` (C# calls JS)

Available in .NET 10 Mono-WASM. Counterpart to `[JSExport]`. Declaration pattern:

```csharp
public static partial class CarbideTerminalInterop
{
    [JSImport("globalThis.Carbide.Terminal.write")]
    internal static partial void WriteStdOut(string text);

    [JSImport("globalThis.Carbide.Terminal.writeErr")]
    internal static partial void WriteStdErr(string text);

    [JSImport("globalThis.Carbide.Terminal.readLineAsync")]
    internal static partial Task<string?> ReadLineAsync();

    [JSImport("globalThis.Carbide.Terminal.readKeyAsync")]
    internal static partial Task<string> ReadKeyAsync();

    [JSImport("globalThis.Carbide.Terminal.getCols")]
    internal static partial int GetCols();

    [JSImport("globalThis.Carbide.Terminal.getRows")]
    internal static partial int GetRows();
}
```

At boot time the TypeScript runtime sets `globalThis.Carbide.Terminal = { write, writeErr, readLineAsync, readKeyAsync, getCols, getRows, … }` pointing at whatever terminal the caller wired up. `[JSImport]` marshalling supports `string`, `int`, primitive arrays, `Task<T>`, `Memory<T>`, and `ArraySegment<byte>` — enough for our pipe. Binary payloads (`ReadOnlySpan<byte>`) can be marshalled as `Memory<byte>` or base64 strings as a fallback.

Cost: one-off boundary-crossing overhead (a fraction of a millisecond per call). Not free — for `Console.Write('x')` called in a tight loop, this adds real latency. Mitigation: buffer on the C# side with a short-interval flush (e.g. flush every 32 ms *or* every 4 KB, whichever comes first) — matches the behavior of real TTYs and stays imperceptibly below human reaction time.

### 6.2 `[JSExport]` (JS calls C#)

This is what Carbide already uses. For the terminal path we'd add two or three:

```csharp
[JSExport] public static void DeliverStdIn(string data);             // JS pushes user bytes in
[JSExport] public static void NotifyResize(int cols, int rows);      // JS reports new size
[JSExport] public static void DeliverSignal(string signal);          // "SIGINT" for Ctrl+C
```

### 6.3 `SharedArrayBuffer` + `Atomics.wait` (shared-memory synchronization)

Needed *only* if we want truly-synchronous `Console.ReadKey()` that blocks the C# thread until a key arrives. Requires the host page to serve with `Cross-Origin-Opener-Policy: same-origin` and `Cross-Origin-Embedder-Policy: require-corp` (or `credentialless`). If the environment supports it, the pattern is:

- Main thread creates a `SharedArrayBuffer`, hands a view to the worker running Mono-WASM *and* registers an xterm `onData` listener.
- When user types, main thread writes bytes into the SAB and `Atomics.notify`s a slot.
- Inside C#, `Console.ReadKey` calls a `[JSImport]` that busy-waits on `Atomics.wait` for that slot.

This is described as a tier 3 capability (§10) because it needs:

- moving Mono-WASM into a Web Worker (the runtime supports this in .NET 10 but is less battle-tested; JSInterop has main-thread restrictions),
- host-page cooperation to set COOP/COEP,
- fallback behavior when SAB is unavailable.

### 6.4 Asyncify / JS-import `Async` marshalling

A `[JSImport]` can be declared with `Async` semantics, making it return a `Task<T>`. Mono-WASM unwinds and resumes the C# stack across the await. This is what option A below rides on. It has no isolation-header requirement; the cost is that `await` points become visible to the user program — not identical to conhost's `Console.ReadKey` which returns synchronously. For pipe-shaped programs ("read a line, process, write a line, loop") this is invisible because `Console.In.ReadLineAsync()` already existed. For programs that actually call `Console.ReadKey(intercept: true)` inside a `while(true)` loop, we need a patched `ConsolePal.ReadKey` that internally awaits — and that patch has to *look* synchronous to the user. Doable, but constrains how it unwinds (§8.4).

## 7. The problem decomposed

### 7.1 stdout bytes → xterm.write

- Wire: `Console.SetOut(new StreamingStdOutWriter(flushInterval))`. The writer's `Write(char)` / `Write(string)` / `Flush()` buffer into a `StringBuilder` (or a pooled `char[]`), and every `flushInterval` or buffer-fill, calls `CarbideTerminalInterop.WriteStdOut(text)`.
- ANSI passthrough is free: xterm.js parses the bytes; Carbide doesn't need to do anything special for SGR, cursor moves, alternate screen, mouse reporting, or OSC.
- `Console.OpenStandardOutput()` bypass: if a user program calls this, their writes go through `BrowserConsoleStream` and end up at emscripten's `console.log`, **not** at our terminal. This is a dual-write hazard identical to the one documented in the [usability report §7 / §11](Carbide-Usability-Report.md) (the "raw bytes surface on stdout" footnote). Fix options:
  1. Accept as a known gap; document it and stop there. (Cheapest.)
  2. Override emscripten's `print`/`printErr` via Module config and route those to the same writer. (Catches all native writes; requires boot-config patch.)
  3. Patch `ConsolePal.OpenStandardOutput` via reflection to return a stream that writes to our bridge instead of `BrowserConsoleStream`. (Comprehensive but adds reflection surface.)
- **Recommendation:** option 2 — override emscripten's `print` at boot — because it's the one that cleanly solves *both* the user-code `OpenStandardOutput` issue and the runtime's own diagnostic spray (which is the source of "CLI JSON output gets prefixed with raw bytes" that U1 half-mitigated).

### 7.2 stderr bytes → xterm.write (red? dim? configurable)

Same shape as 7.1 with `Console.SetError`. Design choice: does stderr interleave into the same xterm with a different SGR, or go to a separate terminal pane? Recommend a single terminal with an optional SGR prefix (`\x1b[2m…\x1b[22m`) configurable by the caller. Matches conhost behavior under cmd.exe/pwsh where both streams land in the same TTY.

### 7.3 stdin: `Console.ReadLine()` (line mode)

The common case. `Console.ReadLine` goes through `Console.In.ReadLine()`, which delegates to whichever `TextReader` is installed. Carbide already owns that install path.

Design:

- Build a `BrowserTerminalReader : TextReader` whose `ReadLineAsync()` awaits a `TaskCompletionSource<string>` that JS fills when a full line arrives.
- The JS side buffers `terminal.onData` bytes, runs a local-echo + line editor (arrow keys, backspace, history optional), and on `Enter` flushes the accumulated line across the boundary via `DeliverStdIn`.
- The reader's synchronous `ReadLine()` **blocks indefinitely** on single-threaded WASM. Two exits:
  1. Document that `Console.ReadLine()` from user code will block the main thread forever — they must call `Console.In.ReadLineAsync()` (a real BCL API since .NET 7) or an equivalent `await`.
  2. Under tier 3 (worker + SAB), `ReadLine()` waits on an `Atomics.wait` slot and returns synchronously.

**Recommendation:** tier 1 ships option 1 with a clear error message when `ReadLine()` is called (detected by the reader), telling the user to use `ReadLineAsync()`. This is friction, but friction that points at a specific API with a clean migration path.

A simple JS-side line editor is needed for line mode because xterm.js by itself only echoes whatever you `write()` back to it; the library [`local-echo`](https://github.com/wavesoft/local-echo) (AGPL; or its MIT forks) gives Carbide a drop-in "read a line with line editing, history, tab completion" implementation. If we don't want the license surface, a 150-line hand-roll covers backspace + arrow history + Enter — the feature bar a shell program expects.

### 7.4 stdin: `Console.ReadKey` (raw/key mode)

The harder case. `Console.ReadKey(intercept)`:

- Not a member of `Console.In` — it's a static on `Console` that calls `ConsolePal.ReadKey(bool)`.
- Returns a `ConsoleKeyInfo` struct with `.KeyChar` (char) + `.Key` (enum) + `.Modifiers` (flags).

Patch strategies:

- **A. Detour `ConsolePal.ReadKey`.** Reflect into `System.Console`'s assembly, locate the static `ConsolePal.ReadKey(bool)`, and hand-install a redirector. Technique: use `DynamicMethodBuilder` → nope, Mono-WASM interpreter doesn't support `DynamicMethod`. Alternative: write a Roslyn source rewrite — but that changes user code. The cleanest path is a small Mono-WASM–specific patch in a sibling assembly that *reimplements* `ConsolePal.ReadKey` and relies on IL substitution at publish time — but Carbide doesn't publish; it loads user PE bytes. So the IL substitution must happen *inside* the `dotnet publish` of `@carbide/core` itself, not the user's assembly.
- **B. Provide `CarbideConsole.ReadKey()` as the blessed API.** Document that `Console.ReadKey()` on browser-WASM throws PNS and that user code should call `CarbideConsole.ReadKey()` (async) or `CarbideConsole.ReadKeyAsync()`. Ugly (not conhost-parity) but honest — the same pattern is already in place for other browser-WASM APIs.
- **C. Provide a `CarbidePolyfill` NuGet-style reference DLL** that shadows `System.Console.ReadKey` with a working implementation. Shadowing is tricky because Roslyn resolves `Console.ReadKey` against the `System.Console` ref-pack metadata, not the implementation. Option: ship a source generator or publish-time weaver that rewrites `Console.ReadKey` calls to `CarbideConsole.ReadKey`. Moderate-complexity, but a real conhost-parity answer.

**Recommendation:** tier 2 ships **B** (explicit `CarbideConsole.ReadKey`). Tier 3 or 4 evaluates **C** if demand materializes. **A** is rejected as too fragile on Mono-WASM interpreter mode.

Key decoding inside the reader:

- xterm.js delivers keystrokes as bytes, not as `ConsoleKey` tags. `ArrowUp` arrives as `\x1b[A`, `F1` as `\x1bOP`, `Home` as `\x1b[H`, etc. A small Carbide-side state machine maps the CSI / SS3 forms back to `ConsoleKey` + `ConsoleModifiers`. Reference: `lib/dotnet/runtime/src/libraries/System.Console/src/System/IO/KeyParser.cs` — the Unix key parser already does this. Port it verbatim; it's ~400 lines and has excellent test coverage upstream.

### 7.5 `Console.ForegroundColor` / `BackgroundColor` / `ResetColor`

Translate at the sink:

```csharp
public static ConsoleColor ForegroundColor
{
    get => _fg;
    set { _fg = value; Write(AnsiSgr.Foreground(value)); }
}
```

Where `AnsiSgr.Foreground(ConsoleColor.Red)` returns `"\x1b[31m"` etc. (With `ConsoleColor.DarkRed` → `"\x1b[31m"`, `ConsoleColor.Red` → `"\x1b[91m"`, etc. — the Windows-to-ANSI mapping is documented.)

Patch vehicle: same as ReadKey (§7.4) — either detour `ConsolePal.ForegroundColor` / `BackgroundColor` / `ResetColor` **or** provide a `CarbideConsole.ForegroundColor` equivalent. Since this is three getters and three setters, I lean toward a Carbide-owned sibling static class *and* a publish-time "rewrite `Console.ForegroundColor` → `CarbideConsole.ForegroundColor`" transform once Carbide has a usable weaver. For tier 2, the `CarbideConsole` static is enough to demo the feature.

### 7.6 `Console.SetCursorPosition(x, y)` / `GetCursorPosition`

Set: emit `\x1b[<y+1>;<x+1>H` (CUP). One-liner.

Get: harder. ANSI has Device-Status-Report (DSR) — send `\x1b[6n`, the terminal replies with `\x1b[<row>;<col>R` on the *input* stream. So `GetCursorPosition` becomes:

1. Emit `\x1b[6n`.
2. Wait for the matching reply on the input pipe (and not let the user program see those bytes).
3. Parse and return `(row-1, col-1)`.

Requires a stdin pre-filter that intercepts DSR replies before they reach the user program's `ReadLine`. Implementable but non-trivial; reasonable to defer to tier 3. A tier 2 shim that tracks cursor position by observing what user code writes (count newlines, count chars) works for 80% of cases but is imperfect.

### 7.7 Window size (`WindowWidth` / `WindowHeight` / `BufferWidth` / `BufferHeight`)

- JS side: read `terminal.cols` / `terminal.rows`, push via `[JSExport] NotifyResize(cols, rows)` both on boot *and* on every `onResize` event.
- C# side: cache `_cols`/`_rows` in a static, return them from `CarbideConsole.WindowWidth` etc.
- For strict conhost parity we'd want `Console.WindowWidth` itself to return the live value, not `CarbideConsole.WindowWidth`. See §7.4/§7.5 on patch strategy.

Conhost-visible corollary: programs that dynamically redraw on resize (like `dotnet tool install` progress bars, `Spectre.Console`, `BenchmarkDotNet`) expect a `SIGWINCH`-equivalent event. .NET doesn't surface one on Windows today (programs poll `Console.WindowWidth` on a timer or in their render loop). So as long as width reads return the fresh value, most well-behaved programs cope. Surfacing an explicit `Carbide.TerminalResized` event is a nice-to-have.

### 7.8 Ctrl+C (SIGINT)

Conhost behavior: when user presses Ctrl+C, the runtime raises `Console.CancelKeyPress` with `ConsoleSpecialKey.ControlC`. If no handler runs, the process exits.

Browser model options:

1. **Byte delivery.** xterm's `onData` delivers `\x03` when user presses Ctrl+C. Treat this as a byte on the input stream. User code that reads stdin sees it; user code that doesn't, doesn't. Cheap but doesn't match `CancelKeyPress`.
2. **Event delivery.** Intercept `\x03` in the input bridge, do *not* push it into the `TextReader`, and instead fire `CancelKeyPress`. Cancel token on `CancellationTokenSource` so the run path can abort an ongoing async operation. Matches conhost semantics better but is a separate signal path.
3. **Both.** If `TreatControlCAsInput == true`, deliver as byte; otherwise, fire event. Matches .NET on Unix/Windows.

**Recommendation:** option 3 behind a toggle. For tier 1, do option 1 (byte delivery) and document the limitation.

### 7.9 `Console.Clear()`

`ConsolePal.Clear()` on browser is an `[InternalCall]` that hits a Mono-WASM runtime function. In practice on a headless Blazor page it's a no-op. Redirect it through our writer: emit `\x1b[2J\x1b[H` (ED + CUP home). xterm.js renders the clear.

Patch vehicle: since `Clear` is unconditionally `internal extern`, we can't shim it without reflection against the runtime. Cleanest path: leave `Console.Clear()` as is (it'll be a silent no-op on our path) and expose `CarbideConsole.Clear()` that emits the escape. User code that targets `Console.Clear` can be rewritten by a publish-time shim or told to use the Carbide variant.

### 7.10 `Console.Title`

Emit OSC 0: `\x1b]0;<title>\x07`. xterm.js supports it, the host page chooses what to do with the title event (`terminal.onTitleChange`) — typically forwards to `document.title`.

### 7.11 Redirection flags (`IsInputRedirected` etc.)

Today all three return `false`, which is fine — xterm.js is an interactive terminal by definition.

## 8. Synchronicity: the central design question

`System.Console.ReadLine` and `ReadKey` are defined as synchronous-blocking. Mono-WASM's default configuration runs C# on the browser main thread, shared with all JS including the xterm event pump. Blocking C# blocks the whole tab, including xterm's `onData` handler — **deadlock**. Three ways out:

### 8.1 Option A: cooperative async (tier 1–2)

- Ship `CarbideConsole.ReadLineAsync()` and `CarbideConsole.ReadKeyAsync()` as the *recommended* interactive APIs.
- Detect synchronous `Console.ReadLine()` and `Console.ReadKey` and throw a helpful exception pointing at the async variants.
- `[JSImport]` with `Task<string>` return — Mono-WASM's asyncify is inherent for JSImport-returned Tasks; the C# awaiter resumes when JS fulfills the promise.

Pros: works today, no COOP/COEP, no worker. Matches most modern C# idioms.
Cons: strict conhost parity is lost — existing programs written against `Console.ReadLine()` without `await` break. Real-world impact: top-level statements that use `Console.ReadLine()` would need to be rewritten to `await Console.In.ReadLineAsync()`. Carbide can also offer an auto-rewrite in its Roslyn layer (top-level statements auto-wrapped in an `async Task` body — Roslyn already does this for `await` at top level; they'd just need to call the async variant).

### 8.2 Option B: worker + SharedArrayBuffer + Atomics.wait (tier 3)

- Move Mono-WASM into a dedicated Web Worker (.NET 10 supports it; the JSImport/JSExport boundary is restricted but usable).
- Main thread owns xterm.js, reads user input via `onData`, writes bytes into a ring buffer backed by `SharedArrayBuffer`, and `Atomics.notify`s a slot.
- In C#, `Console.ReadKey` calls a `[JSImport]` that internally busy-waits on `Atomics.wait` for the slot. This blocks the worker thread *without* blocking the main thread.
- Requires host-page COOP/COEP headers.

Pros: strict sync parity for `Console.ReadKey` / `Console.ReadLine`. Maximum conhost fidelity.
Cons: host-page isolation headers are non-trivial (they break iframe embeds, cross-origin scripts, analytics tags). Moving Mono-WASM to a worker adds a second boot path and complicates host-side debugging. Carbide.Core would need to handle both "main-thread" and "worker" modes.

### 8.3 Option C: asyncify at the JSImport boundary

Mono-WASM's `[JSImport]` with `Task<T>` return type effectively does Option A with a nicer surface — the C# method returns a `Task<T>` that the caller awaits. This is in play for both A and B; it's not a separate mode, it's the underlying mechanism. The practical design distinction is whether user code uses async variants (A) or synchronous variants (B, via SAB).

### 8.4 Recommendation

**Ship tier 1 on A.** It's the only option that works under default browser isolation policies. It constrains users to async-aware entry points, but (a) top-level statements trivially support `await`, (b) most interactive C# console programs already prefer async I/O, and (c) the error message when a user calls `Console.ReadLine()` can be precise and helpful.

**Document B as tier-3-optional.** If Carbide ever adopts COOP/COEP on its own demo page, the existing `@carbide/terminal` code grows a worker-backed variant. The public API doesn't need to break — `runInteractive` can take a `{ mode: "cooperative-async" | "worker-sync" }` option.

## 9. Security, performance, and operational concerns

### 9.1 Security

- xterm.js is a DOM widget that parses untrusted byte streams. It is maintained by a careful team (and Microsoft VS Code depends on it), but escape-sequence fuzz is a real class of bugs. Carbide's only role is byte relay — we shouldn't need to sanitize.
- User C# code with network access can exfiltrate data through `fetch`. Carbide already doesn't sandbox user code; the terminal path doesn't change that risk shape. `Console.Write` doesn't become a new egress channel.
- OSC sequences like `\x1b]52;c;<base64>\x07` (OSC 52 set-clipboard) are parsed by xterm.js and can write to the system clipboard if configured. If Carbide exposes the terminal to untrusted user code, clipboard-write is an extra capability. Recommend: ship with `terminal.options.allowTransparency` / `options.allowProposedApi` at sensible defaults; specifically keep OSC 52 disabled unless the host opts in.

### 9.2 Performance

- Every byte cross the JS/WASM boundary has non-zero cost. A user program doing `for (i=0; i<1_000_000; i++) Console.Write(i);` without buffering is pathological. Mitigation: the `StreamingStdOutWriter` buffers on the C# side with a time-bounded flush — worst case ~32 ms latency, best case one cross-boundary call per `WriteLine`. xterm.js writes are batched via `terminal.write` which internally defers rendering to `requestAnimationFrame`, so throughput on the JS side is already well-tuned.
- The 100k–300k char/s regime is where real programs live (log spam, `Format-Table`, compiler output). A rough benchmark target: user program generating 1 MB of text should drain to xterm.js within ~200 ms of the entry point returning. This is achievable with flushing at 32 ms / 4 KB.
- `Console.ReadLine` has no perf concern — it's latency-bound by user typing speed.

### 9.3 Operational

- **Browser memory ceiling.** Long-running programs that append to xterm.js's scrollback grow memory. xterm has a configurable `scrollback` (default 1000 lines); Carbide should surface this on the terminal options.
- **Tab backgrounding.** When the user backgrounds the tab, `requestAnimationFrame` throttles; xterm's draw slows. User C# keeps computing. For bulk output programs this means the xterm draw buffer can balloon. Not critical but worth documenting.
- **Copy/paste.** xterm handles selection + `document.execCommand("copy")`. Pasting arrives as `onData`. Multi-line paste works; users who paste a 10k-line blob should not deadlock C# if we have flow control. Simplest flow control: cap the inbound queue, drop or backpressure on overflow. Reasonable cap: 1 MB.

## 10. Tiered scope

### 10.1 Tier 1 — "Hello, interactive world" (1–2 weeks)

Goal: a browser page with xterm.js; user C# code can do `Console.WriteLine`, `Console.Write`, emit ANSI escapes, and the bytes appear in the terminal as they land. **No input**.

Scope:

- New TypeScript API: `project.runInteractive({ terminal })` — accepts an xterm.js `Terminal` instance; returns a `Promise<RunResult>` that resolves on entry-point completion.
- `StreamingStdOutWriter` / `StreamingStdErrWriter` on the C# side, with ~32 ms flush interval.
- `[JSImport]` write callbacks wiring into `terminal.write` / `terminal.write(red(text))`.
- Override emscripten `print`/`printErr` at boot so runtime-side bytes also land in the terminal.

Non-scope: `Console.ReadLine`, `ReadKey`, color API, cursor API, resize awareness. Users who emit ANSI escapes manually get colors, cursor, and alternate screen for free — that's already enough for a lot of CLI demos.

### 10.2 Tier 2 — "Usable interactive shell" (4–8 weeks)

Goal: line-mode stdin works. Window-size queries work. Carbide-prefixed APIs cover the `ReadKey`/color gap.

Scope on top of tier 1:

- `BrowserTerminalReader : TextReader` installed via the existing reflection path, backed by a JS-side line editor (use `local-echo`-style library).
- `Console.In.ReadLineAsync()` / `.ReadToEndAsync()` etc. work.
- Async `Console.In.Read()` chars-at-a-time also work (for programs that tokenize stdin).
- `CarbideConsole` static with `ReadKeyAsync`, `ReadLineAsync`, `ForegroundColor`, `BackgroundColor`, `ResetColor`, `SetCursorPosition`, `GetCursorPosition`, `WindowWidth`, `WindowHeight`, `Title`, `Clear`.
- Helpful PNS-translation: when user code calls `Console.ReadLine()` / `Console.ReadKey()` / `Console.ForegroundColor` synchronously, the Carbide-installed reader (or a thin patched shim) throws a `NotSupportedException` with a message pointing at `CarbideConsole.*Async`.
- `NotifyResize` JSExport; `Carbide.TerminalResized` C# event.
- Ctrl+C byte-delivery path.
- Docs: xterm setup fixture, example page, `.csproj` fixture that references `CarbideConsole` via a lightweight `@carbide/terminal-bcl` metadata reference.

### 10.3 Tier 3 — "Conhost-parity mostly" (2–4 months after tier 2)

Goal: strict API parity for the supported subset. `Console.ForegroundColor = ConsoleColor.Red` works unmodified; `Console.ReadKey()` works synchronously.

Scope on top of tier 2:

- Publish-time IL rewrite (or source-generator) in `@carbide/core` that rewrites user assemblies' `Console.ReadKey`, `Console.ForegroundColor`, `Console.SetCursorPosition`, `Console.WindowWidth`, `Console.Clear`, `Console.Title` to their `CarbideConsole` counterparts. *Or*, Option B (worker + SAB) if COOP/COEP is acceptable for the host page.
- `CancelKeyPress` event parity.
- `Console.Clear` parity.
- `GetCursorPosition` parity (DSR reply path).
- ANSI-aware `SpectreConsole`, `Figgle`, `Pastel`, `ConsoleTables` smoke tests.

### 10.4 Explicitly out of scope (at every tier)

- Real stdin pipe redirection from *outside* the browser tab — no filesystem, no OS pipe.
- Real child-process spawning (`Process.Start`) — Mono-WASM has no process model.
- Native terminal sequences that require the Windows Console API surface (`ReadConsoleInput` record events with mouse-wheel clicks, focus events) beyond what xterm.js already parses.
- Virtual terminal sequence *emission* in Windows-console-API-only mode (pre-VT-enabled behavior) — doesn't apply; we're always VT.
- Screen-reader semantics for assistive tech beyond what xterm.js itself provides.

## 11. Carbide-side changes, enumerated

### 11.1 New TypeScript package (or sub-module)

Either a new `@carbide/terminal` package depending on `@carbide/core`, or a sub-entry in `@carbide/core` (e.g. `@carbide/core/terminal`). Favor the sub-entry for now; factor out later if bundle size ever argues for it.

Files (indicative):

- `packages/core/src/ts/terminal/index.ts` — public exports: `runInteractive`, `TerminalOptions`, `TerminalSession`.
- `packages/core/src/ts/terminal/session.ts` — glues `@xterm/xterm` to the Carbide project.
- `packages/core/src/ts/terminal/line-editor.ts` — local-echo-style line editor (or thin wrapper over `local-echo`).
- `packages/core/src/ts/terminal/key-encoder.ts` — maps xterm `onData` strings to C#-consumable "Key" events (for `ReadKey`).
- `packages/core/src/ts/terminal/bridge.ts` — installs `globalThis.Carbide.Terminal.*` pointing at whatever xterm instance is active; teardown on run-complete.

### 11.2 TypeScript API additions

On `Project`:

```ts
interface InteractiveRunOptions {
    terminal: import("@xterm/xterm").Terminal;   // external dep, peer
    args?: readonly string[];
    signal?: AbortSignal;                          // caller-triggered cancel
    ctrlCMode?: "signal" | "byte";                 // default "signal"
    stderrStyle?: "plain" | "dim" | "red";         // default "plain"
    onExit?: (exit: { code: number; err?: string }) => void;
}

class Project {
    // existing run() unchanged
    runInteractive(options: InteractiveRunOptions): TerminalSession;
}

interface TerminalSession {
    readonly exitPromise: Promise<RunResult>;
    cancel(): void;             // triggers signal option on SignalIn
    dispose(): void;             // best-effort teardown; unwires terminal bridges
}
```

`TerminalSession` intentionally returns immediately; the exit is observed via `await session.exitPromise`. That lets the caller keep a handle to cancel or tear down mid-run.

### 11.3 C# surface additions

New files under `packages/core/src/Terminal/`:

- `CarbideTerminalInterop.cs` — the `[JSImport]`/`[JSExport]` boundary.
- `StreamingStdOutWriter.cs` — the `TextWriter` that flushes to JS.
- `BrowserTerminalReader.cs` — the `TextReader` backed by an async JS pump.
- `KeyParser.cs` — ported from `lib/dotnet/runtime/src/libraries/System.Console/src/System/IO/KeyParser.cs`.
- `CarbideConsole.cs` — public static class exposed to user code: `ReadKeyAsync`, `ReadLineAsync`, `ForegroundColor`, `BackgroundColor`, `ResetColor`, `SetCursorPosition`, `GetCursorPosition`, `WindowWidth`, `WindowHeight`, `Title`, `Clear`.
- `TerminalRun.cs` — new run path, parallel to `ProjectCompiler.RunAsync`, that:
  - installs `StreamingStdOutWriter` / `StreamingStdErrWriter`;
  - installs `BrowserTerminalReader` into `s_in` (reuse `SetConsoleInField`);
  - hooks up `NotifyResize` state;
  - invokes user entry point;
  - on completion flushes and tears down.

Existing `ProjectCompiler.RunAsync` stays as-is for the non-interactive path.

### 11.4 New `[JSExport]` methods

In `CompilationInterop.cs` (or a sibling `TerminalInterop.cs`):

```csharp
[JSExport] public static Task<string> RunInteractiveAsync(string projectId, string terminalConfigJson);
[JSExport] public static void DeliverStdIn(string projectId, string data);
[JSExport] public static void NotifyResize(string projectId, int cols, int rows);
[JSExport] public static void DeliverSignal(string projectId, string signalName);   // "SIGINT"
```

Schema version bumps from 3 → 4; validator extended.

### 11.5 New `[JSImport]` methods

```csharp
[JSImport("globalThis.Carbide.Terminal.write")]
internal static partial void WriteStdOut(string text);

[JSImport("globalThis.Carbide.Terminal.writeErr")]
internal static partial void WriteStdErr(string text);

[JSImport("globalThis.Carbide.Terminal.readLineAsync")]
internal static partial Task<string?> ReadLineAsync();

[JSImport("globalThis.Carbide.Terminal.readKeyAsync")]
internal static partial Task<string> ReadKeyAsync();

[JSImport("globalThis.Carbide.Terminal.emitTitle")]
internal static partial void EmitTitle(string text);
```

### 11.6 Browser host adapter extensions

`BrowserHostAdapter` gets a way to receive the emscripten `print`/`printErr` overrides. Two shapes:

1. Add `resolveRuntimeConfigOverlays(): Promise<Partial<MonoConfig & DotnetModule>>` to the `HostAdapter` interface, and let the browser adapter return `{ print, printErr }`.
2. Do the override in `bootRuntime` directly for browser mode.

(1) is the cleaner extension point and parallels the existing `resolveFrameworkAssetsBaseUrl` / `resolveReferencePack` pattern. The dispatch is: when `runInteractive` is called, the adapter *replaces* `print` with a function that relays into the terminal bridge; on teardown, restores the previous.

### 11.7 Dependencies

New peer dependencies declared on `@carbide/core`:

- `@xterm/xterm` ≥ 5.5 (MIT)
- `@xterm/addon-fit` (MIT) — optional but recommended; host can supply its own
- optional: `local-echo` (MIT fork) for line mode

Peer, not direct — the host page owns the xterm lifecycle and can choose versions. The Node adapter is unaffected; attempting `runInteractive` from the Node adapter throws `NotSupportedException` (xterm requires DOM).

### 11.8 Documentation

- New fixture under `packages/core/test/browser/`: `interactive.html` — embeds xterm.js, loads Carbide, runs a small program that prompts + writes + reads.
- A new section in `docs/Carbide-Current-State-Guide.md` ("Interactive terminal"). Normative list of which `System.Console` members work and which require `CarbideConsole`.
- A `drift/` entry per tier for what deviates from conhost (async-only `ReadLine`, `CarbideConsole.*` namespace, etc.).

### 11.9 Testing strategy

- **Node:** `@carbide/core` tests for the new `TextWriter`/`TextReader` with a fake JS bridge — cover line buffering, flush timing, encoding edge cases.
- **Browser (Playwright):**
  - `interactive-hello.spec.mjs`: program writes 100 lines, terminal state asserts that all are visible.
  - `interactive-readline.spec.mjs`: program prompts, test drives `terminal.paste("abc\n")`, asserts echo and post-`ReadLine` output.
  - `interactive-ansi.spec.mjs`: program emits `\x1b[31mRED\x1b[0m`, asserts xterm's parsed state (foreground-color attr on the `RED` cells).
  - `interactive-resize.spec.mjs`: resize the viewport, assert `Console.WindowWidth` sees the new value.
  - `interactive-ctrlc.spec.mjs`: send `\x03`, assert program's `CancelKeyPress` handler ran.
  - `interactive-color-api.spec.mjs`: program uses `CarbideConsole.ForegroundColor = ConsoleColor.Red`, asserts the same xterm-parsed state as the ANSI path.
- **CLI:** tier 1 doesn't touch the CLI. Tier 2/3 could add `carbide run --interactive` that spawns a headless terminal shim — low priority.
- **Regression:** ensure existing `project.run()` behavior is unchanged (interactive path is strictly additive).

## 12. Risks and unknowns

| # | Risk | Severity | Mitigation |
|---|---|---|---|
| 1 | `[JSImport]` with `Task<T>` return interacts with Mono-WASM's asyncify in surprising ways (stack unwind, exception propagation across awaits) | Medium | Write a minimal reproducer early; rely on known-good patterns from Blazor + HttpClient (which do the same thing). |
| 2 | User code that calls synchronous `Console.ReadLine()` silently hangs instead of failing loudly | Medium | Install a `BrowserTerminalReader` whose synchronous `Read*` methods throw a pointed exception immediately, never block. |
| 3 | `Console.OpenStandardOutput` bypass leaks bytes to browser devtools console instead of xterm | Low | Override emscripten `print`/`printErr` at boot (§7.1); worst-case leave as a documented quirk. |
| 4 | ANSI sequences from user code interleave with Carbide's own "prompt" / "line-editor" bytes and confuse xterm | Medium | Gate line-editor output with an `OUTBOUND` vs `INBOUND` channel discipline; never echo while the user program holds the output. |
| 5 | High-volume writes (logs, benchmarks) overflow the flush buffer and lag the terminal | Low | Time-bounded flush (32 ms) + size-bounded (4 KB); backpressure absent because xterm.js's `write` is always synchronous. |
| 6 | Tier-3 worker + SAB path requires COOP/COEP, which breaks most real-world embeds | High (for tier 3 only) | Keep tier 3 optional; document requirement loudly. |
| 7 | `KeyParser.cs` port has edge cases (SS3 vs CSI, kitty-style modifiers, mouse events) | Low | Port upstream verbatim; carry upstream tests. Don't invent new key translations. |
| 8 | Ref-pack mismatch: `CarbideConsole` APIs must be present in the ref-pack for user compile to succeed | Low | Ship a tiny sibling reference DLL `Carbide.Console.Terminal.ref.dll` under `@carbide/refs-net10.0` (or a sibling pack). User code that `using Carbide.Terminal;` picks it up. |
| 9 | `Project.runInteractive` competes with concurrent `Project.build()` / `Project.run()` calls on the same project | Low | Serialize per-project access with a mutex in `SessionSolutions`; throw on reentrant `runInteractive` for the same project. |
| 10 | xterm.js is a large runtime dependency (~250 KB gzipped) | Low | Make it a peer dep; host chooses inclusion. |
| 11 | Interop chattiness tanks perf for single-char `Write('x')` calls in tight loops | Medium | Buffered `TextWriter` (§9.2). |
| 12 | Ctrl+C delivered mid-compile instead of mid-run | Low | Only wire the signal path once the user entry point has been invoked. |

## 13. Prior art and confirmation

Several projects confirm the pattern is tractable:

- **`cryptool-org/wasm-webterm`** — xterm.js addon that runs WASI / Emscripten WebAssembly binaries interactively. Uses Comlink-proxied callbacks between a worker and main thread for synchronous stdin. Confirms both Option A and Option B are deployable in practice.
- **Wasmer's `@wasmer/wasm-terminal`** — production-hardened browser terminal for WASI binaries; uses the same byte-pipe-plus-JS-shim pattern.
- **`XtermBlazor`** — Blazor + xterm.js wrapper with ~72k downloads; primarily a UI widget for Blazor apps rather than a Console-API polyfill, but confirms xterm + Mono-WASM coexist cleanly at the DOM level.
- **Wasmer docs: "Creating an Interactive Terminal with XTerm.js"** — reference implementation for the main-thread async Option A pattern.

None of them try to back `System.Console.ReadKey` specifically; the conhost-parity angle is where Carbide would add value. For tier 1 (output-only streaming) the path is thoroughly walked.

## 14. Recommendation

**Pursue tier 1.** 1–2 weeks of focused work yields a real browser-interactive Carbide demo: user writes `Console.WriteLine("\x1b[1;33mhello\x1b[0m");` in the editor, sees it yellow-bold in xterm.js. That alone enables a class of tooling demos (ASCII art, banner generators, log replayers, spectre/console previews) that the current buffered-run model cannot show. No spec bumps to users, no new sync/async story, no PNS patches.

**Decide tier 2 scope with Vladimir before starting.** The `CarbideConsole.*Async` vs patched-`Console.*` question has a different cost-benefit depending on how much conhost-parity matters. My default recommendation is `CarbideConsole.*` (explicit, honest, no publish-time weaver), but "strict parity" is also defensible and documented in §7.4.

**Defer tier 3 to a time when someone wants it.** Worker + SAB is real engineering and real operations (COOP/COEP). Nothing in the CLAUDE.md vision suggests Carbide is targeting shops where that's acceptable by default.

**Non-goals to lock in from day 1.** (a) No real filesystem/stdin pipe from outside the tab. (b) No child processes. (c) No SDK-level parity with `dotnet run` — Carbide's terminal story is the *runtime* terminal, not an interactive debugger or a full console-host replacement. These are the same non-goals [Carbide Current-State Guide §"What Carbide Is Not For"](../Carbide-Current-State-Guide.md) already commits to.

## 15. Appendix A: proposed `Carbide.Terminal` C# API sketch

```csharp
namespace Carbide.Terminal;

public static class CarbideConsole
{
    // Async equivalents — the blessed interactive APIs.
    public static Task<ConsoleKeyInfo> ReadKeyAsync(bool intercept = false, CancellationToken ct = default);
    public static Task<string?> ReadLineAsync(CancellationToken ct = default);

    // Mirrored state — reads always hit the cached JS-reported value.
    public static int WindowWidth { get; }
    public static int WindowHeight { get; }
    public static int BufferWidth => WindowWidth;   // buffer == window in a tty
    public static int BufferHeight => WindowHeight;

    // SGR emitters — sets future text attributes via ANSI on the current Console.Out.
    public static ConsoleColor ForegroundColor { get; set; }
    public static ConsoleColor BackgroundColor { get; set; }
    public static void ResetColor();

    // Cursor — CUP / DECTCEM / DECSCUSR emission.
    public static void SetCursorPosition(int left, int top);
    public static (int Left, int Top) GetCursorPosition();   // via DSR; tier 3
    public static bool CursorVisible { get; set; }

    // Title — OSC 0.
    public static string Title { set; }

    // Clear — ED + CUP home.
    public static void Clear();

    // Events.
    public static event EventHandler<(int Cols, int Rows)>? TerminalResized;
    public static event ConsoleCancelEventHandler? CancelKeyPress;

    // Escape hatch for advanced users: write raw bytes to the terminal bypassing any buffering.
    public static void WriteRaw(string sequence);
}
```

## 16. Appendix B: proposed TypeScript API sketch

```ts
import type { Terminal } from "@xterm/xterm";
import { CarbideSession } from "@carbide/core";

const session = await CarbideSession.initializeAsync();
const project = session.createProject({ assemblyName: "Demo" });

project.addSource("Program.cs", `
    using Carbide.Terminal;
    Console.WriteLine("Welcome to Carbide Terminal.");
    CarbideConsole.ForegroundColor = ConsoleColor.Green;
    Console.Write("name? ");
    CarbideConsole.ResetColor();
    var name = await CarbideConsole.ReadLineAsync();
    Console.WriteLine($"hello, {name}!");
`);

const terminal = new Terminal({ rows: 24, cols: 80 });
terminal.open(document.getElementById("terminal")!);

const runSession = project.runInteractive({ terminal });
const result = await runSession.exitPromise;
console.log("exit code:", result.exitCode);
```

## 17. Appendix C: the `print` / `printErr` override pattern

Indicative shape of the browser adapter change (`packages/core/src/ts/host/browser/browser-adapter.ts`):

```ts
resolveRuntimeConfigOverlays(): Promise<Partial<DotnetModule>> {
    return Promise.resolve({
        print: (text: string) => this._terminalSink?.writeStdOut(text + "\n"),
        printErr: (text: string) => this._terminalSink?.writeStdErr(text + "\n"),
    });
}
```

with `_terminalSink` set by `runInteractive` and cleared on teardown. Outside `runInteractive`, `_terminalSink` is null and emscripten's defaults apply (`console.log` / `console.error`).

## 18. Summary for decision

| Ask | Answer |
|---|---|
| Is this feasible? | Yes. No blocker is structural. Mono-WASM single-threading is the only real constraint, and it has a well-trodden workaround (async). |
| Roughly what cost? | Tier 1: 1–2 focused weeks. Tier 2: 4–8 weeks. Tier 3 (parity + optional worker+SAB): 2–4 months. |
| Biggest hidden cost? | Patching `System.Console` statics for conhost parity. Defers cleanly to tier 3 if we ship tier 2 with `CarbideConsole.*Async`. |
| Biggest non-obvious benefit? | Fixing the existing U1-era "raw bytes leaked to stdout" quirk via the `print`/`printErr` overlay is effectively free at tier 1. |
| Recommended next step if greenlit? | Spike tier 1: a 40-line C# `StreamingStdOutWriter`, a 60-line TypeScript `TerminalSession`, a Playwright fixture that runs `Console.WriteLine` + ANSI color. One merge. Demo. Decide tier 2 scope from there. |

Vladimir — this one is a clean extension. Carbide's existing seams (the host adapter, the `Console.SetOut` path, the reflection-based `s_in` install, the JSExport surface) already anticipate everything we'd need. The single piece of genuinely-new engineering is the `StreamingStdOutWriter` + `BrowserTerminalReader` pair and the JSImport bridge behind them. Tier 1 is a plausible weekend hack; tier 2 is the interesting design conversation; tier 3 is a "when we have a real user" decision.
