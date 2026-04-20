Below is a consolidated research report based on:

* The three internal T21 reports you pointed me at (investigation, prior art, follow-up).
* A code/architecture review across `packages/core`, the browser terminal bridge, and the forked `System.Console`.
* Web research about the specific exception (“Cannot wait on monitors on this runtime”), .NET WASM hosting modes, and JSImport/JSExport async semantics.

---

# Carbide T21 research report (Console API in the browser)

## 1) What the “roadblock” actually is

### Symptom (as reported)

The initial attempt to run a browser-hosted “console-style” C# app (carbide-gh demo) hit a failure that *looks like*:

* The program prints prompts and then, at the first `await` that actually suspends, it throws:

  * `PlatformNotSupportedException: Cannot wait on monitors on this runtime`

The reports interpret this as: “async suspension is fundamentally broken in our current hosting model (JSExport-driven, no managed `Main`), therefore interactive console apps aren’t viable without major changes.”

### Reality check: what that exception means on .NET WASM

That exception is a *known, expected runtime guardrail* in single-threaded WebAssembly: if anything tries to **block** (e.g., `Monitor.Wait`, `Task.Wait`, `.Result`, `WaitHandle.WaitOne`, many sync-over-async patterns), the runtime throws because blocking would deadlock the only JS thread / event loop. This is not Carbide-specific; it’s a generic “you’re doing a blocking wait” signal on that runtime. ([GitHub][1])

So the important question becomes:

> **Where is the blocking wait coming from?**
> Is it truly `await` itself, or some hidden sync wait triggered by a scheduler / marshaller / runtime init path / library code?

### Key correction to the “any suspension is impossible” framing

Modern .NET-in-the-browser absolutely supports async/await, timers, promises→Tasks, etc., when hosted the “normal” way. The official docs show `JSImport` returning a JS `Promise` and being awaited as a `Task`, including timers like `setTimeout`. ([Microsoft Learn][2])
And practical examples of “no Blazor, just WASM” show ordinary `Task.Delay` running fine inside a managed `Main` started via `runMain`. ([Andrew Lock | .NET Escapades][3])

So if Carbide hits this exception during *ordinary* `await`, it is overwhelmingly likely to be:

* a **hosting-mode mismatch** (how Carbide boots/dispatches managed work),
* a **scheduler/sync-context choice** that triggers a sync wait internally,
* or a **specific interop path** (e.g., delegate callbacks, promise marshalling, threadpool/timer pump) that is not properly initialized in “library mode”.

That makes the roadblock **real** (it breaks your target scenario) but probably **not fundamental** (there should be a simpler fix than “rewrite everything” or “threads-only”).

---

## 2) What the Carbide repo already proves (and what it doesn’t)

### A) Line-based interactive input is already *architecturally solved*

Carbide’s terminal model is sane:

* JS line editor buffers keystrokes, echoes locally, and calls `CompilationInterop.DeliverStdIn(projectId, line)` on Enter.
* C# holds a `BrowserTerminalReader` that exposes `ReadLineAsync`/`ReadAsync` and completes a `TaskCompletionSource` when lines/bytes arrive.
* `ProjectCompiler.RunInteractiveAsync` installs that reader into `System.Console` by **direct field injection** (bypassing `Console.SetIn` synchronized wrappers).

This general pattern is exactly how people implement “console-like” UX in a browser: you don’t use real stdin; you emulate it with a `TextReader` and feed it from JS.

### B) You have a known baseline: .NET’s own Console I/O in WASM is *not* what you want

Historically `Console.ReadLine()` in WASM was wired to `window.prompt()` and had correctness issues (e.g., infinite loop reports). ([GitHub][4])
That’s useful as prior art: *everyone* ends up doing custom terminal I/O for browser console UX.

### C) The failures are concentrated around “runtime-driven scheduling” surfaces

From your follow-up report + the skipped browser fixtures, the “works vs fails” split correlates with:

* **Works:** a direct “JSExport → complete TCS → continuation runs” path (ReadLine-like).
* **Fails:** operations that require runtime timer/threadpool plumbing or more subtle callback marshalling (Delay, maybe resize/cancel/readkey depending on the exact path).

That strongly suggests the roadblock isn’t “Console is impossible”, but “the current host dispatch / async pump isn’t equivalent to the normal `runMain` hosting flow.”

---

## 3) Code-level findings: the most suspicious hot spots

I’ll focus on *things that can plausibly trigger a hidden blocking wait* on WASM.

### 3.1 Carbide’s runtime boot never calls `runtime.runMain`

In `packages/core/src/ts/boot.ts`, the runtime is created, and you immediately use `getAssemblyExports` + JSExports. There is no managed `Main` started and no “forever loop” in managed land.

Compare that to official and community examples: they call `runMain` to start the app (even if the “app” is mostly APIs), and that’s where the runtime installs its “main-thread” execution model and pumps queued work. ([Andrew Lock | .NET Escapades][3])

**Consequence:** You’re in a “library-style embedding mode.” That’s supported, but it is exactly where subtle differences show up: timers/threadpool background exec, dispatcher, and interop call queue semantics can differ.

This is the single biggest architectural delta vs. known-good patterns.

### 3.2 `CompilationInterop.*Async` uses `ConfigureAwait(false)` in JSExport entrypoints

In `packages/core/src/CompilationInterop.cs`:

* `RunInteractiveAsync` does:

  ```csharp
  var result = await Host.Dispatch(...).ConfigureAwait(false);
  ```
* Similar for `RunAsync`, `BuildAsync`, etc.

If `Host.Dispatch(...RunInteractiveAsync...)` suspends (and for interactive it *must*), then the continuation after the await will resume on the default scheduler instead of any installed sync context.

On WASM, “default scheduler” behavior depends heavily on whether the runtime’s background job pump / threadpool emulation is initialized and how re-entrancy is handled. If that path triggers any sort of blocking wait internally, you’ll get your exact PNSE.

This is particularly eyebrow-raising because elsewhere (in `ProjectCompiler.RunInteractiveAsync`) you explicitly avoid `ConfigureAwait(false)` and even comment about it due to this exact issue class. So the JSExport front door still has a footgun even if the inner host code is careful.

**This is one of the simplest “try it first” fixes.**

### 3.3 Delegate-callback JSImport is a different interop path than JSExport

Your `CarbideConsole.DelayAsync` calls a JSImport `DelayCallback(ms, Action callback)`. That relies on marshaling a managed delegate into JS and JS calling it later.

The Microsoft docs explicitly support callback patterns and Promise→Task patterns in WASM. ([Microsoft Learn][2])
So in principle this should be fine — but it is *not* the same codepath as JSExport calls. If one of them is mis-initialized in library mode, you can see “ReadLine works, Delay doesn’t”.

That observation (if accurate in the actual failing runs) points to interop-mode inconsistency, not “await is broken.”

---

## 4) Web research: what others say (and how it maps to Carbide)

### 4.1 “Cannot wait on monitors…” is almost always “something is blocking”

dotnet/runtime issues demonstrate the same exception when code uses parallel/blocking mechanisms like `Parallel.For` (which internally blocks/waits). ([GitHub][1])
This isn’t just “don’t call Monitor.Wait”; it’s “avoid *any* library path that does hidden sync waits.”

So for Carbide’s long-term goal (“run arbitrary console libraries”), this matters:

* Any library that does sync-over-async or uses blocking coordination will fail in single-threaded WASM unless you move execution off the JS thread (threads/WebWorker) or you have a stack-switching solution.

### 4.2 Hosting mode matters: `runMain` is the “normal” place where async plumbing is correct

Andrew Lock’s “run .NET WASM without Blazor” flow uses `runMain` and shows ordinary async constructs working (including `Task.Delay`). ([Andrew Lock | .NET Escapades][3])
That supports a very actionable hypothesis:

> Carbide’s “library mode + JSExport” flow is missing (or fighting) some of the runtime’s expected initialization / dispatch semantics.

### 4.3 JSImport/JSExport supports Tasks/Promises — so a pure “async is impossible” conclusion doesn’t fit

Microsoft’s docs (updated in 2026) show Promise-returning JSImport methods awaited as Tasks, and demonstrate that `setTimeout` integration is normal. ([Microsoft Learn][2])
So if Carbide currently cannot use “timer completes later → resume continuation”, the fix is almost certainly in how Carbide hosts / schedules, not in the platform’s capability.

### 4.4 Console input in WASM has a long history of being “not real stdin”

Mono’s long-standing issues around `Console.ReadLine()` in WASM (prompt-based, buggy) reinforce that any serious “console in browser” implementation uses custom plumbing. ([GitHub][4])

Carbide’s approach (xterm + `TextReader`) is aligned with the ecosystem; the failure is “integration correctness”, not “wrong idea.”

---

## 5) Is T21 as significant as represented?

### It depends what “success” means.

#### If success means:

**“Run *this* kind of app: line-based REPL, using async ReadLine-ish APIs, minimal timers, minimal fancy TUI”**

Then T21 is **not** a hard blocker. Carbide already has most of what’s needed; the remaining failures are likely fixable with relatively contained host/scheduler changes.

#### If success means:

**“Run arbitrary console apps / TUI libraries (Spectre.Console et al.) unmodified in the browser”**

Then T21 is **still significant**, but for *two different reasons*:

1. **Your current hosting/scheduling bug(s)** (the immediate roadblock).
2. **Fundamental single-threaded WASM limits**: many console libraries do hidden blocking waits, sync key reads, etc. Those will always trip the “Cannot wait on monitors…” guardrail unless you:

   * run on WASM threads / WebWorker with cross-origin isolation, or
   * accept patching/shimming libraries, or
   * use emerging stack-switching / JSPI-ish mechanisms (browser dependent).

So: the internal reports slightly overstate the “async is dead” part, but they correctly flag a **compatibility cliff** if the goal is “unmodified console ecosystem.”

---

## 6) Simpler workarounds than the reports proposed

Your reports trend toward “big” options (threads, deep runtime changes). Here are **smaller, high-leverage** options that I think you should try first, in descending order of “bang for complexity.”

### Workaround 1: Remove `ConfigureAwait(false)` from JSExport entrypoints that can suspend

**Why it’s promising:**
It’s a one-line change in a few places, and it directly targets the kind of hidden scheduling that can lead to monitor waits on WASM.

**Where:** `packages/core/src/CompilationInterop.cs`

* `RunInteractiveAsync`
* `RunAsync` (if it awaits program execution)
* potentially `InitAsync` and anything else that can actually yield

**Goal:** ensure continuations stay on the same “cooperative JS thread” context you control, instead of default scheduler paths.

If this fixes the roadblock, it’s the cleanest outcome: you keep your current architecture.

---

### Workaround 2: Start a managed `Main` via `runtime.runMain`, then run everything through a managed work queue

This is the most “conventional” embedding pattern and aligns with known-good examples. ([Andrew Lock | .NET Escapades][3])

**Conceptual model:**

* JS boots runtime.
* JS calls `runMain` on a Carbide.Core entrypoint.
* Carbide.Core `Main` sets up:

  * a `Channel<WorkItem>` or similar
  * captures whatever “main thread dispatcher” context the runtime installs
  * loops forever `await channel.Reader.ReadAsync()`, executes work items serially
* JSExport methods do **not** compile/run directly; they enqueue work items and return a Task that completes when processed.

**Why it helps:**

* You stop “executing large async programs directly inside a JSExport call stack.”
* You move execution into the runtime’s intended main-loop environment (timers/threadpool pump/dispatcher).
* You also get a natural place to implement a Blazor-like “call queue” to avoid reentrancy hazards.

**Why it’s still simpler than threads:**
No COOP/COEP, no worker, no SharedArrayBuffer, no multi-threaded runtime. Just a different organization of where work runs.

---

### Workaround 3: For timers/delays, avoid delegate-callback JSImport; use a pure JS→JSExport “deliver” completion

If `DelayCallback(ms, Action)` is part of the failing surface, replace it with the same pattern you already trust for stdin:

* Managed: allocate a delay id → store `TaskCompletionSource` in a dictionary.
* Managed calls a JS import `scheduleDelay(projectId, delayId, ms)` which does **only** `setTimeout(() => DeliverDelay(projectId, delayId), ms)`.
* JS calls back via a **JSExport** `DeliverDelay(projectId, delayId)` to complete the TCS.

That avoids:

* JS invoking a marshaled managed delegate callback (different interop path),
* any Promise→Task bridge,
* and keeps the “event completes Task” mechanism identical to stdin delivery.

Given the repo already has robust projectId registries and “deliver” JSExports, this is a pretty small change.

---

### Workaround 4: Make resize/cancel/key primitives “single waiter fields”, not event subscription + token registration

If (and only if) you confirm that `WaitForResizeAsync`/cancel paths are part of the failure, simplify them to the minimal “BrowserTerminalReader style” primitive:

* Store a single `_pendingResizeTcs` in `TerminalInputState`.
* On resize JSExport, if pending exists, complete it; else record latest size.
* `WaitForResizeAsync`:

  * if “resize already happened since last wait”, return synchronously,
  * else allocate TCS and await.

This removes:

* delegate event invocation lists
* `CancellationToken.Register` machinery
* extra allocations and subtle scheduling

It’s not pretty, but it’s the exact shape that is already proven for stdin.

---

## 7) Recommendations: what I would do next (in order)

### Phase 1 — smallest diffs, quickest signal

1. **Delete `ConfigureAwait(false)` in `CompilationInterop.RunInteractiveAsync`** (and any other JSExport that awaits long-running tasks).
2. Temporarily add a “diagnostic JSExport” that runs:

   * `await Task.Delay(1)`
   * `await new TaskCompletionSource().Task` that you complete from another JSExport
   * `await CarbideConsole.DelayAsync(1)`
     and logs *exactly* where the exception arises.
3. If this alone fixes the roadblock: re-enable the skipped fixtures progressively (resize/cancel/readkey) to identify the remaining gaps.

### Phase 2 — align hosting with standard WASM patterns

4. **Add a managed Main + `runMain`** and route compilation/run requests through a managed queue.

   * This is the most “canonical” and least magical long-term shape.

### Phase 3 — compatibility strategy decisions

5. Decide what “Console API compatibility” target you truly need:

   * **Async-only console** (ReadLineAsync/Write, key events async) → feasible in single-threaded WASM with careful scheduling.
   * **Full legacy sync console** (ReadKey/ReadLine blocking) → you likely need threads/worker or a stack-switching solution; otherwise you’ll forever fight Monitor.Wait guardrails.

---

## 8) Concrete code pointers (so you can act fast)

* **JS boot (no `runMain`)**: `packages/core/src/ts/boot.ts`
* **JSExport entrypoints using ConfigureAwait(false)**: `packages/core/src/CompilationInterop.cs`
* **Interactive runner**: `packages/core/src/Services/ProjectCompiler.cs` (`RunInteractiveAsync`)
* **Console shims**: `packages/core/src/Terminal/CarbideConsole.cs`, `BrowserTerminalReader.cs`, `TerminalInputState.cs`
* **Terminal bridging**: `packages/core/src/ts/terminal/session.ts`, `line-editor.ts`
* **Forked System.Console**: `packages/core-bcl/System.Console/src/*` (especially `Console.cs`, `ConsolePal.Browser.cs`)

---

# Bottom line

* The exception you’re seeing is not “mysterious”: on browser WASM it almost always means **some code path is attempting a blocking wait**. ([GitHub][1])
* The bleakest interpretation in the first report (“any suspension is broken”) conflicts with known-good .NET WASM behavior and with official interop guidance. ([Microsoft Learn][2])
* The most likely “simpler than threads” path is:

  * eliminate `ConfigureAwait(false)` on JSExport entrypoints,
  * and/or move execution into a managed `Main` started via `runMain` with a queue-based dispatcher.
* If your long-term goal is “run unmodified console/TUI libraries”, the roadblock isn’t only Carbide’s scheduling; it’s also the **inherent incompatibility of sync-blocking console patterns** with single-threaded WASM. ([GitHub][1])
  That’s where threads/worker become the clean compatibility escape hatch.

---

[1]: https://github.com/dotnet/runtime/issues/43411 "https://github.com/dotnet/runtime/issues/43411"
[2]: https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/?view=aspnetcore-10.0 "https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/?view=aspnetcore-10.0"
[3]: https://andrewlock.net/running-dotnet-in-the-browser-without-blazor/ "Running .NET in the browser without Blazor"
[4]: https://github.com/mono/mono/issues/9058 "https://github.com/mono/mono/issues/9058"
