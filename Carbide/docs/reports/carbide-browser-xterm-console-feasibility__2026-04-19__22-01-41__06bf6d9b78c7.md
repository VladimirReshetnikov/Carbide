# Feasibility: Browser-Hosted Interactive C# Console Apps In Carbide Via xterm.js

- Status: Informational (feasibility / architecture report)
- Audience: Carbide maintainers, reviewers, and future implementers of browser-hosted runtime features
- Scope: `src/Carbide` support for running console-shaped C# applications inside a browser page with embedded xterm.js, with interactive input/output and a substantial `System.Console` subset
- Created (UTC): 2026-04-19T22:01:41Z
- Repository HEAD: 43db73bda4ae735ad00fe7c40caab66f203d9dd0
- Related code:
  - `../../packages/core/src/Services/ProjectCompiler.cs`
  - `../../packages/core/src/CompilationInterop.cs`
  - `../../packages/core/src/ts/runtime/boot.ts`
  - `../../packages/core/src/ts/host/browser/browser-adapter.ts`
  - `../../../lib/dotnet/runtime/src/libraries/System.Console/src/System/Console.cs`
  - `../../../lib/dotnet/runtime/src/libraries/System.Console/src/System/ConsolePal.Browser.cs`
  - `../../../lib/dotnet/runtime/src/libraries/System.Console/src/System/ConsolePal.Unix.cs`
  - `../../../lib/dotnet/runtime/src/libraries/System.Console/src/System/IO/StdInReader.cs`
  - `../../../lib/dotnet/runtime/src/libraries/System.Console/src/System/IO/KeyParser.cs`
- Related docs:
  - [Carbide current-state guide](../Carbide-Current-State-Guide.md)
  - [Carbide JS↔C# interop bridge proposal](../proposals/carbide-js-interop-bridge-proposal__2026-04-18__22-00-00-000000__a2d2955163d1.md)
  - [Carbide Avalonia browser GUI feasibility report](../research/avalonia-ui/carbide-avalonia-browser-gui-integration__2026-04-18__21-52-50-185670__57c69d8c45e3.md)

## Executive summary

**Short answer:** yes, but only if Carbide grows a real browser-terminal runtime layer. xterm.js is strong enough to provide a credible VT terminal surface in a web page. Carbide's current browser execution model is not.

The crucial split is this:

- **ANSI/VT rendering, alternate screen, colors, cursor motion, resize reporting, and live output streaming are feasible with high confidence.** xterm.js already implements a large VT/xterm subset and exposes the hooks needed to feed it output and receive input.
- **Transparent support for most existing `System.Console` APIs is not a small extension of current Carbide.** Upstream .NET's browser `System.Console` implementation explicitly throws `PlatformNotSupportedException` for input, key, color, cursor, and window APIs, and Carbide currently layers additional buffered capture on top of that rather than replacing it.
- **True “feels like conhost.exe” behavior for blocking interactive apps is only credible if Carbide introduces a deeper execution architecture than today's one-shot `project.run()` path.** The browser main thread cannot simply block waiting for `ReadKey` / `ReadLine` user input while also servicing DOM and xterm events.

The most practical conclusion is:

1. **A VT-first browser terminal story is feasible now** and should be pursued first.
2. **A broad `System.Console` compatibility story is feasible only with a custom browser console implementation** that Carbide owns, likely coupled to a worker-backed or otherwise non-main-thread execution model.
3. **Full conhost parity is not a realistic goal.** A strong xterm-like terminal experience is realistic; Win32 console semantics are not.

## What Carbide has today

Carbide already has the pieces to compile and execute C# code in a browser-hosted Mono-WASM runtime, but not to host an interactive terminal session.

### Current browser/runtime shape

`@carbide/core` boots `dotnet.js`, finds the `[JSExport]` surface in `CompilationInterop`, loads metadata references, and exposes session/project APIs from TypeScript. In the browser host path, `BrowserHostAdapter` is intentionally thin: it mainly resolves `_framework/` URLs.

At execution time, `ProjectCompiler.RunAsync` does this:

1. compile the project into a PE in memory;
2. `Assembly.Load(byte[])` the emitted assembly;
3. redirect `Console.Out` and `Console.Error` to `StringWriter`s;
4. optionally pre-seed `Console.In` with a buffered `StringReader` using reflection on the internal `Console` field;
5. invoke the entry point; and
6. return a final `RunResult` JSON payload containing `stdOut`, `stdErr`, `exitCode`, diagnostics, and duration.

That architecture is excellent for bounded compile-and-run automation. It is not a terminal session model.

### Current gaps relative to an interactive browser terminal

Current Carbide does **not** provide:

- live stdout/stderr streaming to the browser host;
- live stdin that can arrive after execution starts;
- resize propagation from the page to the running app;
- per-key or mouse input events;
- a browser-visible terminal buffer/state model;
- trustworthy handle-level stdio interception (`Console.OpenStandardOutput()` / `OpenStandardInput()` remain special cases);
- execution isolation suitable for long-lived UI/TUI runs (today's `Assembly.Load(byte[])` path is session-scoped, not per-run collectible isolation).

As a result, today's browser story is closer to “run code in a tab and get the final strings back” than “host a console app inside a terminal widget.”

## Upstream runtime constraints that matter

The main difficulty is not xterm.js. It is the upstream browser implementation of `System.Console` and the browser event model.

### Browser `System.Console` is intentionally non-interactive today

In upstream `dotnet/runtime`, `System.Console` on the browser target delegates to `ConsolePal.Browser`. That implementation:

- provides stdout/stderr streams;
- reports UTF-8 output encoding; but
- throws `PlatformNotSupportedException` for `Console.In`, `ReadKey`, `TreatControlCAsInput`, colors, cursor operations, title, window-size queries, and most other interactive console APIs.

Carbide's current buffered stdin workaround only patches the internal `Console.In` field for pre-supplied text. It does not turn the browser runtime into an interactive console.

### `JSImport` / `JSExport` are main-thread-limited

The current .NET WebAssembly feature documentation explicitly states that JS interop via `[JSExport]` / `[JSImport]` is limited to the main thread even when multithreading is enabled, and warns that blocking the main thread with operations such as `Task.Wait` or `Monitor.Enter` is dangerous in browsers.

That matters because a large amount of existing console code assumes that `Console.ReadKey()` or `Console.ReadLine()` can synchronously block until the user types something.

### Browser multithreading is possible, but not free

The same upstream docs also note that browser multithreading requires `Cross-Origin-Embedder-Policy: require-corp` and `Cross-Origin-Opener-Policy: same-origin`, which means Carbide cannot silently switch to a worker-and-shared-memory design without also taking on hosting, deployment, and documentation consequences.

### The browser event loop is the hidden blocker

An embedded xterm.js terminal only receives keystrokes, paste events, and resize events while the browser event loop keeps running. A synchronous console app that blocks the main thread waiting for input prevents those events from being processed. This is the same core problem called out by browser-side PTY projects such as [`xterm-pty`](https://github.com/mame/xterm-pty): blocking interactive reads require either stack-suspension techniques (Asyncify in Emscripten) or a worker-backed execution model.

Carbide today has neither of those for arbitrary user C# code.

## What xterm.js can provide

xterm.js is not the limiting factor. It already provides most of the browser-side terminal surface Carbide would need.

### xterm.js capabilities relevant to Carbide

From the current xterm.js docs:

- `Terminal.write(...)` accepts strings or `Uint8Array` data and feeds the terminal parser/render pipeline.
- `Terminal.onData` provides user keystrokes as terminal input data.
- `Terminal.onBinary` exists for non-UTF-8-compatible binary input reports (notably some mouse protocols).
- `Terminal.onResize` and `Terminal.resize(cols, rows)` provide terminal geometry changes.
- xterm.js documents broad support for CSI/SGR/alternate-screen/mouse/cursor-style/device-status-report sequences.
- parser hooks (`registerCsiHandler`, `registerOscHandler`, etc.) allow the embedding host to intercept or extend sequences.
- window/report features are intentionally host-defined and security-gated; xterm exposes an `IWindowOptions` surface precisely because some terminal behaviors depend on the embedding environment.

### Why that matters

This means Carbide does **not** need to invent a browser terminal renderer, an ANSI parser, or a key-sequence vocabulary from scratch. xterm.js can already supply:

- visible terminal rendering;
- screen and scrollback buffers;
- alternate screen behavior;
- most ANSI styling and cursor movement semantics;
- input event capture; and
- host-controlled resize/title/window-report behaviors.

That is exactly the right foundation for a browser-hosted console runtime.

## Feasibility matrix

| Goal | Feasibility | Why |
| --- | --- | --- |
| Live stdout/stderr streaming into an embedded terminal | High | Carbide mainly needs a streaming session API; xterm already renders terminal output. |
| ANSI/VT colors and cursor movement | High | xterm supports the relevant VT/xterm sequences; Carbide mostly needs to stop buffering everything into final strings. |
| Alternate screen / full-screen TUI behavior | High | xterm supports alternate screen and related DECSET/DECRST sequences. |
| Terminal resize awareness | High | xterm exposes `cols`/`rows`, `resize`, `onResize`, and host-controlled window-report behavior. |
| Interactive line input for apps designed around a terminal stream | Medium-High | Feasible if Carbide introduces a session transport and input queue; harder if the app expects synchronous blocking on the browser main thread. |
| `Console.ReadKey` / `KeyAvailable` / Ctrl+C handling | Medium | Technically doable, but only with a custom console implementation and careful execution model. |
| `Console.ForegroundColor`, `ResetColor`, `Clear`, `SetCursorPosition`, `CursorVisible`, `Title` | Medium | These map naturally to ANSI/xterm semantics, but upstream browser `ConsolePal` does not implement them. |
| `Console.WindowWidth`, `WindowHeight`, `BufferWidth`, `BufferHeight` | Medium | Page terminal geometry can back these, but semantics differ from a real Win32 console buffer. |
| `Console.OpenStandardInput/Output/Error` parity | Medium-Low | Requires handle-level streams, not just `Console.SetOut` / `SetIn`. |
| Broad compatibility with existing console libraries compiled against `System.Console` | Medium-Low | Requires deeper runtime compatibility than a TS-side adapter alone. |
| Full conhost / Win32 console parity | Low / not realistic | Browser + xterm can approximate VT terminal behavior, not Win32 console host behavior. |

## The real architectural choice

There are three plausible implementation strategies, and Carbide should choose explicitly.

### Option A: VT-first terminal hosting without claiming `System.Console` parity

This option says:

- create an xterm-based browser runner;
- stream stdout/stderr live into xterm;
- feed xterm input back into the running program through a Carbide-defined terminal API; and
- support apps that already speak ANSI/VT or that are written against a Carbide-specific console abstraction.

This is the lowest-risk path and the fastest to ship.

Its limitation is also clear: existing code that calls `Console.ReadKey`, `Console.WindowWidth`, `Console.ForegroundColor`, etc. still does not transparently work.

### Option B: Rewrite user code to a Carbide console shim

This option keeps the stock browser runtime but rewrites user source or IL so that calls to `System.Console` become calls to a Carbide-owned shim.

Pros:

- avoids owning a fork of `System.Console` and the browser runtime;
- could work well for user source compiled directly by Carbide.

Cons:

- does not naturally cover third-party package DLLs already compiled against `System.Console`;
- is brittle around reflection, stack traces, and partial rewrites;
- turns console compatibility into a compiler transformation problem.

This is a plausible fallback for a narrow source-only experience, but it is not the strongest path if the goal really is “most of `System.Console` API.”

### Option C: Own a custom browser console runtime layer

This is the only option that honestly matches the stated goal.

The idea is:

- keep xterm.js as the browser-side terminal;
- replace or fork the browser-side `System.Console` implementation used by Carbide's runtime bundle; and
- back it with a browser terminal bridge that can supply input, output, cursor, color, title, and geometry semantics.

This is the highest-value path, but it is also the most expensive because Carbide would be taking ownership of a runtime-level compatibility surface that upstream .NET explicitly does not provide on browser.

## Why Option C is technically plausible

Even though upstream browser `ConsolePal` is minimal, .NET already contains a lot of reusable VT-oriented console logic on Unix.

### Reusable pieces in upstream `System.Console`

The Unix implementation already has:

- `StdInReader` for line editing and buffered terminal reads;
- `KeyParser` for translating VT/xterm-style input sequences into `ConsoleKeyInfo`;
- `TerminalFormatStrings` for ANSI capabilities such as colors, clear, cursor movement, title, CPR, and key mappings; and
- cursor/window-size caches and ANSI-emitting helpers in `ConsolePal.Unix`.

That suggests a custom browser console layer does **not** need to invent `ReadKey` semantics from zero. It can likely reuse the Unix/VT model conceptually, with browser-specific substitutes for:

- the underlying input/output streams;
- terminal-size queries;
- signal handling;
- terminfo discovery; and
- any operations that depend on a real POSIX tty.

### What cannot be reused unchanged

`ConsolePal.Unix` depends on things the browser does not have:

- `ioctl`/tty window-size queries;
- terminfo database discovery;
- POSIX signal behavior;
- stdin reads from a real terminal FD; and
- native terminal initialization routines.

So the right mental model is **reuse the algorithms and contracts, not the exact implementation**.

## Changes Carbide would need

If the goal is a serious browser terminal runner, the necessary work spans multiple Carbide layers.

### 1. Add a browser terminal package next to `@carbide/core`

Carbide needs an explicit browser terminal host package, for example:

- `@carbide/terminal-xterm`
- or `@carbide/browser-terminal`

Responsibilities:

- own xterm.js creation and configuration;
- own terminal DOM mounting and focus management;
- wire `onData`, `onBinary`, `onResize`, paste, and optional mouse handling;
- apply `@xterm/addon-fit` (or equivalent sizing logic);
- expose a durable session object rather than a one-shot `run()` call.

This package should be browser-focused and should not be forced into the Node host adapter abstraction.

### 2. Introduce an interactive run API separate from `Project.run()`

The current `Project.run()` contract is “give me a final JSON result.” That contract is wrong for terminal hosting.

Carbide should add something like:

```ts
const terminalRun = await project.runInteractive({
  terminalHost,
  args,
  environment,
});

terminalRun.onExit(...);
terminalRun.writeInput(...);
terminalRun.resize(cols, rows);
await terminalRun.dispose();
```

Key point: do **not** overload the existing buffered `run()` shape with terminal semantics. Keep the automation-friendly API and add a session-oriented interactive API next to it.

### 3. Add a duplex transport between JS and the managed runtime

The current `CompilationInterop.RunAsync(projectId, runOptionsJson)` call is a one-request/one-response JSON boundary. An interactive terminal needs a long-lived transport for:

- stdout chunks;
- stderr chunks;
- input bytes or key events;
- resize notifications;
- title updates / bell notifications / optional diagnostics; and
- final exit.

This can be implemented as:

- a queue-based polling bridge;
- callback registration through JS interop; or
- a more explicit session object exported from the runtime.

For browser-hosted terminals, queue-based or event-based session semantics are a much better fit than “return a final string.”

### 4. Replace buffered `Console.SetOut` capture with terminal-aware streaming

`ProjectCompiler.RunAsync` today redirects `Console.Out` / `Console.Error` to `StringWriter`s. For terminal mode, Carbide instead needs writers that:

- stream incrementally to the JS-side terminal transport;
- preserve ordering between stdout and stderr to the extent the chosen transport can express it; and
- still optionally accumulate a final transcript for logs/debugging.

Handle-level writes matter too. Browser terminal mode should not keep the current footgun where `Console.OpenStandardOutput()` bypasses the capture model.

### 5. Decide how `System.Console` compatibility is implemented

This is the biggest decision.

If Carbide wants transparent compatibility for most existing console code, it needs one of these:

- a custom `System.Console` browser implementation carried inside Carbide's runtime bundle; or
- a systematic rewrite/shim layer with clearly documented compatibility limits.

My recommendation is to treat the custom console runtime layer as the mainline target and the rewrite idea only as a contingency plan.

### 6. Likely move interactive execution off the browser main thread

For transparent blocking console reads, keeping the entire managed app on the page's main thread is the wrong shape.

A more realistic design is:

- xterm.js and DOM stay on the main thread;
- the managed program runs in a worker-backed WebAssembly runtime;
- input and output flow through a shared queue or explicit message bridge; and
- the main thread remains free to process terminal events.

This brings new requirements:

- COOP/COEP headers and SharedArrayBuffer-safe hosting;
- worker-aware boot logic in Carbide's browser host package;
- stricter documentation about where the browser runner can be hosted.

Without this step, Carbide should not promise transparent `Console.ReadKey` / `ReadLine` behavior for arbitrary existing programs.

### 7. Add terminal-state services to the browser host

For a useful `System.Console` subset, Carbide's browser host needs to supply at least:

- current `cols` / `rows`;
- cursor-position cache or CPR-backed query handling;
- color/style reset behavior;
- title updates;
- bell behavior;
- Ctrl+C policy;
- visibility/style of the cursor; and
- optional mouse-mode enablement.

This is where xterm's parser hooks and window options become useful: they let the host decide what window/report semantics to expose, rather than pretending that the browser itself is a Win32 or POSIX console.

### 8. Improve execution isolation for long-lived runs

Interactive terminal apps are often long-running. Carbide's current `Assembly.Load(byte[])` in the default runtime context is tolerable for short compile-and-run tasks, but it is a poor fit for many terminal sessions in a single page lifetime.

A browser terminal workstream should budget for:

- per-run isolation or reset semantics;
- deterministic disposal of session resources; and
- explicit rules for re-running the same project repeatedly in one page.

Even if a collectible unload path is not available on the browser target, Carbide should not ignore the lifecycle problem.

## API surface: what can realistically be supported

The following table reflects the most realistic target for a browser xterm-backed console surface.

| `System.Console` area | Realistic target | Notes |
| --- | --- | --- |
| `Write`, `WriteLine`, `Error.Write*` | Yes | Stream text/bytes to xterm. |
| ANSI text written directly by user code | Yes | xterm already renders it. |
| `ForegroundColor`, `BackgroundColor`, `ResetColor` | Yes | Emit ANSI equivalents. |
| `Clear` | Yes | Map to ANSI clear + scrollback clear policy. |
| `SetCursorPosition`, `CursorLeft`, `CursorTop`, `GetCursorPosition` | Mostly | Requires cursor cache and/or CPR support. xterm supports DSR/CPR. |
| `WindowWidth`, `WindowHeight` | Yes | Map to xterm `cols` / `rows`. |
| `BufferWidth`, `BufferHeight` | Partial | Browser terminal buffer semantics are not Win32 console buffer semantics. |
| `Read`, `ReadLine` | Partial to mostly | Feasible if execution model permits waiting for live input. |
| `ReadKey`, `KeyAvailable` | Partial to mostly | Feasible with VT key parsing and a non-main-thread-friendly design. |
| `TreatControlCAsInput`, `CancelKeyPress` | Partial | Host policy needed for Ctrl+C routing. |
| `Title` | Partial | Can update terminal/tab title, but semantics are host-defined. |
| `CursorVisible`, cursor style | Partial to mostly | xterm supports related sequences; exact parity is host-defined. |
| `Beep` | Partial | Host can play a bell or ignore it. |
| `OpenStandardInput/Output/Error` | Hard but possible | Needs real stream objects, not just `StringWriter` redirection. |
| `SetWindowSize`, `SetWindowPosition`, `MoveBufferArea`, `NumberLock`, `CapsLock` | No / out of scope | Not credible browser targets; do not promise them. |

## Recommended implementation plan

### Phase 0: make the target explicit

Decide that the target is **xterm-like browser terminal compatibility**, not “full Windows console host parity.” Document the non-goals up front.

### Phase 1: ship a VT-first browser terminal runner

Deliver:

- xterm.js integration package;
- `runInteractive(...)` browser API;
- live stdout/stderr streaming;
- user input forwarding;
- resize propagation;
- no claim yet that `Console.ReadKey` and friends transparently work.

This gets immediate value and exercises the browser terminal/session architecture.

### Phase 2: prove the execution model for live input

Build a focused spike around blocking input semantics:

- either worker-backed runtime + queues;
- or a deliberately limited cooperative-input model.

Do not begin by patching dozens of console APIs. First prove that a browser-hosted C# program can wait for input without freezing the page.

### Phase 3: add a custom browser console compatibility layer

Once the execution model is proven, implement the `System.Console` subset in earnest.

Priority order:

1. stdout/stderr stream path,
2. line input,
3. key input,
4. colors/clear/cursor/window-size,
5. title/bell/signal policy,
6. handle-level `OpenStandard*` behavior.

This is the point where Carbide either starts owning a patched browser `System.Console` runtime layer or commits to a rewrite-based alternative.

### Phase 4: harden with compatibility tests

Add Playwright-driven browser tests that compare Carbide behavior against expected console behavior for:

- ANSI colors and styles;
- cursor addressing and CPR;
- alternate screen entry/exit;
- line input and editing;
- `ReadKey` arrow/function/control-key sequences;
- resize handling;
- libraries that are terminal-heavy but still `System.Console`-based.

A useful acceptance bar is not byte-identical parity with conhost. It is that the same console app behaves plausibly and predictably in both places.

## Final recommendation

**Proceed, but with the right scope boundary.**

If the desired feature is:

- “embed xterm.js and let browser users interact with console-shaped C# apps,”

then Carbide absolutely can grow into that.

If the desired feature is:

- “make existing `System.Console` apps in the browser behave close to conhost.exe,”

then Carbide should still proceed, but only on the understanding that this is a **runtime workstream**, not a small TS-host enhancement.

The best path is:

1. ship an xterm-backed interactive runner first;
2. validate a worker-safe/live-input architecture second; and then
3. layer in a Carbide-owned browser console compatibility implementation.

That sequencing yields user-visible value early, preserves room to stop at the VT-first layer if it already satisfies most scenarios, and avoids committing to a fragile half-emulation of `System.Console` before the execution model is sound.

## External source notes

Primary external sources used for this report:

- [.NET WebAssembly features (`dotnet/runtime`)](https://raw.githubusercontent.com/dotnet/runtime/main/src/mono/wasm/features.md)
- [JavaScript `[JSImport]` / `[JSExport]` interop with a WebAssembly Browser App project](https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/wasm-browser-app?view=aspnetcore-10.0)
- [xterm.js `Terminal` API](https://xtermjs.org/docs/api/terminal/classes/terminal/)
- [xterm.js supported terminal sequences](https://xtermjs.org/docs/api/vtfeatures/)
- [xterm.js parser hooks guide](https://xtermjs.org/docs/guides/hooks/)
- [xterm.js `IWindowOptions` API](https://xtermjs.org/docs/api/terminal/interfaces/iwindowoptions/)
- [`mame/xterm-pty` README](https://github.com/mame/xterm-pty)

Repository-local source evidence was used for all Carbide-specific and runtime-implementation-specific claims.
