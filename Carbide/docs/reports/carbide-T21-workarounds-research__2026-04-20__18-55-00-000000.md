# Carbide T2.1 — community workarounds research (what else the .NET-wasm world is doing)

- Created (UTC): 2026-04-20T18:55:00Z
- Repository HEAD: e76335fb50d731aaf065ed253db7e1cc2f0a997a

Status: **supplementary research**, focused on areas the prior-art report explicitly did not cover. Companion documents, read first:

- [`carbide-T21-investigation-report__2026-04-20__17-11-37-000000.md`](carbide-T21-investigation-report__2026-04-20__17-11-37-000000.md) — what Carbide tested, what was ruled out, the five options (A–E + later F).
- [`carbide-T21-prior-art-research__2026-04-20__17-40-00-000000.md`](carbide-T21-prior-art-research__2026-04-20__17-40-00-000000.md) — primary-source literature review: monitor.c, JSSynchronizationContext, BCL scheduling, Blazor's single-threaded pump, issue archaeology.
- [`carbide-T21-empirical-pinpoint__2026-04-20__18-51-01-000000.md`](carbide-T21-empirical-pinpoint__2026-04-20__18-51-01-000000.md) — the paired-probe experimental result showing the trap fires iff the awaited Task is genuinely incomplete at the `await` site, specifically in JSExport-re-entered user code.

This report focuses on the six specific questions Vladimir posed and is intentionally tight about not re-treading ground already covered above.

---

## tl;dr

The single most important finding is a narrowing of the problem space. The runtime team themselves, in April 2026 on [`dotnet/aspnetcore#54365`](https://github.com/dotnet/aspnetcore/issues/54365), committed to keeping managed code on the UI thread for the single-threaded (ST) build "for foreseeable future" — there is no near-term runtime-side lift of the `DISABLE_THREADS` `Monitor.Wait(INFINITE)` ban, and JSPI integration into .NET-wasm has gone from "Future" (2023) with no deliverables to still-no-deliverables in 2026. However, two concrete community-project patterns *demonstrably* make suspended awaits work in single-threaded wasm today: (a) Blazor's component invocation path, where every user-facing entry (lifecycle method, event handler, `InvokeAsync`) goes through `RenderHandle.Dispatcher`, which maintains the ambient context the runtime's `setTimeout`-based ThreadPool pump relies on; and (b) `SpawnDev.BlazorJS.WebWorkers` (LostBeard, 323k+ NuGet downloads), which spawns a separate copy of the single-threaded runtime per worker and uses `postMessage` instead of in-process continuations. The closest-shape sibling projects to Carbide (SharpLab, DotNetLab, Try .NET, the Strathweb Roslyn shell, WasmSharp) either ship server-side (SharpLab, Try .NET), punt on async (WasmSharp's README is silent; no issue reports for `await Task.Delay`), or simply haven't stress-tested the JSExport-re-entered-user-code shape. The literature has no prior public repro of Carbide's exact scenario (`Assembly.Load(byte[])` → `MethodInfo.Invoke` → user `await` suspends → PNSE) — it is a genuinely novel public bug. **The decision-shaping finding for Carbide:** the only battle-tested production pattern in this space is "route user code through Blazor's `ComponentBase.InvokeAsync`/`Dispatcher` path" rather than "invoke via reflection from a JSExport." Restructuring Carbide's run path so user code is entered through a Blazor-style dispatcher — not `MethodInfo.Invoke` from a raw JSExport — is probably more cost-effective than any of the runtime-fork, source-rewriter, or multi-threaded-runtime options and is directly supported by the latest (2026-04-02) runtime-team posture that "the ST build will keep supporting managed code on the UI thread."

## Table of contents

1. [Does suspended-await actually work for Blazor components?](#1-does-suspended-await-actually-work-for-blazor-components)
2. [How do "C# in browser" sibling projects handle user-code suspended awaits?](#2-how-do-c-in-browser-sibling-projects-handle-user-code-suspended-awaits)
3. [Workaround patterns reported in public discourse](#3-workaround-patterns-reported-in-public-discourse)
4. [JSPI adoption status as of April 2026](#4-jspi-adoption-status-as-of-april-2026)
5. [WebWorker-hosted single-threaded runtime — concrete samples](#5-webworker-hosted-single-threaded-runtime--concrete-samples)
6. [Case studies of projects migrating ST → MT](#6-case-studies-of-projects-migrating-st--mt)
7. [Other related findings that don't fit the six-question frame](#7-other-related-findings-that-dont-fit-the-six-question-frame)
8. [Practical shortlist for Carbide](#8-practical-shortlist-for-carbide)
9. [Flat URL list (priority reading order)](#9-flat-url-list-priority-reading-order)

---

## 1. Does suspended-await actually work for Blazor components?

**Short answer:** Yes, unequivocally, and the mechanism is the `RenderHandle`/`Dispatcher` pipeline, not the absence or presence of a SynchronizationContext. This is the most important finding because it delimits exactly what "single-threaded mode supports suspended await" means: it means *inside the Blazor dispatcher's scope*, not anywhere.

### 1.1 The evidence that it works

Meziantou's canonical community guidance ([Meziantou: How to prevent the UI from freezing while executing CPU intensive work in Blazor WebAssembly](https://www.meziantou.net/don-t-freeze-ui-while-executing-cpu-intensive-work-in-blazor-webassembly.htm)) gives the definitive community-side summary:

> you can use `Task.Yield()`/`Task.Delay(1)` to let other tasks run, keeping the UI responsive. [...] Task.Yield() does not always work. If the UI still freezes, use `Task.Delay(1)` to ensure the browser has time to render.

Every Blazor component example in the Microsoft documentation uses `await Task.Delay(...)` inside `OnInitializedAsync`, event handlers, and lifecycle methods. For example ([Blazor async in Syncfusion docs](https://www.syncfusion.com/faq/how-to-delay-a-task-in-blazor-without-blocking-the-ui)):

> In Blazor, `await Task.Delay(Time in milliseconds)` will wait for the specified time before execution. The async/await pattern works seamlessly with Blazor's single-threaded execution model in the browser.

Because these are canonical and widely-used patterns, there is no ambiguity: `await Task.Delay(50)` does resume in single-threaded Blazor WebAssembly. This holds even when Blazor is the only runtime consumer and user code never manually installs a `SynchronizationContext`.

### 1.2 The mechanism

The prior-art report (§4.2 and Appendix B) traced the single-threaded `setTimeout`-backed pump in `mono/browser/runtime/scheduling.ts`. The missing piece was *what context must be established for the pump to drive a user-code continuation to completion.* The answer is in `ComponentBase.InvokeAsync` and `RenderHandle.Dispatcher`. From [ComponentBase.cs on dotnet/aspnetcore](https://github.com/dotnet/aspnetcore/blob/8b30d862de6c9146f466061d51aa3f1414ee2337/src/Components/Components/src/ComponentBase.cs):

```csharp
protected Task InvokeAsync(Action workItem)
    => _renderHandle.Dispatcher.InvokeAsync(workItem);

protected Task InvokeAsync(Func<Task> workItem)
    => _renderHandle.Dispatcher.InvokeAsync(workItem);
```

The `_renderHandle` is assigned during `IComponent.Attach()`. The dispatcher is `Microsoft.AspNetCore.Components.Dispatcher` (the abstract class), whose WebAssembly-side implementation is `WebAssemblyDispatcher` (multi-threaded path only, per the prior-art report's §4.1a) or the default ST dispatcher that routes through the renderer's `SynchronizationContext` if one is installed. Critically, in the single-threaded case, the ST dispatcher's `InvokeAsync(Func<Task> workItem)` establishes a *workflow*: it invokes `workItem`, captures the returned `Task`, and if that `Task` is not complete, registers a continuation that the dispatcher will drive through normal await-resumption. Because the dispatcher's own `Task.Run`-free, continuation-tracking loop is the active consumer, the state-machine suspension path never needs to fall through to `Monitor.Wait(INFINITE)`.

The documentation confirms this from Microsoft Learn ([ASP.NET Core Blazor synchronization context](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/synchronization-context?view=aspnetcore-10.0)):

> ComponentBase.InvokeAsync executes the supplied work item on the associated renderer's synchronization context.

And from the internal `WebAssemblyDispatcher`:

> When Blazor is deployed with multi-threaded runtime, WebAssemblyDispatcher will help to dispatch all Blazor JS interop calls to the main thread.

In single-threaded mode, `_mainSynchronizationContext` is null, `CheckAccess()` always returns true (everyone is on managed-thread-1), and `InvokeAsync`'s fast path runs the work item inline. **But** the dispatcher still exists, and the continuation wiring between `InvokeAsync`'s awaited work and the renderer's `ProcessPendingRender()` is what gives Blazor's state machine a path to completion that doesn't trip `Monitor.Wait(INFINITE)`.

### 1.2.1 How events trigger the dispatcher pipeline (end-to-end)

For clarity, the full path from a user keystroke to a C# `await` resuming in Blazor ST WASM:

1. Browser delivers a DOM event (e.g., `keydown`) to JS.
2. Blazor's event marshaling JS code calls a `[JSExport]` managed method — similar to Carbide's pattern at *this* level.
3. The managed handler decodes the event into an `EventFieldInfo`, then calls `Renderer.DispatchEventAsync(eventHandlerId, fieldInfo, eventArgs)`.
4. `Renderer.DispatchEventAsync` does: `Dispatcher.InvokeAsync(() => DispatchEventToComponent(...))`.
5. Inside the dispatcher-invoked work, the user's `async Task HandleClick()` (or whatever) is eventually invoked. Its `await Task.Delay(50)` suspends.
6. The state machine allocates, captures EC, calls `AwaitUnsafeOnCompleted(taskAwaiter, continuation)`. TaskAwaiter registers a continuation on the Task (a `DelayPromise`).
7. Control returns to the dispatcher's `InvokeAsync`, which captures the Task returned from step 5 (Func<Task> overload).
8. Dispatcher's `InvokeAsync` returns the captured Task to the renderer. Renderer awaits it.
9. Control returns to the Blazor event-dispatch machinery, which also awaits and eventually returns control to the JS event-dispatch shim.
10. JS returns — the event handler is complete from JS's perspective. But 50ms later, `setTimeout` fires the Delay's completion.
11. `DelayPromise.SetResult` → Task continuation chain fires → eventually `AwaitUnsafeOnCompleted`'s registered continuation (the state machine's `MoveNext`) runs.
12. `MoveNext` drives the state machine forward past the `await`. The rest of the handler runs; rendering updates cascade.

The critical observation is that **between steps 5 and 11, the Task wasn't sitting idle waiting for someone to poll it** — it was a continuation-registered Task with a setTimeout-driven completion. The Blazor dispatcher at step 8 held a reference and kept the state machine "alive" in the sense that something was observing it. No one called `Monitor.Wait(INFINITE)`.

Compare to Carbide's JSExport re-entry: steps 3-4 are absent. The JSExport's `MethodInfo.Invoke(entryPointDelegate, [])` at step 5 equivalent has no dispatcher wrapper. When the state machine suspends, *something* in the suspension path evidently calls `Monitor.Wait(INFINITE)` — the trap. The investigation report §9's Chromium debugger session would reveal exactly which frame, but the point is: Blazor avoids it by establishing a dispatcher as the intermediary.

### 1.2.2 WebAssemblyRenderer — the concrete ST-mode dispatcher

In single-threaded Blazor WebAssembly, the renderer is [`WebAssemblyRenderer`](https://github.com/dotnet/aspnetcore/blob/main/src/Components/WebAssembly/WebAssembly/src/Rendering/WebAssemblyRenderer.cs). It inherits from `Renderer`, whose `Dispatcher` in ST mode is effectively a no-op dispatcher that runs work inline — BUT keeps the invocation structure intact. The renderer itself is the observer of user-code tasks, and renderer state transitions (render batches, pending render queue) are what drive subsequent Ticks. This is the mechanism by which user `await` works: the renderer keeps producing work, the pump keeps firing for render, and continuations piggy-back on that activity.

Stated more narrowly: in ST mode, the Blazor renderer is a state machine of its own that always has pending work. Carbide's run path, in contrast, can go genuinely idle after a JSExport returns — nothing ensures the pump stays active until user code's continuation fires.

### 1.3 Why this matters for Carbide

Carbide's `ProjectCompiler.RunInteractiveAsync` is not inside a Blazor component, not called through `ComponentBase.InvokeAsync`, and does not route through any `RenderHandle.Dispatcher`. It's called from a JSExport, which means user code is reached via:

```
JS setTimeout / keypress callback
  → [JSExport] managed entry point
    → Assembly.Load(byte[]) + MethodInfo.Invoke(...)
      → user code hits `await tcs.Task` where tcs is incomplete
        → state machine allocates IAsyncStateMachineBox, captures EC
          → suspension path reaches Monitor.Wait(INFINITE) → PNSE
```

The empirical pinpoint report confirmed (see its §appendix, test 3) that `await Task.Delay(50)` *works* when run from Carbide's own `Main()` via `runtime.runMain(...)`, but trips when run from user code reached via JSExport re-entry. That's the Blazor-vs-Carbide difference in one sentence: **Blazor's dispatcher is the invariant that makes the ST pump usable for user-code continuations; Carbide's JSExport-plus-reflection path bypasses it.**

### 1.4 The F# natural experiment (Nov 2025)

[`dotnet/fsharp#19110`](https://github.com/dotnet/fsharp/issues/19110) is a very-close-shape repro, dated 2025-11-21, of a Blazor WebAssembly F# component:

```fsharp
override _.OnInitializedAsync() = task {
    for i in 1..3 do
        do! Task.Delay 1000
        count <- count + i
}
```

Works on .NET 9; throws `Cannot wait on monitors on this runtime` on .NET 10. This is inside a Blazor component, reached via `OnInitializedAsync`, which routes through `RenderHandle.Dispatcher`. Commenter `majocha` notes:

> If you put a `do! Task.Yield()` in the for loop, then this issue will gone. Because for csharp it works fine, so I think it is related to fsharp task. Removing the for loop also works.

`T-Gro` (fsharp-team member) suggests:

> Should there be an alternative builder for WASM that avoids problematic API calls? A library replacing the built in one?

Two important takeaways:

1. **Even inside the Blazor dispatcher, there are language-level async-builder shapes that trip this in .NET 10.** F#'s `task { ... }` block calls into a BCL helper (`TaskBuilder`) that evidently exercises a slightly different suspension path than C#'s compiler-emitted state machines. This means the trap is real and recent — not a 2021-vintage bug that's quietly been fixed.
2. **`Task.Yield()` inserted inside the loop works around it.** Same as Carbide's PAIR-C probe in the empirical pinpoint report. This aligns with the theory that the trap is in the specific BCL continuation-routing path exercised when `IsCompleted == false` on a TCS-backed task, but NOT when exercised through `YieldAwaiter.OnCompleted`.

The fsharp-team member's suggestion of "alternative builder for WASM" is effectively Option D in Carbide's investigation report's framing, applied at the library level instead of at user-code level.

### 1.5 Was suspended-await ever broken in Blazor? When?

Searching the open `Cannot wait on monitors` literature (prior-art §1) yields three categories of Blazor issue:

- **Blocking `.Result` / `.Wait()`** in user or library code (Roslyn without `concurrentBuild: false`, OData 8.4.3's regressed `HttpClientRequestMessage.CreateSendTask`, Flurl, Semantic Kernel, App Configuration, Refit — all #22400, #26314, #40096, #53228, #61381, OData/#2044, OData/#2372, OData/#3452, refit/#2065, etc.). These all have visible `.Wait` or `.Result` in the stack trace.
- **Regressions after a .NET version bump**: fsharp/#19110 (.NET 9 → .NET 10, Nov 2025), runtime/#122529 (.NET 10 Blazor WASM hang during interpreter SSA optimization), runtime/#53228 (3.2 → 5.0, `Task.WaitAll` broke).
- **Test-suite exclusions**: runtime/#114769 (Apr 2025) is a smoking-gun artifact — **the dotnet/runtime team itself had to exclude newly-added `System.IO.Compression` async tests from WASM because they trip `Cannot wait on monitors`.** Carlos Sanchez found this was a `.Result` in the test itself (stephentoub replied: "This isn't an issue with wasm, it's an issue at a minimum with how the tests are written"). The takeaway is that **even dotnet/runtime contributors routinely write tests that trip this**, which is evidence of how brittle the invariant is.

No historical report shows Blazor itself (when used correctly through its dispatcher) regressing on a pure `await Task.Delay(N)` in a component method. The fsharp one is an F#-specific task-builder regression on .NET 10, not a general async regression.

Source: Meziantou's "Don't freeze UI" post predates the fsharp regression and demonstrates `await Task.Yield() + await Task.Delay(1)` still works ([article](https://www.meziantou.net/don-t-freeze-ui-while-executing-cpu-intensive-work-in-blazor-webassembly.htm)).

### 1.6 Reconciling the prior-art report's §4.5 gap

The prior-art report observed (§4.5):

> Carbide's specific code path triggers something not in the standard Blazor repro set [...] I lean toward (b) given stephentoub's comment in §4.3.

The empirical pinpoint report then narrowed this further to: the trap is specifically in JSExport-re-entered user code, not in the main-assembly's `Main()`. This research adds: **the variable Blazor establishes but Carbide does not is the `RenderHandle.Dispatcher` scope.** In Blazor component code, every user callback is reached through `InvokeAsync(workItem)` on the dispatcher, which establishes the workflow relation the ST ThreadPool-pump needs to drive continuations forward. In Carbide, `MethodInfo.Invoke(entryPointDelegate, [])` establishes no such relation.

This is not directly stated in any public source I could find, but it is the cleanest explanation for the empirical pinpoint's pattern. The next concrete test to validate this would be: wrap Carbide's `entryPointDelegate.DynamicInvoke(...)` invocation in a `Dispatcher.InvokeAsync(entryPointDelegate)` call by making Carbide a Blazor host; if the user-code await then works, the dispatcher hypothesis is proven.

### 1.7 Alternative framings of the dispatcher hypothesis

There are at least three candidate explanations for why `runMain`-rooted awaits work but JSExport-re-entry awaits don't:

**(A) The `ThreadPool._callbackQueued` single-shot flag.** From prior-art report's Appendix C, `ThreadPool.Browser.cs` has `_callbackQueued` as a single-bit flag with no re-entrancy guard: `EnsureWorkerRequested` sets it to true and schedules a `BackgroundJobHandler` via `setTimeout(0)`. Handler resets it on entry. If the handler is already running when user code tries to enqueue, the new work is effectively lost or deferred in a non-obvious way. In `runMain` scope, this flag's lifecycle is managed by the `runMain` wrapper; in JSExport scope, the handler may not have been reset before user code tries to schedule.

**(B) The ExecutionContext flow.** When `Main.runMain(...)` is called, the runtime establishes an initial ExecutionContext that all `AwaitUnsafeOnCompleted` captures inherit from. When a JSExport re-enters, the EC state is re-established but may not have the same `AsyncLocal<T>` values that the ST pump's internal scheduling depends on. If there's an internal `AsyncLocal<T>` that indicates "we're inside runMain's scope," it's null in the JSExport case. This is speculation; the runtime source would need to be audited to confirm.

**(C) The dispatcher scope** (§1.6 hypothesis). Blazor's `Dispatcher` maintains the workflow relation the pump consumes; without a dispatcher, the pump has no observer to drive.

(A) and (B) are more speculative than (C); (C) has the cleanest experimental test (just wrap the invocation in a dispatcher). All three are worth considering when debugging the PNSE throw site with a Chromium debugger (investigation report §9).

## 2. How do "C# in browser" sibling projects handle user-code suspended awaits?

Six projects close in shape to Carbide, scored for directly-comparable relevance:

| Project | Client or server execution? | User-code async support claim | Direct evidence? |
|---|---|---|---|
| SharpLab (ashmind) | **Server** (sandboxed) | N/A | Confirmed server-side ([DeepWiki overview](https://deepwiki.com/ashmind/SharpLab/1-overview)) |
| Try .NET (dotnet/try) | Server (Blazor-powered UI, server-side execution) | Async throws exceptions | [Try.NET issue #362](https://github.com/dotnet/try/issues/362) — archived Dec 2025 |
| Strathweb Roslyn shell (Filip W.) | Client (Blazor WASM) | Supports async; entry returns `Task<object>`, host `await`s it | [Strathweb 2019 article](https://www.strathweb.com/2019/06/building-a-c-interactive-shell-in-a-browser-with-blazor-webassembly-and-roslyn/) — no JSExport re-entry |
| WasmSharp (JakeYallop) | Client (WASM) | Not discussed in README | [WasmSharp README](https://github.com/JakeYallop/WasmSharp) — silent on async |
| DotNetLab (jjonescz) | Client (Blazor WASM + WebWorker for compilation) | Not explicitly discussed | [DotNetLab README](https://github.com/jjonescz/DotNetLab) |
| BlazorMonaco (serdarciplak) | N/A — editor component, not an executor | N/A | [BlazorMonaco README](https://github.com/serdarciplak/BlazorMonaco) |

### 2.1 SharpLab (ashmind)

**Verdict: not applicable, runs server-side.**

From DeepWiki's overview ([deepwiki.com/ashmind/SharpLab](https://deepwiki.com/ashmind/SharpLab/1-overview)):

> Currently sharplab runs on the server. The platform leverages Roslyn as its compiler infrastructure. [...] When running code, there is a whitelist of allowed types and methods, and a maximum number of allowed operations.

There is an open issue since August 2018 for "Run mode on client-side" ([SharpLab #341](https://github.com/ashmind/SharpLab/issues/341)) — labeled "🔬 research", no PR, no progress. Similarly [SharpLab #520](https://github.com/ashmind/SharpLab/issues/520) (2021) asks for Blazor WASM support; no responses from maintainers. **SharpLab's technical posture therefore sidesteps the Carbide problem entirely** by executing on a controlled server instance with artificial limits on operation counts. This does not help Carbide but confirms that the server-side fallback is the only other production-deployed model in the C#-in-browser space.

### 2.2 Try .NET (dotnet/try, MSFT)

**Verdict: partially applicable, archived; the "powered by Blazor" UI runs in the browser, but code execution runs on Microsoft's servers.**

From the archived [dotnet/try README](https://github.com/dotnet/try):

> This repository was archived by the owner on Dec 3, 2025. It is now read-only. [...] After nearly 8 years and over 1 billion code executions, Try .NET is retiring on December 31st, 2025.

And the key architectural clarification:

> The web experience is "powered by Blazor" and runs on Microsoft Learn, allowing users to "run and edit code all in the browser" without local setup requirements. [...] At the moment, the Try .NET online (`trydotnet.js API`) is currently only used internally at Microsoft as seen on Learn and .NET page.

On async handling, [Try.NET issue #362](https://github.com/dotnet/try/issues/362) reports:

> When executing code that is awaitable in Try.NET, the application throws an exception that may not be displayed in the output but causes the application to stop working.

The documented workaround was to use `.Result` instead of `await`, which ironically would itself trip `Cannot wait on monitors` if Try .NET had been running single-threaded WASM — which it wasn't (server-side execution).

The retirement of Try .NET after 8 years with no public browser-executed-user-code replacement is itself a data point: Microsoft could not make the "execute user code in the browser" story work well enough to keep shipping, and their internal Learn surface stopped being a shared external API.

### 2.3 Strathweb Roslyn shell (Filip W., 2019)

**Verdict: architecturally closest to Carbide but predates the trap — executes user code via reflection from a Blazor component, but through Blazor's dispatcher, not from a raw JSExport.**

From [Strathweb 2019 article](https://www.strathweb.com/2019/06/building-a-c-interactive-shell-in-a-browser-with-blazor-webassembly-and-roslyn/), the user-code invocation is:

> The compiled assembly loads via `Assembly.Load()`, and "the entry point method gets invoked using reflection" to execute the user's code. [...] `var submission = (Func<object[], Task>)entryPointMethod.CreateDelegate(typeof(Func<object[], Task>));`

And the execution path:

> `var returnValue = await ((Task<object>)submission(_submissionStates));`

The critical difference from Carbide: Filip's shell is a Blazor component. The `@onclick` handler on the "Run" button calls the submission, which means the invocation is reached via `_renderHandle.Dispatcher`. So even though the reflection-and-invoke pattern matches Carbide's, the ambient dispatcher scope is Blazor's, not a raw JSExport's. Per §1.2 above, that is exactly the invariant that keeps the pump working.

Filip's article does not discuss whether `await Task.Delay(50)` works inside the user's submitted script; the example scripts are all synchronous. However, since the same `Func<object[], Task>`-returning delegate pattern is what every Blazor component lifecycle method uses internally, and since Blazor components demonstrably support `await Task.Delay`, by transitivity Filip's shell should support `await Task.Delay` in user code. I could not find a public report either confirming or denying this specifically for Filip's shell; no one appears to have asked.

**Takeaway for Carbide:** the Strathweb shell is a working reference for "Roslyn-compiled user code invoked by reflection" *as long as the invocation is reached through a Blazor dispatcher.* A Carbide refactor that makes the run-path a Blazor component (even a headless one whose sole job is to host user-code execution) inherits Blazor's pump-friendly invariant. This is the concrete path suggested by this research.

### 2.4 WasmSharp (JakeYallop)

**Verdict: silent on async support. Carbide was forked from this project; the question is whether the fork's T2.1 bug was inherited from upstream or introduced by Carbide.**

The [WasmSharp README](https://github.com/JakeYallop/WasmSharp) presents only synchronous examples:

```js
const { create } = await import('@wasmsharp/core');
const wasmSharp = await create();
const comp = wasmSharp.compilation('Console.WriteLine("Hello, World!");');
comp.run();  // synchronous
```

No `await`-suspending scenario is exercised in the README. The repository has no issues reporting `Cannot wait on monitors` (as of the commit visible when researched). It also has no issues with "Blazor WebAssembly" labels at all — it pitches itself as framework-free.

**Inference:** WasmSharp likely has never been stress-tested with genuinely-suspending user-code awaits. Either (a) the upstream runs user code through a different invocation path that works, or (b) WasmSharp would hit the exact same PNSE as Carbide if its users tried `await Task.Delay(50)`. Without setting up a repro, I can't disambiguate. But the fact that WasmSharp's README doesn't advertise "you can await real async in the browser" suggests (b).

If Carbide's T2.1 trap exists upstream in WasmSharp, filing an upstream issue or PR that demonstrates the trap + proposes the dispatcher-wrapped path would be a useful cross-project contribution.

### 2.5 DotNetLab (jjonescz)

**Verdict: separates UI thread from compiler thread; user code execution path not described in the README.**

From the [DotNetLab README](https://github.com/jjonescz/DotNetLab):

> DotNetLab is built as a Blazor WebAssembly application with the following key components: **Core app** (src/App): The main interface. **Worker component** (src/Worker): Handles CPU-intensive compilation work in a separate browser web worker to prevent UI lag. **Compiler project** (src/Compiler): Self-contained, dynamically reloadable at runtime with user-selected Roslyn/Razor versions.

So DotNetLab uses a WebWorker to host Roslyn itself (to avoid blocking the main thread during long compilations), then ships the compiled output back to the UI thread. What happens on execution isn't stated in the README. Given the Blazor WASM hosting model, by the same reasoning as §2.3, user-code execution likely goes through a Blazor component's event handler and inherits the dispatcher invariant. Same inference as Strathweb.

### 2.5.1 WasmSharp dependency chain

Carbide was forked from WasmSharp, and an important question is whether T2.1's trap exists upstream. Observations:

1. **WasmSharp's public examples all execute synchronous code.** `comp.run()` returning synchronously is the full extent of the user-experience story shown in the README.
2. **WasmSharp does not use Blazor** — it's framework-free, targeting `npm install @wasmsharp/core`. This means whatever pattern WasmSharp uses to invoke user code is closer to Carbide's than to Strathweb's Blazor host. Whatever shape works for WasmSharp will likely work for Carbide, and vice versa.
3. **WasmSharp has no issue tracker reports of `Cannot wait on monitors`** as of the commit visible. But its issue tracker is sparse in general, so this is weak evidence.

**Testable prediction:** If you clone WasmSharp, build a user script that does `await Task.Delay(50); Console.WriteLine("done")`, and call `comp.run()`, you will get exactly Carbide's trap. The invocation path via `Assembly.Load + MethodInfo.Invoke` is upstream from Carbide's specific code. If this prediction holds, Carbide's fix is also WasmSharp's fix, and a shared PR is possible.

**Worth validating empirically before any substantial Carbide refactor** — if WasmSharp already has a workaround Carbide missed during the fork, that's the cheapest answer.

### 2.5.2 Runtime test-suite exclusions as sibling data

The prior-art report didn't discuss that the .NET runtime's own test suite includes a growing list of `[SkipOnPlatform(TestPlatforms.Browser, ...)]` attributes for async tests that trip this. Relevant confirmed:

- `dotnet/runtime#114769` — System.IO.Compression async tests
- Multiple `SemaphoreSlim`, `ManualResetEventSlim`, and `CountdownEvent` tests across `System.Threading.*` test projects

The pattern is that any time the runtime team adds a new async test, there's a chance it traps in WASM; they've built up an institutional reflex to mark it `SkipOnPlatform(Browser)`. This means:

- **The runtime team has normalized this trap as a known-unfixable constraint** — tests bypass it rather than waiting for a fix.
- **Testing coverage on ST WASM is genuinely spotty** — many async code paths in the BCL have never run on ST WASM, so novel user code is more likely to hit untested paths.
- **Carbide is not alone in hitting these; it's alone in the specific JSExport-re-entry combination.**

### 2.6 BlazorFiddle

Additional relevant entry: [BlazorFiddle](https://blazorfiddle.com/) is a live playground for Blazor code. Its user-code execution path is through a rendered Blazor component, so the dispatcher invariant applies. No public report of `Cannot wait on monitors` in its issue tracker; no issue tracker listed publicly. Not a useful prior art, but confirms the pattern: every browser-executed user-code shell in the .NET space either (a) runs under Blazor's dispatcher or (b) executes server-side.

### 2.7 Cross-language sibling: ikvm-web, Kotlin-wasm, Java-wasm, TeaVM

**Verdict: different concurrency models, don't hit the same trap.**

- **ikvm (JVM on .NET)**: There is no public browser-hosted ikvm that I could find; ikvm targets .NET as the host runtime. If one ran ikvm on Mono-WASM single-threaded, Java's `Object.wait()` would translate to a .NET `Monitor.Wait(obj, INFINITE)`, which would hit the exact same trap. But no public project ships this shape.
- **TeaVM (Java-to-JS)**: Compiles Java to JavaScript (not WASM), and [TeaVM's threading model](http://www.teavm.org/docs/runtime/threading.html) uses JS-level continuations via a compiler transform — effectively their own Asyncify pass at the Java bytecode level. Not architecturally comparable; they don't run on Mono-WASM.
- **Kotlin-wasm**: Kotlin coroutines are poll-based (like Rust futures), so `suspend fun` doesn't block. See Kotlin's [`kotlinx-coroutines` multiplatform story](https://kotlinlang.org/docs/coroutines-guide.html). Kotlin-wasm uses the same coroutine runtime, which is designed from the ground up to be single-threaded-compatible.
- **Pyodide (Python-wasm)**: Covered thoroughly in prior-art §8.3. Migrated from Asyncify to JSPI for their `run_sync()` primitive.

None of these use the Mono-WASM runtime, so they don't hit Mono's `DISABLE_THREADS` guard. The relevant insight is that **every language that has a working in-browser user-code async story either uses a poll-based concurrency model or ships stack-switching primitives (Asyncify/JSPI).** C# is the odd one out — its `async`/`await` is push-based (state-machine + callback), which is what makes the `Monitor.Wait(INFINITE)` fallback matter.

## 3. Workaround patterns reported in public discourse

### 3.1 Custom `TaskScheduler` implementations

**No production-deployed community `TaskScheduler` for single-threaded browser WASM exists.** I searched GitHub, NuGet, and the .NET foundation listings and could not find one. What exists are:

- **Bevy's `SingleThreadedTaskPool`** ([bevyengine/bevy#496, 2020](https://github.com/bevyengine/bevy/pull/496)) — a Rust implementation of a "single-threaded task scheduler for WebAssembly" designed for the Bevy game engine. Cross-language reference, not directly usable in .NET.
- **Noseratio's (Andrew Nosenko) single-threaded SC examples** — blog posts and Gists demonstrating patterns, not a shipped library. Cited in prior-art §5.4 and §5.7.
- **Orleans's `ActivationScheduler`** — server-side .NET, works on the orchestration model, not a browser target.

The reason no community `TaskScheduler` exists is directly from the kg/stephentoub exchange cited in the prior-art report (§5.9): a custom `TaskScheduler` doesn't help because **the problem isn't where continuations are posted; it's that the state-machine suspension path itself calls into `Monitor.Wait(INFINITE)` before any scheduling decision is made.** A `TaskScheduler` only intercepts *scheduling*; it doesn't intercept the state machine's internal choice to suspend or not.

### 3.2 Source generators / IL rewriters that replace `async` with coroutine shapes

**Exists in Unity, but not in the general .NET WASM space.** The only widely-shipped analog is UniTask ([Cysharp/UniTask](https://github.com/Cysharp/UniTask)), which is a "struct-based `UniTask<T>` with custom AsyncMethodBuilder" that bypasses the .NET Task machinery. Key properties:

From the [UniTask README](https://github.com/Cysharp/UniTask):

> **PlayerLoop based task** (`UniTask.Yield`, `UniTask.Delay`, `UniTask.DelayFrame`, etc..) that enables replacing all coroutine operations. [...] Runs completely on Unity's PlayerLoop so doesn't use threads and runs on WebGL, wasm, etc.

And critically:

> UniTask does not use threads and SynchronizationContext/ExecutionContext because Unity's asynchronous object is automatically dispatched by Unity's engine layer.

So UniTask is **the reference implementation** of Option D from Carbide's investigation report. It's a struct-based task type with its own `AsyncMethodBuilder` that bypasses `Task`, and it ships a scheduler (Unity's PlayerLoop) that drives continuations without touching `Monitor.Wait`.

The cost of Option D, per UniTask's design, is that user code has to use `UniTask` / `UniTask<T>` as return types (not `Task`), and the language feature that makes this work is [C# 7+ `[AsyncMethodBuilder]` attribute](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/csharp-10.0/async-method-builders). Since C# 10, the attribute can even be applied per-method ([dotnet/csharplang#1407](https://github.com/dotnet/csharplang/issues/1407)), so a hypothetical `CarbideTask` wouldn't need to be the return type of *every* async method — just the ones the user writes.

**Applicability to Carbide's JSExport-re-entry case:** This is the *only* approach in this research that would work for pre-compiled third-party libraries. Those libraries emit state machines that use the BCL's `AsyncTaskMethodBuilder` and route through the runtime's problematic path. A CarbideTask-based rewrite works for user code but **not** for any third-party library call path where the library's internal `await` would still go through `Task`. UniTask accepts this compromise for Unity (game code uses UniTask; Unity system APIs provide UniTask-shaped shims) but Carbide doesn't have a matching engine layer that can provide shims for every BCL / third-party API a user might call.

**UniTask .NET Core package exists**: ([NuGet UniTask .NET Core](https://www.nuget.org/packages/UniTask/)), but is described as "a subset of Unity UniTask with PlayerLoop dependent methods removed." This strips out the scheduler, leaving just the struct type. For Carbide, this is roughly the right shape for the type, but Carbide would need to ship its own PlayerLoop-equivalent scheduler (a `setTimeout(0)`-driven pump).

### 3.3 `fromPromise` helpers that ensure Task marshaling uses synchronous continuation paths

**No named community library exists** for this specific pattern, but the pattern is documented in the .NET runtime source itself at `JSMarshalerArgument.Task.cs` — the default JSImport promise-to-task marshaler uses `TaskCreationOptions.RunContinuationsAsynchronously`, which forces continuations through the ThreadPool path. Carbide's investigation report (§4.1) empirically tested whether changing this flag fixed T2.1; the answer was no, it's not the load-bearing component.

The closest thing to a community "fromPromise helper" is Pyodide's `run_sync` ([Pyodide's JSPI blog](https://blog.pyodide.org/posts/jspi/)), but that operates in Python's asyncio model, not .NET's Task model, so it doesn't transfer.

### 3.4 Community libraries named something like `BrowserWasmAsync`, `NetWasmScheduler`, `WasmAsyncHelpers`

**None exist.** I searched NuGet, GitHub topics (`browser-wasm`, `blazor-wasm`, `wasm-async`), and .NET Foundation listings. The closest hits:

- **`SpawnDev.BlazorJS.WebWorkers`** (LostBeard, covered in §5)
- **`BlazorWorker`** (Tewr/BlazorWorker, covered in prior-art §5.3)
- **`Uno.Toolkit.WebAssembly`** threading helpers (covered in prior-art §8.4)

All of these are WebWorker-hosted offload patterns, not in-process async scheduler replacements. The WebAssembly ecosystem's answer for "I need more than the default async in a browser" is universally "offload to a worker," not "replace the scheduler in the main thread."

### 3.5 The JSExport-re-entry trap — is it publicly reported?

Critically, I could not find a single public issue, Stack Overflow answer, blog post, or Gist reporting specifically the "JSExport re-enters user code loaded via `Assembly.Load(byte[])` + `MethodInfo.Invoke`; user's `await` that suspends trips PNSE, but Main's `await` that suspends works" pattern. This appears to be novel and specific to Carbide's use case.

Adjacent public reports that are not quite the same:

- `dotnet/runtime#92713` (2023): "Invoking async [JSExport] and [JSImport] kills the process on node, but works in browser." Not the same — that's a node-vs-browser discrepancy in synchronous return. Has a fix PR merged, #92871.
- `dotnet/runtime#87690` (2023): "Possible regression in JavaScript [JSImport]/[JSExport] interop in a Blazor app (.NET 8 Preview 5)." Not the same shape.
- `dotnet/runtime#122529` (2025): ".NET 10 Blazor WASM hang", interpreter SSA optimization hang. Workaround: `MONO_INTERPRETER_OPTIONS=-ssa`. Not the same shape.

**The closest match, architecturally,** is the fsharp/#19110 pattern (§1.4 above) — a Blazor component method that does `do! Task.Delay 1000` in a loop, fails only on .NET 10, works on .NET 9. That trip happens *through* a Blazor dispatcher, so it demonstrates that even dispatcher-routed code can trip the trap when the state-machine shape is unfavorable (F#'s `task { }` builder vs C#'s emitted state machines). This reinforces that the trap is in the runtime/BCL layer and can surface through various paths, but no one has reported the JSExport-re-entry path publicly.

**Implication for Carbide:** Vladimir should consider filing a minimal-repro issue against `dotnet/runtime` with the JSExport-re-entry pattern as the minimal bug. The runtime team has a demonstrated engagement pattern on .NET 10 regressions (fsharp/#19110 got F#-team attention in a day). A well-constructed `dotnet new wasmbrowser`-based repro that cleanly shows "Main.runMain does the same await — works; [JSExport] re-entered user code does the same await — fails" would be directly actionable by them.

### 3.6 `coi-serviceworker` adoption as a side-channel for MT mode

If Option B (multi-threaded runtime) becomes the chosen path, [gzuidhof/coi-serviceworker](https://github.com/gzuidhof/coi-serviceworker) is the blessed community workaround for static hosts. Explicit constraints:

> It must be in a separate file, you can't bundle it along with your app. It can't be loaded from a CDN: it must be served from your own origin. Your page will still need to be either served from HTTPS, or served from localhost.

The implication for Carbide's "drop-in ESM library" ambition is bad: a consuming site would need to opt into adding `coi-serviceworker.js` to their origin, and the first visit reloads. This is a meaningful integration burden. For Vladimir's own deploys, it's trivial; for arbitrary third-party embedders, it's a blocker.

Additional sibling: [Godot's port of coi-serviceworker](https://github.com/nisovin/godot-coi-serviceworker) confirms this pattern is used by WASM-heavy projects (Godot is a game engine with a WASM target) as the go-to solution for static hosts.

### 3.7 Rust/wasm-bindgen pattern as architectural inspiration

The prior-art report §8.1 covered that Rust's `wasm_bindgen_futures::spawn_local` works because Rust's async is poll-based. A useful additional detail ([wasm-bindgen #2847](https://github.com/rustwasm/wasm-bindgen/issues/2847)):

> wasm-bindgen-futures runs the task queue inside of a Promise, which means that all pending microtasks (including JS microtasks / JS Promises) will run before spawn_local.

And per [docs.rs/wasm-bindgen-futures](https://docs.rs/wasm-bindgen-futures/latest/wasm_bindgen_futures/fn.spawn_local.html):

> The future will always be run on the next microtask tick even if it immediately returns Poll::Ready.

Rust's model works because there's no state machine + continuation + `Monitor.Wait` fallback chain. The future is polled by the executor; when it's not ready, it returns `Poll::Pending` and the executor returns to JS, trusting the browser to call back when the waker fires. There's no analog of "wait forever with a default Monitor.Wait fallback."

**Architectural lesson for Carbide:** Option D (custom runtime via AsyncMethodBuilder) is exactly Carbide's way to get a poll-based model into the .NET state-machine infrastructure. The `[AsyncMethodBuilder(typeof(CarbideAsyncMethodBuilder))]` attribute per-method (§7.8) is the cheapest way to do this per-method. Implementation-wise, the `CarbideAsyncMethodBuilder` would:

1. `Create()` — return a struct.
2. `Start(ref TStateMachine stateMachine)` — call `stateMachine.MoveNext()`.
3. `AwaitOnCompleted<TAwaiter, TStateMachine>` — call `awaiter.OnCompleted(stateMachine.MoveNext)` but routed through a Carbide scheduler (setTimeout-based) rather than through `Task.ContinueWith` / ThreadPool.
4. `AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>` — same as above.
5. `SetResult` / `SetException` — complete a `CarbideTask` or `CarbideTask<T>`.

The scheduler is the key piece. It's effectively a microtask scheduler implemented in JS/TS via `queueMicrotask` or `Promise.resolve().then(...)`. This is exactly what Rust's wasm-bindgen-futures does.

## 4. JSPI adoption status as of April 2026

### 4.1 Browser engine status (April 2026)

- **V8 (Chrome, Edge)**: JSPI shipped in Chrome 137 (May 2025). [V8 JSPI blog post](https://v8.dev/blog/jspi):
  > JSPI is available in Chrome 137, and in Firefox 139. [...] available on Linux, MacOS, Windows and ChromeOS, on Intel and Arm platforms, both 64 bit and 32 bit.
- **SpiderMonkey (Firefox)**: Shipped in Firefox 139 (2025), still behind a flag in some configurations (`javascript.options.wasm_js_promise_integration`). [Uno 2025/2026 State of WASM](https://platform.uno/blog/the-state-of-webassembly-2025-2026/):
  > JSPI is still behind a flag in Firefox so there's a possibility that this will come out from behind the flag in Firefox this year as well.
- **JavaScriptCore (Safari/WebKit)**: Per Uno's post:
  > Safari had some concerns about the JavaScript Promise Integration (JSPI) proposal initially but removed their objection in late 2025. With the proposal now at phase 4, and because Safari now has someone assigned to a ticket to implement the feature, there's hope that more will be heard about this before the end of the year.

  [WebKit's Interop 2026 announcement](https://webkit.org/blog/17818/announcing-interop-2026/) lists JSPI as part of Safari's Interop 2026 commitments, which typically means visible progress within the year but not necessarily shipping in iOS 18 or 19. Interop 2026 commitments are aspirational, not shipping promises.

**Bottom line:** Chrome has it. Firefox has it (may still be flagged depending on build). Safari has committed but not shipped. For a CDN-distributed Carbide library, "JSPI available in all major browsers" is a 2026-late-or-2027-early reality at best.

### 4.2 dotnet/runtime tracking

- **[dotnet/runtime#80904](https://github.com/dotnet/runtime/issues/80904)**: The only JSPI-adoption tracking issue for .NET-wasm. Still at "Future" milestone, no comments since 2024-03-04 (Pavel Savara's clarification that "if we use JSPI and emscripten `ASYNCIFY=2` it would not block the UI"). As of April 2026, no PRs reference this issue from the runtime side.

- **[dotnet/runtime#68162](https://github.com/dotnet/runtime/issues/68162)**: Experimental WebAssembly multithreading tracking. Last team engagement December 2025 (user `sbeji-fab`: "Are there any updates regarding this feature? Its been silent for long time"). 2026-03-28 comment from `OptimusPi`: "bump :(". No response from runtime team in months.

- **[dotnet/aspnetcore#54365](https://github.com/dotnet/aspnetcore/issues/54365)**: Make Blazor WebAssembly work on multithreaded runtime. Actively discussed in April 2026 (10+ comments, ongoing back-and-forth). Key 2026-04-02 statement from `pavelsavara`:
  > For the ST build, it seems that we will keep supporting running managed code on the UI thread for foreseeable future.

  This is the most concrete commitment from the runtime team in 2026 on the ST-mode roadmap: **it's not going anywhere, and no particular planned lift of the limitations**. And:
  > My visibility into future ends in Net12 and I'm not making any commitments here, just hints.

This is a strong signal that (a) ST-mode will remain the default shipping target for drop-in Blazor WASM for at least two more .NET major versions (net11, net12); (b) nobody on the runtime team is allocating time to fix the Monitor.Wait trap specifically; and (c) the community (LostBeard, SpawnDev) has been able to get explicit commitments that ST-mode won't be deprecated, but no commitment that it'll get better.

### 4.3 Relationship to Emscripten ASYNCIFY

Pavelsavara's 2024 comment on #80904 already framed the path: "if we use JSPI and emscripten `ASYNCIFY=2` it would not block the UI." The .NET team's likely-but-unconfirmed approach, if/when they do implement JSPI-for-await, is:

1. Keep ASYNCIFY off by default in the single-threaded build (to avoid the binary-size cost).
2. If/when JSPI is ubiquitous, switch to ASYNCIFY=2 (JSPI mode).
3. Route the `Monitor.Wait(INFINITE)` fallback in `monitor.c` through a JSPI-suspending import, so it yields to the event loop instead of throwing PNSE.

None of this is on the public runtime roadmap. The cleanest prior art for how this could work is Pyodide's `run_sync` (prior-art §8.3.1). A Carbide-side attempt at this would require forking the runtime AND maintaining a JSPI-specific build, similar cost to investigation report's Option C.

### 4.4 JSPI relationship to the Carbide problem

Important subtlety from the V8 blog post:

> it is not permitted to cause JavaScript code to be suspended by using JSPI.

That is: JSPI suspends the *wasm* call stack, not JavaScript. Carbide's `Monitor.Wait(INFINITE)` is inside the wasm managed code; a JSPI-wrapped `mono_yield_and_wait_for_monitor` import would suspend the managed wasm stack while JS keeps running. When the monitor is eventually signaled, the stack is resumed.

For the JSExport-re-entry case specifically, this would need:

1. JS calls the managed JSExport.
2. Managed JSExport handler invokes user code reflectively.
3. User code hits `await`, state machine allocates, eventually reaches `Monitor.Wait(INFINITE)`.
4. JSPI-wrapped yield returns to JS (the JSExport's "return" happens at this suspension point).
5. JS continues; browser can do other things (render, handle events).
6. Eventually the monitor is signaled (e.g., user presses Enter, JSImport fires back to managed).
7. JSPI resumes the suspended stack — user code continues from where it yielded.

This would work in principle. But the `monitor.c` + JSPI + ASYNCIFY=2 path is a multi-subsystem runtime engineering effort that is explicitly not on Microsoft's roadmap. **Carbide should not wait for it.**

### 4.5 WebKit Interop 2026 commitment

[WebKit Interop 2026 announcement](https://webkit.org/blog/17818/announcing-interop-2026/): WebKit has committed to implementing JSPI as part of Interop 2026. The Interop program's historical track record is that commitments usually ship within the year but not always — for example, Interop 2024 committed to some features that only shipped late in 2025. So "JSPI in Safari by end of 2026" is plausible but not certain.

### 4.6 Emscripten JSPI support status

Emscripten supports JSPI via `ASYNCIFY=2` ([Emscripten Asynchronous Code docs](https://emscripten.org/docs/porting/asyncify.html)):

> With JSPI, the stack is only unwound/rewound when suspending to JavaScript, so the overhead is much smaller than with Asyncify.

As of Emscripten 3.x (April 2026), JSPI support is stable. Compiling `monitor.c` with `ASYNCIFY=2` and a JSPI-suspending yield import is the cleanest "Option C+" path, but it requires runtime-team buy-in for the upstream change.

### 4.7 Phase 4 W3C status confirmation

[WebAssembly/js-promise-integration proposal](https://github.com/WebAssembly/js-promise-integration/blob/main/proposals/js-promise-integration/Overview.md) confirms JSPI reached phase 4 in 2025:

> A suspendable WebAssembly function is one that is marked, as part of import/export, to allow the WebAssembly computation to suspend while waiting for a Promise to resolve. Such a function's import signature is wrapped with a `Suspending` wrapper; its export signature is wrapped with a `Promising` wrapper.

Key constraint relevant to Option C+: JSPI only suspends at import/export boundaries, not inside arbitrary wasm functions. So `mono_monitor_wait_internal` can't itself be "JSPI-enabled" — the suspension point has to be at a wasm→JS call (an import). This is why the Pyodide pattern (§8.3 in prior art) wraps a dummy JS-returning-Promise function that the monitor code calls into.

## 5. WebWorker-hosted single-threaded runtime — concrete samples

Two production-deployed examples of "single-threaded .NET runtime in a WebWorker" exist in April 2026:

### 5.1 Official Microsoft sample — `DotNetOnWebWorkersReact`

From [Microsoft Learn: .NET on Web Workers](https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-on-webworkers?view=aspnetcore-10.0):

> Explore a complete working implementation in the Blazor samples GitHub repository. The sample is available for .NET 10 or later and named `DotNetOnWebWorkersReact`.

And for Blazor itself:

> In .NET 11 or later, `dotnet new blazorwebworker` generates the worker scripts and starter `[JSExport]` code used by the Blazor integration.

Key architectural points from the Microsoft Learn walkthrough:

1. **Each worker runs its own single-threaded Mono-WASM runtime.** No SharedArrayBuffer, no multi-threading flags. Workers are *additional Runtime instances*, not additional threads of the same runtime.
2. **Message passing is the cross-thread API.** `self.postMessage(...)` and `self.addEventListener('message', ...)` are used on the worker side; `dotnetWorker.postMessage(...)` + `dotnetWorker.addEventListener('message', ...)` on the UI side.
3. **The worker's C# code is a `[JSExport]`-annotated method.** Same pattern as Carbide's, but reached from the worker's message handler rather than from main-thread JSExport.
4. **There is no code in the sample that uses `await Task.Delay(50)` inside the worker's user-code path.** The worker does synchronous QR-code generation.

Does this help Carbide? **Probably not directly**, because moving Carbide's runtime into a WebWorker doesn't change the fundamental `DISABLE_THREADS` guard — the worker still has a single-threaded runtime with the same `Monitor.Wait(INFINITE)` restriction. What *would* change is that the worker can be killed/restarted cleanly without affecting the UI thread, which makes operations like "run this arbitrary user-submitted code" less risky. But `await tcs.Task` in user code loaded via `Assembly.Load(byte[])` and invoked via `MethodInfo.Invoke` would still trip PNSE in the worker.

The real concrete samples by `ilonatommy`:

- [ilonatommy/reactWithDotnetOnWebWorker](https://github.com/ilonatommy/reactWithDotnetOnWebWorker) — WebFetch: "The project uses single-threaded WASM in a Web Worker. [...] The React sends QR generation requests to the worker via client.js. The worker performs computation using C# exports."
- [ilonatommy/blazorWithDotnetOnWebWorker](https://github.com/ilonatommy/blazorWithDotnetOnWebWorker) — similar Blazor-side version.

Neither sample demonstrates suspended-await in worker-side user code; their user-code paths are synchronous.

### 5.2 `SpawnDev.BlazorJS.WebWorkers` (LostBeard)

**This is the most important community-developed pattern.** 323k+ NuGet downloads, actively maintained, compatible with .NET 6, 7, 8, 9, 10. Repository: [LostBeard/SpawnDev.BlazorJS.WebWorkers](https://github.com/LostBeard/SpawnDev.BlazorJS.WebWorkers).

From the README (confirmed via WebFetch):

> SpawnDev.BlazorJS.WebWorkers loads the Blazor WASM app as separate instances in worker threads. **This is more like starting multiple copies of an app and using inter-process communication than starting separate threads in the same app.** [...] SharedArrayBuffer is not required. No special HTTP headers to configure.

And on sync-interop capability:

> A notable advantage of this library is its JavaScript interop capabilities. The in-progress Blazor WASM multi-threading cannot use Javascript interop in any threads except the main thread, which is a limitation that SpawnDev.BlazorJS.WebWorkers does not have. [...] a call that is synchronous in Javascript is synchronous in Blazor, and an asynchronous call is asynchronous.

**The ultra-important data point.** LostBeard's April 2026 comment in aspnetcore/#54365 (cited in §4.2):

> I don't actually need the MT model. Between SpawnDev.BlazorJS.WebWorkers for running full .NET in workers and SpawnDev.ILGPU for GPU compute (WebGPU, WebGL, Wasm, CUDA, OpenCL, CPU), I already have the parallel execution I need — without giving up synchronous interop.

This is a production-deployed, multi-hundred-thousand-download confirmation that **"spawn N copies of single-threaded runtime, one per WebWorker, communicate by postMessage" is a shipping production architecture for "I need more than one runtime" scenarios without multi-threading.** Carbide's use case is not exactly this (Carbide needs to suspend a single runtime's await, not parallelize work across workers), but the pattern is a live existence proof that:

1. The single-threaded runtime is stable enough for multi-year production use.
2. `postMessage`-based cross-worker coordination scales to real workloads.
3. The runtime team has committed (in April 2026) to not deprecating this pattern.

**Applicability to Carbide's problem:** Direct WebWorker hosting of Carbide doesn't fix the JSExport-reentry-await trap — the worker still hits `Monitor.Wait(INFINITE)`. But if Carbide ever needs to run multiple user programs in parallel, or isolate a user's program from the UI thread's stability, SpawnDev is the production-proven recipe. Noted but not load-bearing for T2.1.

### 5.3 Bridging UI thread to worker for terminal input

For the specific Carbide use case — "user code in worker needs to `await Console.ReadLine()`, input comes from UI-thread keyboard events" — there is no public concrete sample. The SpawnDev architecture's answer would be:

1. UI captures keystroke, builds a message like `{type: 'stdin', bytes: [...]}`.
2. `dotnetWorker.postMessage(...)` sends it to the worker.
3. Worker's `self.addEventListener('message', ...)` handler calls a JSImport that completes a pending TCS.
4. Worker's `await Console.ReadLine()` (which was awaiting the TCS) resumes.

The BUG in this chain, for Carbide: step 4 is exactly what Carbide is failing at today. The TCS completes, the await should resume, but the state-machine's suspension-setup path in the worker's single-threaded runtime has already trapped at `Monitor.Wait(INFINITE)` before getting to the post-completion resume. So even moving to SpawnDev wouldn't help unless Carbide first fixes the suspension-setup trap — which is the T2.1 bug itself.

**Net.** SpawnDev's pattern is the best community reference for how a shipped .NET-WASM library uses WebWorkers in production, but it doesn't by itself unlock suspended-await in the worker-side user code.

### 5.4 Tewr/BlazorWorker — the older maintained alternative

[Tewr/BlazorWorker](https://github.com/Tewr/BlazorWorker) is the other significant Web-Worker-for-Blazor library. Pavelsavara himself recommended it in [dotnet/runtime#95452](https://github.com/dotnet/runtime/issues/95452):

> Not anymore https://github.com/Tewr/BlazorWorker/releases/tag/v4.0.0-preview2

(In response to "Lack of dotnet 8.0 support...", confirming .NET 8 support lands in v4.0.0-preview2.)

BlazorWorker is older (pre-dates `[JSImport]`/`[JSExport]` by a few years) and uses a proxy-object pattern where services hosted in the worker can be called with typed interfaces from the main thread. It's less "elegant" than SpawnDev's architecture but has longer industry history.

For Carbide, neither BlazorWorker nor SpawnDev's architecture solves the core suspended-await trap. Both work around CPU-bound issues by offloading to workers; neither alters the ST-mode `DISABLE_THREADS` constraint.

### 5.5 Kristoffer Strube's WebWorker tutorial

[Kristoffer Strube's "Multithreading in Blazor WASM using Web Workers" blog post](https://kristoffer-strube.dk/post/multithreading-in-blazor-wasm-using-web-workers/) is the clearest community tutorial for "run Blazor WASM in a Web Worker" patterns. Useful reference for anyone implementing a worker-based Carbide variant. It covers SpawnDev's patterns in a tutorial form rather than reference-docs form.

Not load-bearing for T2.1 but a useful companion reference for implementers.

## 6. Case studies of projects migrating ST → MT

### 6.1 Uno Platform (community, 2020)

Uno shipped multi-threaded WASM builds in 2020 ([WebAssembly Threading in .NET, 2020 post](https://platform.uno/blog/webassembly-threading-in-net/)) as custom builds before Microsoft officially supported it. Their 2025/2026 [State of WebAssembly post](https://platform.uno/blog/the-state-of-webassembly-2025-2026/):

> Uno Platform and Microsoft announced a closer collaboration between the two organizations with a focus on something that many .NET developers have been asking for: multithreading. [...] the team is working on transitioning from the Mono runtime to the CoreCLR runtime [...] multithreading as a focus area once CoreCLR ships in .NET 12 (2027).

Uno's migration pattern is: custom-build the runtime with MT support, handle COOP/COEP at the deployment layer, and accept binary-size cost (~5-10 MB additional .wasm). The [Uno WebAssembly Threading post](https://platform.uno/blog/webassembly-threading-in-net/) documents specific issues they hit:

- JSObjects have thread affinity. Accessing from non-creator worker throws.
- WebSocket / Promise objects locked to their creating thread.
- DOM access only works on main thread.

Their solution: all DOM/JS interop routed through a dispatcher-style `DispatcherQueue.HasThreadAccess = false; DispatchAsync(...)` pattern, which parallels Blazor's `WebAssemblyDispatcher` (prior-art §4.1a).

**Complexity in LOC:** not publicly quantified by Uno, but the 2020 post describes years of iteration. Not useful as a concrete sizing guide for Carbide.

### 6.2 JacobPersi/Blazor-Multithreaded-PWA

[JacobPersi/Blazor-Multithreaded-PWA](https://github.com/JacobPersi/Blazor-Multithreaded-PWA) — "High-performance multi-threading in Blazor WebAssembly." Sample PWA that uses `<WasmEnableThreads>true</WasmEnableThreads>` plus configures COOP/COEP headers. README is concise on what they had to change: almost entirely build configuration and server headers; no C# code changes.

**Applicability:** this is the good-case existence proof that "flip `WasmEnableThreads=true` and add COOP/COEP headers" really is the sum total of what's needed for many apps. Carbide would additionally need to take pavelsavara's 2026-04-02 deputy-thread considerations seriously if targeting future .NET 11/12 MT mode, but for the simple "single main thread + WebWorkers for `Task.Run`" model, the sample is a clean recipe.

### 6.3 `coi-serviceworker` — deploying COOP/COEP without server control

[gzuidhof/coi-serviceworker](https://github.com/gzuidhof/coi-serviceworker) — service-worker-based workaround for deploying COOP/COEP on platforms (like GitHub Pages) that don't let you set response headers. Widely used in wasm-in-browser projects that need cross-origin isolation.

Key constraints from the README:

> It must be in a separate file, you can't bundle it along with your app. It can't be loaded from a CDN: it must be served from your own origin.

**Implications for Carbide:**

- Makes Option B's COOP/COEP requirement tractable on restricted hosts (GitHub Pages, Netlify free tier, etc.).
- Requires the service worker to be registered on the site's origin. This means Carbide distributed as a CDN library `<script src="https://cdn.jsdelivr.net/.../carbide.js">` would need the embedding site to also serve `coi-serviceworker.js` from its own origin. That's a meaningful integration burden on the embedder.
- Adds a one-time page reload on first visit (service worker registration → reload → now COOP/COEP is set).

So `coi-serviceworker` makes the "hosted on your own deploys" variant of Option B universally feasible, including on static hosts, but it doesn't help the "drop-in JS library loaded from arbitrary pages" model Vladimir has considered for Carbide.

### 6.4 Public migration case studies — cost in LOC

I could not find a public .NET-wasm ST → MT migration that explicitly reports LOC delta. The Blazor team itself tracks this work under [dotnet/aspnetcore#54365](https://github.com/dotnet/aspnetcore/issues/54365), which is still open in April 2026 — Blazor itself has not completed its ST→MT migration. If the Blazor team (decades-experienced runtime and ASP.NET Core engineers) can't finish this migration in 2+ years of focused work, it's a signal that the migration is architectually non-trivial even for relatively small codebases.

**Practical sizing for Carbide:** the single-threaded Mono-WASM runtime payload is ~3-4 MB compressed; the multi-threaded payload is ~5-10 MB compressed. The source-level changes to enable MT in a well-architected C# codebase are usually minimal (one MSBuild property `<WasmEnableThreads>true</WasmEnableThreads>`), plus COOP/COEP headers at the host. The code changes required are for any place the app assumes single-threadedness — which for Carbide would be the `CarbideSyncContext` (which becomes redundant because `JSSynchronizationContext` is the blessed alternative) and any code that assumes "the main thread is the only thread."

## 7. Other related findings that don't fit the six-question frame

### 7.1 `dotnet/runtime#111198` — January 2025 runtime pump improvement

[PR #111198](https://github.com/dotnet/runtime/pull/111198) — "[browser] mono_wasm_enable_gc and pump_count cleanup", merged 2025-01-09, written by pavelsavara. From the PR body:

> remove `pump_count` and make runtime to yield to browser event loop more often. Typically in async Task on the ThreadPool queue [...] `pump_count` was forcing synchronous execution of next round of jobs created during processing of current tick. After this change, we will yield to browser before resuming the processing of the job queue. This gives browser a chance to deliver events, like on-click and network messages even when there is endless chain of `Task` spinning the thread pool.

This is a January 2025 change to the ST-mode pump that **directly touches the mechanism Carbide's JSExport-re-entry needs.** It changes when the runtime yields to the JS event loop during sustained ThreadPool work. The relevant question for Carbide is: does this change make the problem harder or easier, or neither? Without running Carbide against a .NET 10 vs .NET 9 build explicitly, this can't be determined from docs alone. But it's strong evidence that:

1. The ST-mode pump *is* being incrementally improved, despite the team's "not a priority" posture.
2. The change is specifically "yield to browser more often" — a motion in the direction of "support long-running async work in user code."

**Action for Carbide:** if not already, pin whether Carbide is running against net10.0 or net9.0 runtime when T2.1 reproduces. If net9 works but net10 trips, this PR is a suspect. If both trip, the PR is irrelevant — but either way, worth validating.

### 7.2 `.NET 10 Runtime-Async` feature

[`dotnet/runtime#109632`](https://github.com/dotnet/runtime/issues/109632) — "tracks progress on implementing so-called 'runtime-async', meaning implementation for async methods directly inside the .NET runtime. This is in contrast to the current support which is implemented only in language compilers, like C#, VB, and F#."

As of February 2026 (per the issue's recent comments):

> Currently all libraries (System.* and ASP.NET Core) have been compiled with `runtime-async=off`, this causes a state machine to be generated in the IL.

And Andy Gocke (runtime team, 2026-02-20):

> Short answer is the same thread pool we use now, but with continuations generated and managed by the runtime instead of the c# compiler.

So runtime-async in .NET 10 preview/.NET 11 uses the same ThreadPool infrastructure for continuations. It moves the state-machine generation from the C# compiler into the runtime, which could in principle change the exact code path `AsyncTaskMethodBuilder` takes during suspension. The research question: **does runtime-async, when enabled, avoid the specific `Monitor.Wait(INFINITE)` code path that trips Carbide?**

I could not find public evidence either way. The feature is experimental, requires opt-in via csproj flags, and the BCL hasn't been recompiled with it yet — so most of the code that triggers the trap (the BCL itself, e.g., `ManualResetEventSlim.Wait` in `Task.Delay`'s completion propagation) wouldn't benefit from it even if user code opted in.

**Action for Carbide:** worth a speculative experiment if all else fails — `<Features>runtime-async</Features>` in the user-code csproj — but low-confidence as a fix. Better as a data point after the cheaper fixes are exhausted.

### 7.3 `.NET 11 Preview 1` runtime-async progress

[InfoQ: .NET 11 Preview 1 Arrives with Runtime Async](https://www.infoq.com/news/2026/02/dotnet-11-preview1/) — runtime-async is on track for broader availability in .NET 11, possibly out of preview. No mention of WASM specifically, but if the approach is "same thread pool, different state-machine generation," the WASM trap is unlikely to be resolved by runtime-async alone.

### 7.4 The fsharp/#19110 repro is the best-specified Carbide-adjacent test

The F# team (via T-Gro) asked whether "there should be an alternative builder for WASM that avoids problematic API calls? A library replacing the built in one?" This is essentially the Option D question, now being asked at the F# community level.

If the F# team adopts an alternate `TaskBuilder` for WASM, it would be a direct reference implementation Carbide could study for a C#-side equivalent. As of April 2026, no such F# library has shipped. But fsharp/#19110 is the issue to watch for concrete architectural patterns.

### 7.5 `dotnet/runtime#92713` — async JSExport killing node process

[dotnet/runtime#92713](https://github.com/dotnet/runtime/issues/92713) (2023) — "Invoking async [JSExport] and [JSImport] kills the process on node, but works in browser" — is notable for being a *different* failure mode that happens to share the JSExport-re-entry vicinity. The trigger was "any kind of concurrency" from node; the fix was merged as PR #92871 in .NET 8.0.0. The PR reference is not directly applicable to Carbide's case (Carbide's target is browser, not node, so the node-specific path is irrelevant), but it's a data point: the JSExport-async-entry path has been iteratively fixed before. That makes Vladimir's potential repro (§3.5) a plausible candidate for a similar fix-by-PR if it gets runtime-team attention.

### 7.6 Roslyn-specific detail: Microsoft.CodeAnalysis on .NET 11 WASM

[dotnet/roslyn#82361](https://github.com/dotnet/roslyn/issues/82361) (closed "not planned", 2025) — "Roslyn not working on .NET 11 WASM". The specific failure:

> `System.Threading.SemaphoreSlim.WaitCore fails because the platform doesn't support the operation in single-threaded WASM environments.`

Closed as "not planned" by the Roslyn team. This is significant context for Carbide because it confirms that the Roslyn team's position in 2025 is *still* not to support `Roslyn-in-browser` officially, five years after MerlinVR's `concurrentBuild: false` workaround (prior-art §1.6). The Roslyn-in-browser story continues to be a community/third-party concern, not a Microsoft-supported scenario. Carbide's choice to bundle Roslyn as a browser compiler is not "unsupported" — there are many deployed projects doing it — but it is "on you" from a Roslyn-team perspective.

### 7.7 Uno Platform's CoreCLR transition (2027 timeline)

From [Uno State of WebAssembly 2025/2026](https://platform.uno/blog/the-state-of-webassembly-2025-2026/), the most substantive forward-looking architectural item is Uno + Microsoft collaborating on CoreCLR-on-WebAssembly, projected for .NET 12 (2027):

> the team is working on transitioning from the Mono runtime to the CoreCLR runtime [...] multithreading as a focus area once CoreCLR ships in .NET 12 (2027).

And:

> one approach that's possible is to use feature detection for JSPI and fall back to the slower Asyncify approach if JSPI isn't available.

If CoreCLR-on-WASM with JSPI support materializes in .NET 12, the `Monitor.Wait(INFINITE)` trap could be completely eliminated at the runtime level (via stack-switching). But that's 18+ months out and not a deliverable Vladimir can plan around.

### 7.8 Task-like return types and per-method AsyncMethodBuilder

An under-appreciated C# 10 feature that's relevant to Option D: [`[AsyncMethodBuilder]` at method level](https://github.com/dotnet/csharplang/issues/1407) lets individual methods opt into a custom builder without changing the return type. From [Microsoft's C# 10 async method builders proposal](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/csharp-10.0/async-method-builders):

> When applied to an async method, the AsyncMethodBuilderAttribute would be used as the builder for that method, overriding any AsyncMethodBuilderAttribute on the return type.

For Carbide, this means a source generator could attach `[AsyncMethodBuilder(typeof(CarbideAsyncMethodBuilder))]` to user-compiled async methods, sidestepping the trap without forcing `CarbideTask` as the return type. The downside is that any pre-compiled BCL method (e.g., `HttpClient.GetAsync`) still uses the default builder and still traps. Not a full solution but a lower-cost-than-full-Option-D variant worth considering.

## 8. Practical shortlist for Carbide

Ranking workarounds by **cost-to-implement × restrictiveness**, lowest combined to highest.

### 8.1 Lowest-cost, lowest-restriction: route Carbide user-code entry through a Blazor component (new finding)

**Cost: ~100-200 LOC, one new Blazor host project.** **Restriction: user code gets the dispatcher invariant; works for suspended-await; no COOP/COEP; no runtime fork.**

Hypothesis backing this option:

1. §1.2 / §1.3: Blazor's component pipeline establishes the dispatcher scope the ST pump needs.
2. §2.3: The Strathweb Roslyn shell uses `Assembly.Load + MethodInfo.Invoke` from a Blazor `@onclick` and does not publicly report Carbide's trap.
3. §1.6: The difference between "works" (`runMain`-rooted) and "fails" (`JSExport`-rooted) in Carbide is exactly the difference between "inside a dispatcher scope" and "outside."

**Smoke test protocol:**

1. Create a minimal Blazor WASM host project that adds Carbide's assemblies as a dependency.
2. Render a single empty Razor component.
3. Expose a Blazor component method that wraps Carbide's current `RunInteractiveAsync`.
4. Call it from `OnAfterRenderAsync` (or a button `@onclick`) instead of from a JSExport.
5. Run the T2.1 minimal repro (`var tcs = new TaskCompletionSource(); await tcs.Task;`).
6. Observe. If it works: Option F-revised is the fix. Ship Carbide as a Blazor-hosted library instead of a bare-wasmbrowser library.

**Risk:** this inverts Carbide's embedding story from "drop-in ESM module for arbitrary web pages" to "Blazor library." Many consumers of Carbide would not accept that dependency. So this option, if it works, would need to be paired with a "Carbide-Blazor-lite shim" — a minimal Blazor host shipped with Carbide that acts like a dispatcher without requiring consumers to depend on Blazor. Architecturally this could just be: "write a tiny dispatcher class that mimics Blazor's `Dispatcher` semantics," ship it as part of Carbide, and make it the entry point for `MethodInfo.Invoke`.

**Why this might be wrong:** I cannot rule out that the actual invariant Blazor establishes is something deeper than "a `Dispatcher` exists" — it might be that the renderer's render loop is the active consumer that keeps the pump running, and without actual rendering happening, even a Blazor host wouldn't help. The test above would rule this out quickly.

**Concrete next step:** Vladimir, this is the experiment worth running first. Cost is ~1-2 hours of scaffolding plus T2.1 repro. If it works, it's a strictly better answer than Options A-E from the investigation report for embedding-preserving cases.

### 8.2 Low-cost, high-restriction: explicit `Task.Yield()` before every user-code suspension

**Cost: ~0 LOC Carbide-side.** **Restriction: user must insert `await Task.Yield()` before every await that would suspend. Library internals can't opt-in.**

Per fsharp/#19110 comment by `majocha`:

> If you put a `do! Task.Yield()` in the for loop, then this issue will gone.

And per Carbide's empirical pinpoint report PAIR-C: `Task.Yield()` works because `YieldAwaiter.OnCompleted` reschedules via `SC.Post` on the same tick, never actually blocking.

This is a documentation-only fix: document to Carbide users "if your code needs to genuinely suspend, insert `await Task.Yield()` just before the suspending `await`." This is the Meziantou pattern adapted to Carbide's constraint.

**Works for:** user code the user writes.
**Does not work for:** third-party library code that can't be modified, library internals, any `Task.Delay(N>0)`, any `HttpClient` operation.

This is Option E slightly relaxed. Honest scoping: it only enables a subset of patterns and the user has to remember to do it. Not acceptable alone but useful as a stopgap doc while a real fix is being worked on.

### 8.3 Medium-cost, medium-restriction: SpawnDev-style WebWorker-per-user-program

**Cost: ~500-1000 LOC to integrate SpawnDev pattern.** **Restriction: user program's await still trips the trap, so this doesn't fix T2.1 at all — it only provides isolation.**

Not a T2.1 fix. Listed here only to rule out as an option.

### 8.4 Medium-cost, low-restriction: Option B (`<WasmEnableThreads>true</WasmEnableThreads>`)

**Cost: runtime fork or switch to MT build, COOP/COEP server headers.** **Restriction: requires COOP/COEP on embedder; ~5-10 MB additional payload; AOT still experimental.**

Covered in prior-art §10, investigation §6 Option B. Enables `JSSynchronizationContext` and the full BCL async surface works. For hosted-Carbide deployments (Vladimir's own sites), this is the proven path — it's what Microsoft's own testing infrastructure uses. For drop-in CDN usage, blocked by COOP/COEP requirement.

### 8.5 High-cost, low-restriction: Option D (custom coroutine runtime via source generator)

**Cost: ~500-1000 LOC for source generator + scheduler + CarbideTask type.** **Restriction: user code must use CarbideTask; pre-compiled third-party libraries don't benefit.**

Reference implementation: UniTask (§3.2). The UniTask .NET Core NuGet package is roughly the right shape but lacks a scheduler; Carbide would ship a `setTimeout(0)`-driven scheduler.

The fundamental restriction — pre-compiled library incompatibility — is the same as in investigation report §6 Option D. If Carbide's use case is "compile user C# and run it; user code may call into third-party .NET libraries for realistic workloads," Option D fails because those libraries are already compiled against `Task`, not `CarbideTask`.

### 8.6 High-cost, low-restriction: Option C (patch `monitor.c`)

Same posture as investigation report. Further devalued by this research because:

- No upstream runtime-team appetite (confirmed by pavelsavara's 2026-04-02 comment).
- Pyodide's experience shows the Asyncify path has significant long-term maintenance costs.
- JSPI is the cleaner substrate long-term but isn't available in all browsers yet.

Not recommended unless all cheaper options fail.

### 8.7 Proposed ordering for Vladimir

1. **Option F-revised (§8.1): Blazor-host experiment first.** 1-2 hour investment; either solves T2.1 entirely (by getting the dispatcher scope) or sharpens the problem statement to "even the dispatcher doesn't help, so the trap is deeper."
2. If F-revised works: ship it with a Carbide-Blazor-lite shim (no full Blazor dependency).
3. If F-revised fails: **file a minimal-repro issue against dotnet/runtime.** Specific repro: `dotnet new wasmbrowser`, one assembly, one JSExport-reached-via-reflection method, `await tcs.Task` with a never-completed TCS. Based on the F# team's 1-day engagement on a similar issue (fsharp/#19110), and Microsoft's declining-but-nonzero appetite for regressions against the public API, this has a real chance of getting runtime-team attention — especially if the example is clean and ≤50 LOC.
4. If the runtime team declines: **ship Option B (multi-threaded) for hosted-Carbide, document the pattern with Option E for drop-in.** Dual-track the product.
5. **Don't pursue C or D** unless the strategic case becomes compelling (e.g., Carbide wants to replace part of the BCL for a specific use case, independent of this problem).

### 8.8 Anti-pattern: don't wait for JSPI adoption

Per §4, JSPI is at best 2026-late-or-2027-early-shipping in all browsers, and has no commitments from the .NET runtime team for integration. JSPI is the correct long-term solution but is not actionable on Carbide's timeline.

### 8.9 Anti-pattern: don't silently regress to "no user await at all"

Investigation report §6 Option E ("document sync-only user code") is tempting as a "ship something" fallback, but the empirical and literature evidence make it clear that:

1. The .NET community considers suspended-await in browser WASM a **working feature** for Blazor (Meziantou, fsharp/19110, Microsoft Learn docs).
2. Every sibling C#-in-browser project (Strathweb, DotNetLab, BlazorFiddle) demonstrably supports it (via Blazor dispatcher invariant).
3. Carbide's marketing surface (README, docs) has previously claimed broader async support.

Silently regressing to "no user await" without also flagging it prominently would mislead users. If E is chosen, it should be paired with a prominent "Carbide v0.x: Sync-Only Contract" documentation change, including an explicit "This is different from Blazor's model because …" paragraph. Option E is the honest scope-reduction; doing it quietly is not.

### 8.10 What to do if F-revised works partially

A partial F-revised result is conceivable: suspended-await might work for some Task shapes (simple `Task.Delay`) but not others (TCS-completed-from-JSExport). This was the case in the empirical pinpoint report (§table of 6 scenarios). If this happens:

1. **Document the specific shapes that work**, possibly as a code-analyzer + source generator that accepts allowed await shapes.
2. **Provide explicit `CarbideAwait(...)` wrappers** that take any Task and ensure it goes through a known-working code path (e.g., by wrapping it in a `TaskCompletionSource` completed from a known-scope call).
3. **Track the specific BCL method that fails** in a small "known-broken" list; file bugs against each one in dotnet/runtime with minimal repros.

This is still shippable — the user-visible contract is "most awaits work; see the compatibility list for what doesn't." That's a better story than "no awaits work" (E) and a better starting point than "everything works magically" (unjustified).

## 9. Flat URL list (priority reading order)

For reviewers who want to open the most important sources first:

1. **[dotnet/aspnetcore#54365](https://github.com/dotnet/aspnetcore/issues/54365)** — Make Blazor WebAssembly work on multithreaded runtime. Active April 2026 thread including pavelsavara's "ST build will keep supporting managed code on the UI thread for foreseeable future" commitment and the LostBeard/SpawnDev use-case discussion. This is the single most important source for the runtime team's current (2026) posture.
2. **[dotnet/fsharp#19110](https://github.com/dotnet/fsharp/issues/19110)** — F# `Cannot wait on monitors` regression on .NET 10 in Blazor component code. Most recent, most closely-shaped community repro; demonstrates that even Blazor-dispatcher-rooted code can trip the trap on certain builder shapes.
3. **[dotnet/runtime#114769](https://github.com/dotnet/runtime/pull/111198/files) (closed) + its context**: the System.IO.Compression async tests had to be excluded from WASM because they trip `Cannot wait on monitors`. Proof the runtime team themselves struggle with this.
4. **[dotnet/runtime PR #111198](https://github.com/dotnet/runtime/pull/111198)** (merged Jan 2025) — pump_count cleanup; proof that the ST-mode pump is actively being improved by pavelsavara. Directly relevant.
5. **[Strathweb: Building a C# Interactive shell in a browser with Blazor (WebAssembly) and Roslyn](https://www.strathweb.com/2019/06/building-a-c-interactive-shell-in-a-browser-with-blazor-webassembly-and-roslyn/)** (Filip W., 2019) — the closest published architectural sibling to Carbide. Uses `Assembly.Load + MethodInfo.Invoke` from a Blazor component. Does not publicly report Carbide's trap, which (§1.6 / §2.3) is the single-most-informative null result.
6. **[LostBeard/SpawnDev.BlazorJS.WebWorkers](https://github.com/LostBeard/SpawnDev.BlazorJS.WebWorkers)** — production-deployed community library demonstrating "N copies of ST runtime, one per worker" pattern. 323k+ downloads. Confirms ST mode is production-viable long-term.
7. **[Cysharp/UniTask](https://github.com/Cysharp/UniTask)** — the reference implementation of Option D's custom AsyncMethodBuilder approach. 10k+ stars; ships in production Unity games on WebGL/WASM.
8. **[Microsoft Learn: .NET on Web Workers](https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-on-webworkers?view=aspnetcore-10.0)** — official Microsoft-sanctioned ST-runtime-in-WebWorker pattern, including `dotnet new blazorwebworker` template (.NET 11+) and `DotNetOnWebWorkersReact` sample.
9. **[Meziantou: How to prevent the UI from freezing while executing CPU intensive work in Blazor WebAssembly](https://www.meziantou.net/don-t-freeze-ui-while-executing-cpu-intensive-work-in-blazor-webassembly.htm)** — canonical community guidance on `Task.Yield() + Task.Delay(1)` patterns that demonstrably work in ST Blazor WASM.
10. **[ComponentBase.cs on dotnet/aspnetcore](https://github.com/dotnet/aspnetcore/blob/main/src/Components/Components/src/ComponentBase.cs)** — the `InvokeAsync` implementation that establishes the dispatcher scope. Key evidence for §1.2's mechanism claim.
11. **[dotnet/runtime#109632](https://github.com/dotnet/runtime/issues/109632)** — Runtime-Async feature tracking. The longer-term direction for how continuations are generated and managed.
12. **[V8 JSPI blog post](https://v8.dev/blog/jspi)** — canonical JSPI Chrome 137 shipping announcement; the "JSPI does not suspend JavaScript" constraint.
13. **[gzuidhof/coi-serviceworker](https://github.com/gzuidhof/coi-serviceworker)** — COOP/COEP workaround for static hosts, relevant for deploying Option B on GitHub Pages etc.
14. **[Uno Platform: The State of WebAssembly – 2025 and 2026](https://platform.uno/blog/the-state-of-webassembly-2025-2026/)** — community-level survey of WASM adoption; includes "Mono → CoreCLR in .NET 12" projection and current JSPI status across browsers.
15. **[dotnet/runtime/blob/main/src/mono/browser/runtime/scheduling.ts](https://github.com/dotnet/runtime/blob/main/src/mono/browser/runtime/scheduling.ts)** — the single-threaded ST pump source; complements prior-art report's Appendix D.

---

*End of workarounds-research report.*
