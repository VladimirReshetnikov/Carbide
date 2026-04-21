# Carbide T2.1 — `runMain` + managed work queue experiment (disproved)

- Created (UTC): 2026-04-21T00:36:24Z
- Repository HEAD: 490067b18f2c2e571374052c70599176090c5ccb

Status: **experiment disproved. The external [`await-issue-report.md`](external/await-issue-report.md)'s Workaround 2 — "start a managed `Main` via `runtime.runMain` and route every JSExport through a managed work queue" — was implemented and tested. It does not fix T2.1. Reverted. This report records the experiment so the path isn't re-tried.**

Companion to the earlier [dispatcher experiment report](carbide-T21-dispatcher-experiment__2026-04-20__19-30-00-000000.md), which showed that Blazor's `RendererSynchronizationContext` alone doesn't work either.

## The hypothesis tested

The external report ([`docs/reports/external/await-issue-report.md`](external/await-issue-report.md)) identified Carbide's single biggest architectural delta vs the canonical Mono-WASM-browser hosting pattern:

> "In `packages/core/src/ts/boot.ts`, the runtime is created, and you immediately use `getAssemblyExports` + JSExports. There is no managed `Main` started and no 'forever loop' in managed land."
>
> "You're in a 'library-style embedding mode.' That's supported, but it is exactly where subtle differences show up: timers/threadpool background exec, dispatcher, and interop call queue semantics can differ."

Phase 2 of the report's remediation plan was: call `runtime.runMain(mainAssemblyName, [])` fire-and-forget, have `Main` sit forever in an `await foreach` over a `Channel<Func<Task>>` work queue, and route JSExport async entry points (`RunAsync`, `RunInteractiveAsync`, etc.) through `queue.EnqueueAsync(work)` so all their work runs inside `Main`'s execution-context lineage.

The intuition: earlier experiments (see the [empirical pinpoint report](carbide-T21-empirical-pinpoint__2026-04-20__18-51-01-000000.md)'s §appendix test 3) showed `await Task.Delay(50)` **works** when called from Carbide.Core's own `Main` (invoked via `runtime.runMain(...)`) but **fails** when called from user code loaded via `Assembly.Load(byte[])` and invoked from a JSExport. Phase 2 was: give every JSExport-initiated run the same "inside `Main`'s scope" property by routing through the queue.

## What was implemented

- New file `packages/core/src/Hosting/MainLoop.cs`: a static class with `EnqueueAsync<T>(Func<Task<T>>)` / `EnqueueAsync(Func<Task>)` that writes to an unbounded `Channel<Func<Task>>`, plus `RunAsync()` that sits in an `await foreach (var item in channel.Reader.ReadAllAsync())` loop and `await item()`s each one serially.
- `packages/core/src/Program.cs`: `Main` changed to `return MainLoop.RunAsync();`.
- `packages/core/src/ts/runtime/boot.ts`: after `await interop.InitAsync(assemblyUrls)`, fire-and-forget `runtime.runMain(mainAssemblyName, []).catch(...)`, then `await new Promise(r => setTimeout(r, 0))` to yield a macrotask so Main reaches its first channel read.
- `packages/core/src/CompilationInterop.cs`: `RunAsync` and `RunInteractiveAsync` JSExports rewritten to `return MainLoop.EnqueueAsync(async () => { ... })` instead of running the dispatch inline.
- A `DebugMainLoopState` JSExport + `AppContext`-backed counters (`Carbide.MainLoop.Dequeued`, `Carbide.MainLoop.Completed`) so the test probe could read back whether MainLoop was actually processing.

## The test

Fixture: [`packages/core/test/browser/t21-matrix-probe.html`](../../packages/core/test/browser/t21-matrix-probe.html) — probe `PAIR-A-late`.

```csharp
using System;
using System.Threading.Tasks;
Console.WriteLine("ready");
var line = await Console.In.ReadLineAsync();
Console.WriteLine("got: " + line);
```

Harness waits for `"ready\n"` in terminal, *then* delivers input. Canonical T2.1-tripping case.

## Result

**Still fails.** Same `PlatformNotSupportedException: Cannot wait on monitors on this runtime` at the same frame (`Carbide.Core.Services.ProjectCompiler.RunInteractiveAsync`).

But the diagnostic counters make the picture clearer than it was before:

```
DebugMainLoopState() → "State=started; Dequeued=1; Completed=1; LastFault=<none>"
```

So:

- `Main` **did** run (state reached `"started"`).
- Main **did** dequeue the work item that `CompilationInterop.RunInteractiveAsync` enqueued (`Dequeued=1`).
- The work item **did** complete from Main's perspective (`Completed=1; LastFault=<none>`) — the item's wrapped async lambda caught the PNSE inside Carbide's own try/catch and returned the RunResult as a failure. Main saw a success from its await perspective; the fault was already funnelled into the caller's TaskCompletionSource.

That is: **the trap still fired inside user code, but from Main's perspective the queue is pumping normally**. Moving JSExport work into `Main`'s lineage did not protect user-code awaits.

The terminal buffer captured a bit more than the clean stdOut/stdErr split reports:

```
System.PlatformNotSupportedException: Cannot wait on monitors on this runtime.
   at Carbide.Core.Services.ProjectCompiler.RunInteractiveAsync(String projectId, InteractiveOptions options)
ready

got:

```

User code reached `Console.WriteLine("got: ")` after the PNSE had fired — meaning `Console.In.ReadLineAsync()` returned `null` (the `BrowserTerminalReader`'s `Complete()` was called in teardown and set the pending TCS to null). So user's state machine **did** resume, just with a null result, after Carbide's outer catch had already faulted out and teardown had run.

## What this narrows

Combined with the earlier empirical pinpoint ("Task.Delay in Carbide.Core Main works") and this experiment ("Task.Delay in a work item Main dequeues fails"), the runtime's "magic runMain scope" is narrower than the external report hypothesised:

- Awaits in `Main`'s **direct body** resume correctly. Confirmed by the earlier `AppContext`-probe test that put `await Task.Delay(50)` literally inside `Program.Main`.
- Awaits in **nested async lambdas dequeued from a channel inside `Main`** do **not** get the same protection, even though the whole thing is rooted in the `runMain(...)` invocation.

Whatever the Mono-WASM single-threaded runtime treats specially about `Main`'s execution context, it's not ExecutionContext flow (which would propagate across `await foreach`) and it's not `SynchronizationContext.Current` (we exhaustively tested that). It's something tied to the specific state machine that `runMain` is holding, and it doesn't survive being unwrapped into sibling async state machines — even one level deep.

## What's left untested

Three structural escape hatches remain; each is substantially more expensive than Phase 1/Phase 2 and was out of scope for this experiment:

1. **Restructure user-code execution so it runs as part of Main's body, not as a work item.** Concretely: instead of `MainLoop.EnqueueAsync(work)`, have JSExport post a "request" and `Main` has an explicit `switch`/dispatcher that expands into the request type and awaits directly. The request handler becomes a direct `await` in Main's own state machine, not a nested one. Feasibility: plausible, but the request-router has to handle every async shape (compile, build, run, run-interactive) by name, and each handler's awaits have to be inlined into Main's body. Would require ~200-400 LOC of boilerplate and is brittle to future API additions.

2. **Host Carbide inside a Blazor component.** The [workarounds research](carbide-T21-workarounds-research__2026-04-20__18-55-00-000000.md) cited Strathweb / DotNetLab / BlazorFiddle as the shape that works. This is a multi-day restructure; Carbide stops being a vanilla `@carbide/core` ESM library and gains a Blazor hosting shell.

3. **Move user execution to a WebWorker** (SpawnDev.BlazorJS.WebWorkers pattern). A separate runtime instance per worker, with `postMessage` bridging. Full restructure of the run path; also a multi-day project.

Plus the longer-shot option:

4. **In-browser debugger break on the PNSE throw site with Mono-WASM debug symbols.** Would tell us exactly which BCL frame is calling `Monitor.Wait(INFINITE)` — the information nothing in this chain of experiments has yet revealed. The investigation report §9 already recommended this; it remains the single highest-value unexplored lead.

## Reverted state

Carbide.Core is back to the stable T2/T3 baseline:

- `Program.Main` is the empty synchronous method again.
- `MainLoop.cs` is deleted.
- `CompilationInterop.*Async` JSExports run inline with `.ConfigureAwait(false)` as before.
- `boot.ts` does not call `runtime.runMain`.
- `interactive-readline.spec.mjs` baseline: **PASS** (confirmed post-revert via `fixture-probe.mjs interactive-readline.html`).

Probe fixtures kept for future investigators:

- [`packages/core/test/browser/t21-matrix-probe.html`](../../packages/core/test/browser/t21-matrix-probe.html)
- [`packages/core/test/browser/t21-diagnostic-probe.html`](../../packages/core/test/browser/t21-diagnostic-probe.html)
- [`packages/core/test/browser/interactive-await-suspend-probe.html`](../../packages/core/test/browser/interactive-await-suspend-probe.html)
- [`packages/core/test/browser/await-suspend-noninteractive-probe.html`](../../packages/core/test/browser/await-suspend-noninteractive-probe.html)

## Takeaway updates for the report chain

- The [original investigation](carbide-T21-investigation-report__2026-04-20__17-11-37-000000.md)'s narrow "any await that genuinely suspends trips" claim stands: still not falsified by any experiment.
- The [empirical pinpoint](carbide-T21-empirical-pinpoint__2026-04-20__18-51-01-000000.md) stands: sharpened further — "genuinely suspends" means "the Task is not already complete when the awaiter's `IsCompleted` is checked, *and* the code is not in the specific state-machine that runMain is awaiting."
- The [workarounds research](carbide-T21-workarounds-research__2026-04-20__18-55-00-000000.md)'s primary dispatcher-scope hypothesis: disproved by the earlier [dispatcher experiment](carbide-T21-dispatcher-experiment__2026-04-20__19-30-00-000000.md).
- The [external await-issue report](external/await-issue-report.md)'s Workaround 2 (managed `Main` + work queue): **disproved by this experiment**.
- The original investigation's five options (A–E) remain the live decision set. Of them, Option B (multi-threaded runtime with COOP/COEP) is still the most-likely-to-actually-work escape hatch.

## The single recommendation that survives every experiment so far

Attach a Chromium debugger to the failing probe with Mono-WASM debug symbols, break on exception, and walk up the managed stack at the PNSE throw site. That's how this investigation eventually terminates in a concrete answer. Every structural "fix this architecturally" experiment has now been tried and failed; the actual BCL frame that's calling `Monitor.Wait(INFINITE)` is the missing piece, and no amount of reasoning from outside will substitute for reading it.
