# Carbide T2.1 — dispatcher-shim experiment (Blazor-style SC, disproved)

- Created (UTC): 2026-04-20T19:30:00Z
- Repository HEAD: e76335fb50d731aaf065ed253db7e1cc2f0a997a

Status: **experimental follow-up to [`carbide-T21-workarounds-research__...`](carbide-T21-workarounds-research__2026-04-20__18-55-00-000000.md). The cheapest prescribed test — clone Blazor's `RendererSynchronizationContext` and install it as Carbide's SC — was run. It did NOT fix T2.1. Reverted. This report records the experiment so future investigators don't re-do it.**

## The hypothesis tested

The workarounds research report hypothesized that Blazor's "awaits just work" property is rooted in its `RenderHandle.Dispatcher` — specifically the `RendererSynchronizationContext` whose `Post` chains each callback as a Task continuation on a serialized `_taskQueue` with `ConfigureAwaitOptions.ForceYielding`. Carbide's `CarbideSyncContext.Post` runs inline; the research suggested that switching to the Blazor-style pattern might unblock user-code `await`s that suspend and resume from JSExport-delivered completions.

## What was changed

- [`packages/core/src/Terminal/CarbideSyncContext.cs`](../../packages/core/src/Terminal/CarbideSyncContext.cs) was rewritten as a verbatim-ish clone of [`dotnet/aspnetcore` `RendererSynchronizationContext`](https://github.com/dotnet/aspnetcore/blob/main/src/Components/Components/src/Rendering/RendererSynchronizationContext.cs), dropping Blazor-specific surface (UnhandledException event, blocking `Send` path) and keeping the core mechanism: `Post` chains callbacks via `_taskQueue = PostAsync(_taskQueue, d, state)`, where `PostAsync` awaits the antecedent with `ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding`.
- [`packages/core/src/Services/ProjectCompiler.cs`](../../packages/core/src/Services/ProjectCompiler.cs) was also adjusted to drop `.ConfigureAwait(false)` from both `RunAsync`'s and `RunInteractiveAsync`'s `await task` of the user-code Task, so the continuation captures the new SC.

## The test (PAIR-A-late)

Using [`packages/core/test/browser/t21-matrix-probe.html`](../../packages/core/test/browser/t21-matrix-probe.html)'s `PAIR-A-late` probe — a known-failing canonical case:

```csharp
using System;
using System.Threading.Tasks;
Console.WriteLine("ready");
var line = await Console.In.ReadLineAsync();
Console.WriteLine("got: " + line);
```

Harness waits for `"ready\n"` in the terminal (proving the state machine has reached the await), *then* delivers input. This is the probe shape that reliably trips T2.1 because user code genuinely suspends.

## Results

- With T2 inline-Post `CarbideSyncContext` + `.ConfigureAwait(false)` on Carbide's user-task await: **FAIL (PNSE)**. Baseline behaviour documented in the empirical pinpoint report.
- With Blazor-style `RendererSynchronizationContext` clone installed + `.ConfigureAwait(false)` kept: **FAIL (PNSE)**. Same error, same stack.
- With Blazor-style SC + Carbide's outer `await task` without `.ConfigureAwait(false)` (so continuation captures the new SC): **FAIL (PNSE)**. Same error, same stack.

User-code try/catch around the `await` never fires — neither "Task.Delay OK" nor "Task.Delay FAILED" are observed. The PNSE is not caught by managed user code; it escapes past user's try block entirely. The only handler that captures it is Carbide's outer async-method catch (`ProjectCompiler.RunInteractiveAsync`'s `catch (Exception ex)`).

A second diagnostic probe ([`packages/core/test/browser/t21-diagnostic-probe.html`](../../packages/core/test/browser/t21-diagnostic-probe.html)) confirmed: `stdOut` contains only `"before await\n"`. User's post-await paths (both the success branch and the catch branch with stack-walking) never run. The user-code state machine never resumes.

## What this means

The Blazor-SC-alone theory is **disproved** for Carbide's specific scenario. Either:

1. **The research's reasoning about the dispatcher "invariant" is incomplete.** Blazor's awaits may work because of something else the Blazor runtime establishes — a `RenderHandle`, an `IComponent` attachment, the `WebAssemblyRenderer`'s render queue, or the `BlazorWebAssemblyStartup`'s `StartAsync` wrapping — not solely because of `RendererSynchronizationContext`.
2. **The trap is inside a code path the SC never touches.** When user code calls `await Task.Delay(50)`, the state machine's `AwaitUnsafeOnCompleted` is invoked. That internally attaches a continuation to the Task.Delay task via `Task.UnsafeSetContinuationForAwait`, which on Mono-WASM single-threaded may take a path (e.g. `ThreadPool.UnsafeQueueUserWorkItemInternal` as the fallback for a completed task, or a `Timer` registration that blocks on the internal `_timerQueue` monitor) that ends up at `Monitor.Wait(INFINITE)` — before any SC is consulted.
3. **Assembly.Load'd code has a different ALC / execution context.** User code is loaded via `Assembly.Load(byte[])` and invoked via `MethodInfo.Invoke` from a JSExport. The main-assembly Main method runs under a different ALC/EC lineage that the runtime's internal scheduling recognizes; Assembly.Load'd user code does not.

Distinguishing between these requires the kind of in-browser debugger break the investigation report's §9 described — break at the `PlatformNotSupportedException` throw site with Mono-WASM debug symbols loaded, walk up the managed stack.

## What was NOT tested in this experiment (still open)

1. **Wrapping the `MethodInfo.Invoke` call in an explicit `CarbideDispatcher.InvokeAsync(Func<Task>)`** that mimics Blazor's `ComponentBase.InvokeAsync` pattern fully — not just the SC, but also the extra state-machine-box wrapping where `SendIfQuiescedOrElsePost` fires a *nested* async callback whose state machine suspends when user's task suspends. This is a bigger restructure than just swapping the SC.
2. **Hosting Carbide inside an actual Blazor component.** The workarounds research's strongest-signal finding (Strathweb shell, DotNetLab, BlazorFiddle all execute user code from Blazor component event handlers) implies this would work. Cost: Carbide has to become a Blazor app, which is a much deeper architectural change than we've been contemplating.
3. **Running Carbide inside a WebWorker** (the `SpawnDev.BlazorJS.WebWorkers` pattern). This sidesteps the trap entirely because the worker's own runtime instance has its own pumping loop. Also a big restructure.

Each of these remains a candidate next-experiment, but each requires substantially more work than "swap the SC". They are not cheap interventions.

## Reverted state

Carbide.Core is back to the stable T2/T3 baseline:

- `CarbideSyncContext.Post` runs callbacks inline (as before the experiment).
- `ProjectCompiler.RunAsync` and `RunInteractiveAsync` keep `.ConfigureAwait(false)` on user-code `await task` — matches the T2/T3 behavior that existed before this experiment.
- `interactive-readline.spec.mjs` baseline: **PASS** (confirmed post-revert).

The probe fixtures ([`t21-matrix-probe.html`](../../packages/core/test/browser/t21-matrix-probe.html), [`t21-diagnostic-probe.html`](../../packages/core/test/browser/t21-diagnostic-probe.html)) are kept for future investigators.

## Takeaway updates for the report chain

- The [empirical pinpoint](carbide-T21-empirical-pinpoint__2026-04-20__18-51-01-000000.md) is still correct as written.
- The [workarounds research](carbide-T21-workarounds-research__2026-04-20__18-55-00-000000.md)'s primary recommendation ("wrap in a dispatcher-shim to fix T2.1 cheaply") is now known to be **insufficient** as stated. The dispatcher hypothesis may still be directionally right, but the implementation required is more than just replacing the SC — it likely involves either hosting Carbide inside a Blazor component or relocating user execution to a WebWorker.
- The [original investigation report](carbide-T21-investigation-report__2026-04-20__17-11-37-000000.md)'s five options (A–E) are still the live decision set. No cheap win has been found. Option B (multi-threaded runtime with COOP/COEP) remains the most-likely-to-actually-work escape hatch.

## Recommended next investigation

**In-browser debugger break** at the PNSE throw site, with Mono-WASM debug symbols loaded:

1. `dotnet publish -c Debug` Carbide.Core (enables Mono-WASM debug data).
2. Open the failing probe URL (`t21-matrix-probe.html?probe=PAIR-A-late`) in Chromium devtools.
3. Pause on exception (ensure "Pause on caught exceptions" is ON).
4. Reload and wait for the break on the `PlatformNotSupportedException` throw.
5. Walk up the call stack frame-by-frame in the managed-stack pane.
6. Record the frames that are *above* `mono_monitor_wait_internal` — particularly any `AsyncTaskMethodBuilder`, `Task.SetContinuationForAwait`, `ThreadPool.UnsafeQueueUserWorkItemInternal`, `Timer*`, or `SynchronizationContext*` frames.

That stack trace will definitively answer which of the three hypotheses in §"What this means" is the actual cause, and narrow the real fix from "rewrite everything as a Blazor app" to a specific surgical change.
