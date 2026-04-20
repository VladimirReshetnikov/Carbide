# Carbide T2.1 follow-up research report

Created (UTC): 2026-04-20T18:20:09Z
Repository HEAD: e4e53b7638153472a3db0e2115b9345cb6e84283

## Executive summary

The roadblock described in `carbide-T21-investigation-report__2026-04-20__17-11-37-000000.md` is real enough to block the original `carbide-gh` attempt, but it is not as broad or final as that report presents it.

What is firmly established is this:

- Single-threaded browser WebAssembly does not support blocking waits such as `Task.Wait`, `.Result`, `Monitor.Wait`, and related monitor-backed primitives on the main thread. Upstream .NET explicitly treats that as an environment limitation and, since .NET 6, intentionally fails such waits instead of letting them deadlock unpredictably.
- Carbide at the current `HEAD` also fails on several non-blocking async shapes that matter for an interactive console: `Task.Delay`, `CarbideConsole.DelayAsync`, `CarbideConsole.WaitForResizeAsync`, and the Ctrl+C path all fail with `PlatformNotSupportedException: Cannot wait on monitors on this runtime` after the program has already started and printed output.
- However, Carbide already has at least one real suspended-await success case in the browser today: `CarbideConsole.ReadLineAsync` works in the Playwright browser fixture. That alone disproves the blanket claim that "any await that actually suspends" is fundamentally impossible on single-threaded browser WASM.
- The skipped T2.1 fixtures are not one homogeneous failure. `ReadLineAsync` passes, resize and Ctrl+C fail fast with PNSE, and `ReadKeyAsync` appears to hang rather than fail in the same way.

So the right conclusion is narrower:

> Carbide currently hits a significant browser-WASM async-integration limitation for several important interactive-console patterns, but the limitation is not equivalent to "browser-hosted C# console apps with real async are fundamentally impossible." It is a scoped but important runtime-integration problem, not an existential impossibility result.

The simplest plausible workaround I could confirm from public prior art is not "build a custom threaded runtime". The simpler, lower-cost options are:

- restrict Carbide's browser interactive story for now to the subset that already works (`ReadLineAsync`-style line input plus output);
- disable known internal blocking parallelism in specific dependencies when that is the real culprit (for example Roslyn's `concurrentBuild: false` workaround from upstream discussion);
- investigate a deeper runtime-integration fix modeled on the official browser scheduling machinery rather than Carbide's current ad hoc setup;
- when the app does not need synchronous UI-thread interop, consider the already-documented single-threaded-runtime-on-WebWorker model before jumping to a custom multithreaded runtime pack.

A custom multithreaded runtime or `FEATURE_WASM_MANAGED_THREADS`-style path remains a real option, but it should be treated as a high-cost fallback, not the first conclusion.

## Question answered by this report

This report re-evaluates the T2.1 conclusion after reviewing:

- the original Carbide T2.1 investigation report;
- the new Carbide prior-art report;
- the relevant current Carbide source code and browser fixtures;
- local reproduction results from the current repository state;
- git history that shows how the current explanation formed; and
- current upstream .NET runtime and Blazor discussions.

The specific questions are:

- Is the T2.1 roadblock as significant as the original report says?
- Are there simpler workarounds than the options listed there?
- What does public prior art say about this class of problem?
- How did Carbide arrive at its current interpretation?

## Bottom-line answer

### Significance

Yes, the roadblock is significant for Carbide's immediate T2.1 goal.

It blocks important interactive-console behaviors that the original `carbide-gh` scenario wanted:

- timed waits;
- resize-driven wakeups;
- Ctrl+C cancellation timing;
- probably some key-oriented input flows.

But no, it is not as significant as the original report claims.

It does **not** justify the stronger statement that browser-hosted C# console apps using the Console API cannot meaningfully use suspended async on single-threaded browser WASM. Carbide itself already demonstrates one counterexample (`ReadLineAsync`), and upstream .NET runtime code clearly contains a browser-side non-blocking async/timer/background-job pump.

### Simpler workarounds

Yes, there are simpler workarounds than "custom multithreaded runtime or bust", but they split into two very different categories:

- **Pragmatic product-scope workarounds** that reduce the problem surface right now.
- **Integration-level engineering workarounds** that are still substantial, but much smaller and less risky than maintaining a custom threaded runtime flavor.

The best short list is:

1. Treat browser interactive support as a supported subset, not an all-or-nothing story. Ship line-oriented REPL / console interaction first, and explicitly defer resize, Ctrl+C timing, and read-key semantics.
2. Where a dependency internally reaches blocking waits, remove or disable the blocking path instead of redesigning the whole runtime around it. Roslyn's `concurrentBuild: false` is the clearest public example.
3. Revisit Carbide's browser scheduling integration using the official runtime's `JSSynchronizationContext` and background-job model as the reference shape, instead of assuming the only escape hatch is threads.
4. If the workload can tolerate message-based UI interaction, consider running a single-threaded .NET runtime in a WebWorker. This has official .NET samples/docs work and community prior art already.
5. Keep a multithreaded/custom-runtime plan as a fallback only for the case where Carbide truly needs broad compatibility with arbitrary libraries that internally block.

## What the original T2.1 report got right

The original report is directionally correct on several important points.

### 1. Blocking waits are genuinely unsupported on browser main-thread WASM

This is the strongest and most stable conclusion in the whole investigation. Upstream runtime documentation and issue discussion all agree:

- browser execution is single-threaded by default;
- blocking monitor-based waits on the main thread are unsafe or impossible;
- .NET 6 intentionally started failing `.Wait()` calls rather than allowing unpredictable deadlocks.

This part of the original report should be kept.

### 2. A multithreaded runtime is a materially heavier option

The original report is also right that browser multithreading is not a free switch:

- it requires a separate runtime flavor;
- it requires browser security headers such as `Cross-Origin-Embedder-Policy: require-corp` and `Cross-Origin-Opener-Policy: same-origin`;
- JavaScript interop remains constrained to the main thread even when threads are enabled;
- upstream still describes browser multithreading as experimental.

So the cost/risk framing of the multithreaded option was basically sound.

### 3. For arbitrary third-party libraries, blocking code paths remain a serious compatibility hazard

If Carbide wants to run arbitrary console applications and arbitrary libraries in the browser, any internal use of `Task.Wait`, `.Result`, `ManualResetEventSlim.Wait`, or other monitor-backed waits remains toxic on the single-threaded runtime.

This is a real product-design constraint and not just an implementation accident.

## What the original T2.1 report overstates or gets wrong

### 1. The blanket "any suspended await fails" claim is false

Current Carbide already has a counterexample:

- `src/Carbide/packages/core/test/browser/interactive-readline.spec.mjs` passes after rebuilding current assets;
- its program awaits `CarbideConsole.ReadLineAsync()`;
- the task is not already complete when awaited;
- the task later completes from browser-side input delivery.

That is exactly the kind of real suspension the original report said could not work.

This does not make the T2.1 problem disappear, but it changes its nature. The problem is not "await that suspends is impossible". The problem is "some async/scheduling shapes resume correctly in Carbide's current browser runner, and some do not".

### 2. The failing fixtures are not one homogeneous runtime law

Local follow-up reproduction shows at least three categories:

| Scenario | Current result | Notes |
| --- | --- | --- |
| `ReadLineAsync` fixture | Passes | Real suspended await already works |
| Resize fixture | Fails fast with PNSE | Prints initial output, then fails after `ready` |
| Ctrl+C fixture | Fails fast with PNSE | Same broad failure shape as resize |
| `Task.Delay` one-off program | Fails fast with PNSE | Not limited to Carbide's custom delay wrapper |
| `ReadKeyAsync` fixture | Appears to hang | Different failure shape from resize/Ctrl+C |

That means the current report flattens several distinct failure modes into one story.

### 3. The current callback-based delay rewrite did not actually solve the general problem

The T2.1 report describes the callback-based `CarbideConsole.DelayAsync` rewrite as the right lower-level pattern. Even if that pattern is directionally better than Promise-to-Task marshaling, it does **not** solve the broader failure shape by itself.

At current `HEAD`:

- plain `Task.Delay(50)` still fails with the same PNSE;
- `CarbideConsole.DelayAsync` in the Ctrl+C fixture still fails;
- `WaitForResizeAsync` still fails;
- wrapping the awaited `Task` in `ValueTask` did not help in local tests;
- clearing the `SynchronizationContext` inside user code did not help in local tests.

So the roadblock is deeper than one broken JSImport signature or one missing callback shim.

### 4. The custom-SynchronizationContext theory is plausible but not yet proven as the whole root cause

The upstream discussion in `dotnet/runtime#69409` makes the custom-sync-context theory very plausible, and the new Carbide prior-art report was right to question the original T2.1 certainty.

But local follow-up experiments did **not** validate the simplest form of that theory:

- calling `SynchronizationContext.SetSynchronizationContext(null)` at the beginning of user code did not make `Task.Delay` or `WaitForResizeAsync` succeed;
- user-code-side `ValueTask` wrapping and `ConfigureAwait(false)` did not change the outcome either.

That does not prove the custom context is irrelevant. It only proves the simple user-code-level fixes are insufficient.

The likely reality is that the problem sits somewhere in the interaction between:

- the host-side `RunInteractiveAsync` entrypoint await;
- Carbide's custom browser scheduling assumptions;
- JSImport/JSExport completion paths; and/or
- runtime heuristics around continuations and browser thread-pool emulation.

## Local reproduction from current `HEAD`

Repository state used for the follow-up experiments:

- `HEAD`: `e4e53b7638153472a3db0e2115b9345cb6e84283`
- Worktree: clean before documentation edits
- Browser assets rebuilt before running Playwright/browser experiments

### Confirmed passing cases

- `interactive-readline.spec.mjs` passes.
- `interactive-sync-throw.spec.mjs` passes.

The important one is `interactive-readline`, because it proves that Carbide can already survive at least one later-completing browser task.

### Confirmed failing cases

Manual browser runs against the skipped fixtures on rebuilt assets showed:

- `interactive-resize.html` reaches `ready`, then fails with `System.PlatformNotSupportedException: Cannot wait on monitors on this runtime.`
- `interactive-ctrlc.html` reaches `ready`, then fails with the same exception.

I then ran several one-off browser programs against the current runtime integration. All of the following failed after printing `ready` and before resuming past the await:

- `await Task.Delay(50);`
- `await CarbideConsole.WaitForResizeAsync();`
- the same two cases after `SynchronizationContext.SetSynchronizationContext(null);`
- the same two cases wrapped as `await new ValueTask(task);`
- the same two cases wrapped as `await new ValueTask(task).ConfigureAwait(false);`

Those follow-up experiments matter because they eliminate several attractive but overly simple explanations.

### Provisional interpretation of the local evidence

The local evidence supports the following narrower statement:

- Carbide's current browser interactive runner can already handle at least one real suspended-await pattern (`ReadLineAsync`), but several other awaited patterns still trigger a monitor-wait failure during resumption/continuation handling.

That is a very different conclusion from "browser async console apps are impossible".

## Relevant code-path observations

The most relevant source files for this issue are:

- `src/Carbide/packages/core/src/Services/ProjectCompiler.cs`
- `src/Carbide/packages/core/src/CompilationInterop.cs`
- `src/Carbide/packages/core/src/Terminal/CarbideConsole.cs`
- `src/Carbide/packages/core/src/Terminal/BrowserTerminalReader.cs`
- `src/Carbide/packages/core/src/Terminal/CarbideSyncContext.cs`
- `src/Carbide/packages/core/src/Terminal/CarbideTerminalInterop.cs`
- `src/Carbide/packages/core/src/Terminal/TerminalInputState.cs`
- `src/Carbide/packages/core/src/ts/terminal/bridge.ts`

### `ReadLineAsync` is the strongest counterexample

`BrowserTerminalReader.ReadLineAsync` returns a task backed by a `TaskCompletionSource<string?>`, and the browser fixture that awaits it succeeds.

That proves all of the following already work together in at least one path:

- browser-side event delivery;
- JS -> C# bridge entry;
- later completion of a task that was incomplete when first awaited;
- resumption of user code after that completion.

Any theory that cannot explain this success case is incomplete.

### The current sync-context comments and the current behavior are not fully aligned

`ProjectCompiler.RunInteractiveAsync` currently re-installs `CarbideSyncContext` immediately before invoking user code, and it avoids `ConfigureAwait(false)` when awaiting the returned user task. The surrounding comments frame this as the only working path on browser WASM.

That explanation now looks too certain. The follow-up experiments show that even when user code clears the context immediately, the failing `Task.Delay` and `WaitForResizeAsync` cases still fail. So the custom context may still be part of the problem, but it is not the whole user-visible story.

### `bridge.ts` contains stale intent that is not reflected in the C# sync context

`src/Carbide/packages/core/src/ts/terminal/bridge.ts` contains comments about a `scheduleMacrotask` path, but current `CarbideSyncContext` does not actually import and use that helper. That is not itself proof of root cause, but it is concrete evidence that the current implementation and the documented theory around yielding/posting drifted during T2.1 experimentation.

### `WaitForResizeAsync` failing means the problem is not just "async method wrapper vs raw TCS"

One tempting explanation was:

- `ReadLineAsync` works because it returns a raw task;
- `ReadKeyAsync` fails because it is an `async Task<ConsoleKeyInfo>` wrapper that awaits internally.

That explanation is too simple. `WaitForResizeAsync` also returns a task directly, yet it still fails. So the issue is not reducible to "avoid `async Task` methods".

## Git history summary

This section summarizes how Carbide arrived at the current interpretation, as requested.

### 1. T2 introduced the interactive console and skipped the problematic browser fixtures immediately

Commit `35368142f334c7cac9769d4a4c4170de6acc537e` (`Carbide: Implement T2`) added the interactive terminal stack and the relevant browser fixtures.

Crucially, the problematic browser tests were already introduced as skipped in that commit:

- `interactive-readkey.spec.mjs`
- `interactive-resize.spec.mjs`
- `interactive-ctrlc.spec.mjs`

The skip comments already pointed at a browser async/runtime problem, but the comments were narrower and more tentative than the later T2.1 report.

### 2. T3 improved stock `System.Console` integration, but not the T2.1 gap

Commit `8c224d6b6` (`Carbide: Implement T3`) focused on the `System.Console` overlay/fork and additional browser console behavior, not on resolving the suspended-await gap.

This matters because it means the T2.1 limitation is not a new regression introduced by T3. It is a known gap that survived across milestones.

### 3. The broad "custom SynchronizationContext will fix it" theory hardened during the first `carbide-gh` attempt

Commit `170f06dbffedb527ace4867c3109d51a62a1838e` (`Carbide: An initial attempt at carbide-gh demo`) changed `ProjectCompiler.RunInteractiveAsync` in two especially relevant ways:

- it re-installed `CarbideSyncContext` immediately before invoking user code;
- it removed `ConfigureAwait(false)` when awaiting the user's returned `Task` / `ValueTask`.

The commit comments there are where the strongest version of the "keep the custom sync context, otherwise awaits fail" explanation appears in source.

### 4. The "failed attempt at fixing awaiting async tasks" then locked in a broader explanation than the evidence justified

Commit `446d0ec58e5948e558d68e22c97069a70c8c23a3` (`Carbide: A failed attempt at fixing awaiting async tasks`) added:

- the original T2.1 investigation report;
- the callback-based `DelayCallback` rewrite;
- additional explanatory comments asserting a more global runtime limitation.

This is the point where the local interpretation became much stronger than "some fixtures are still broken". The follow-up evidence in this report suggests that interpretation overshot what was actually proven.

### 5. The demo was then archived as an artifact, preserving the failure framing

Commit `b88f6a763a1c673abc8672555236891f54f15fe4` (`Carbide: Move non-working carbide-gh example to artifacts`) moved the demo attempt into report artifacts and kept the T2.1 report as the explanatory anchor.

This cemented the idea that the roadblock was fundamental enough to stop the first demo entirely.

### 6. The new prior-art report is the first repository document to materially weaken that conclusion

Commit `e4e53b7638153472a3db0e2115b9345cb6e84283` added `carbide-T21-prior-art-research__2026-04-20__17-40-00-000000.md`.

That report correctly pointed out the tension in the original claim:

- upstream clearly rejects blocking waits;
- upstream also clearly supports real non-blocking async on single-threaded browser WASM;
- therefore Carbide's problem is likely narrower than the original T2.1 report claimed.

### Overall history takeaway

The history suggests that the strong "fundamental runtime impossibility" framing was not the result of long multi-day convergence. It crystallized quickly during a same-day cluster of T2/T3/demo/T2.1 investigation work.

That does not make it unreasonable, but it does mean it should be treated as a fast local conclusion that now deserves revision in light of broader evidence.

## Public prior art and web research

Only primary sources were used for the technical conclusions in this section.

### 1. Upstream .NET runtime explicitly rejects blocking waits on browser main-thread WASM

The clearest sources are:

- `dotnet/runtime#61308`
- `dotnet/runtime#61381`
- `src/mono/wasm/features.md` in `dotnet/runtime`

Relevant points:

- In `dotnet/runtime#61308`, Javier Calvarro Nelson explains that the browser runtime is single-threaded and if a library blocks the main thread there is no extra thread available to resume the work and unblock it.
- In `dotnet/runtime#61381`, Lewing states that in the move to .NET 6 the runtime deliberately chose to fail `.Wait()` calls instead of letting some of them fail randomly at runtime.
- The current `features.md` says browser multithreading is opt-in and experimental, and also states that blocking on the main thread with operations like `Task.Wait` or `Monitor.Enter` is not supported by browsers.

This is the strongest evidence that the original report was right about blocking waits.

### 2. Upstream .NET runtime also clearly has a working non-blocking async scheduling model on single-threaded browser WASM

The strongest sources here are:

- `src/mono/browser/runtime/scheduling.ts` in `dotnet/runtime`
- `src/libraries/System.Runtime.InteropServices.JavaScript/.../JSSynchronizationContext.cs` in `dotnet/runtime`
- the fact that Blazor and other browser-hosted .NET code routinely use `await` on timers and JS interop tasks without requiring multithreading

`scheduling.ts` contains the browser-side background-job and timer scheduling path (`SystemJS_ScheduleBackgroundJobImpl`, `SystemJS_ScheduleTimerImpl`, `mono_background_exec_until_done`, timer pump helpers). `JSSynchronizationContext` uses a queue plus a scheduled JS pump rather than Carbide's current inline `Post` behavior.

This is why the original T2.1 claim cannot stand as written. The official runtime already contains a non-blocking async execution path for the single-threaded browser environment.

### 3. Custom `SynchronizationContext` interactions really do affect continuation placement

The key public discussion is `dotnet/runtime#69409`.

Important takeaways from the runtime-team discussion there:

- having a sync context at all can change where `Task` continuations run;
- same-instance identity of the sync context matters, because await infrastructure can avoid posting when the current context matches the one captured at await time;
- `ConfigureAwait(false)` is not a universal escape hatch once runtime heuristics and sync-context checks enter the picture;
- the browser runtime needed a real, runtime-installed synchronization context for main-thread affinity in the multithreaded design.

This upstream discussion strongly supports the idea that Carbide should not invent its own simplistic browser sync model without closely matching official behavior.

### 4. Official docs and issues show that worker-hosted single-threaded .NET is already a real, documented lane

The most useful sources are:

- `dotnet/runtime#95452`
- references from that issue to demo repos and docs efforts

That issue exists specifically to document starting a **single-threaded** .NET runtime on a WebWorker for workloads that do not need direct UI-thread C# interaction.

This matters because it gives Carbide a simpler architectural escape hatch than "go directly to multithreaded runtime" for some scenarios.

### 5. Upstream Blazor/runtime discussions show the broader ecosystem is hitting adjacent versions of the same problem

The most relevant sources are:

- `dotnet/aspnetcore#54365`
- `dotnet/runtime#126438`

What they show:

- the official team does not want to regress single-threaded builds in order to support threaded builds;
- synchronous JS interop and main-thread execution model constraints are still actively debated in 2026;
- community libraries have already explored alternative worker/main-thread arrangements;
- as of April 2026, Pavol Savara still says single-threaded builds running managed code on the UI thread will be supported for the foreseeable future, while also saying mixed-mode threading is not planned.

This confirms that Carbide is not the first project to encounter the underlying class of browser/runtime constraint. The issue is well known upstream; Carbide's particular shape is just one instance of it.

### 6. There are concrete, simpler workarounds in prior art for some subcases

The best concrete example is from `dotnet/runtime#61381`:

- MerlinVR reports that setting Roslyn `CSharpCompilationOptions.concurrentBuild` to `false` works around a specific browser-WASM failure caused by `WaitForWorkers()`.

That does **not** solve Carbide's current resize/Ctrl+C/delay failures. But it does matter because it shows a pattern:

- when the real problem is an internal blocking parallel path in a dependency, the first workaround should often be to disable that path, not to redesign the whole hosting runtime.

## Assessment of simpler workaround options

This section re-evaluates simpler options than the original T2.1 report's recommended custom-runtime path.

### Option 1: Narrow the supported browser interactive subset

**Assessment:** immediately practical and probably the best short-term move.

Carbide can already support a meaningful browser-console subset:

- output;
- line-oriented input via `ReadLineAsync`;
- synchronous exceptions / compilation diagnostics;
- stock `System.Console` ANSI and basic window-size priming from T3.

For many REPL/demo scenarios, that is already useful enough to ship as a documented subset.

This option does not solve the hard runtime issue, but it converts a hard blocker into a scoped limitation.

### Option 2: Eliminate known blocking code paths in dependencies

**Assessment:** highly worthwhile whenever the failing path is inside a specific library.

Example:

- Roslyn `concurrentBuild: false` from `dotnet/runtime#61381`.

This is not the whole Carbide T2.1 solution, but Carbide should absolutely use this style of workaround where applicable. It is much cheaper than a custom runtime.

### Option 3: Rework Carbide's browser scheduling model to look more like the official one

**Assessment:** the most promising real fix that is still simpler than multithreading.

The local evidence says Carbide is not hitting a universal browser law, because one suspended-await path already works. The upstream runtime sources say single-threaded browser async already has a real scheduling model. That strongly suggests Carbide should investigate whether its runner is violating some assumption the official runtime machinery expects.

The likely direction is:

- minimize custom browser scheduling behavior;
- understand exactly how the official `JSSynchronizationContext` and background-job pump are intended to be used;
- stop relying on comments/theories that were inferred during the demo attempt;
- build a minimal repro outside Carbide if needed, then narrow the divergence.

This is still real work, but it is much simpler than carrying a custom threaded runtime flavor.

### Option 4: Run a single-threaded .NET runtime inside a WebWorker when direct UI-thread interop is not required

**Assessment:** viable for some workloads, not a universal answer.

This option has both official and community prior art.

It is a good fit when:

- the app is mostly compute or message-driven;
- UI interaction can be proxied through messages;
- synchronous DOM/main-thread interop is not required.

It is a poor fit for a full xterm-backed interactive console if the goal is tight synchronous coupling to browser-side terminal APIs, resize signals, and low-latency control flow.

Still, it is meaningfully simpler than a full multithreaded runtime path and should be considered before jumping to threads.

### Option 5: Multithreaded/custom runtime path

**Assessment:** still valid, but should be demoted from first recommendation to fallback recommendation.

This becomes the right answer only if Carbide needs one or more of the following:

- broad compatibility with arbitrary libraries that internally block;
- true `System.Threading` semantics rather than carefully curated async-safe subsets;
- a product promise that browser-hosted execution behaves much more like desktop/server .NET.

The original report was too quick to recommend this path as the main answer. It is a valid path, but it has high configuration, hosting, and maintenance cost.

## Recommendations

### Recommendation 1: Revise the T2.1 narrative in Carbide docs

Carbide should stop saying or implying:

- that any suspended await is impossible on single-threaded browser WASM;
- that the T2.1 result proves browser Console API apps are broadly blocked unless Carbide ships a custom threaded runtime.

The correct current phrasing is narrower:

- blocking waits are fundamentally incompatible with the single-threaded browser runtime;
- Carbide currently has additional async-integration failures for several interactive-console patterns;
- some important suspended-await patterns already work.

### Recommendation 2: Treat browser interactive support as a staged compatibility matrix

For now, Carbide should explicitly separate:

- **known-good browser interactive behaviors**;
- **known-broken behaviors**;
- **known-untested or heterogeneous behaviors**.

A suggested initial matrix is:

- supported: output, `ReadLineAsync`, basic stock `Console` ANSI/state, compile/run/diagnostics;
- unsupported for now: `Task.Delay`, `DelayAsync`, resize wait, Ctrl+C timing, `ReadKeyAsync` browser fidelity.

This would let Carbide move forward without pretending the gap is solved.

### Recommendation 3: Investigate the runner/scheduler boundary before any threaded-runtime investment

This is the most important technical recommendation.

The next engineering investigation should aim to answer a narrower question than the original T2.1 report did:

> Why do some later-completing browser tasks resume correctly in Carbide while others end in a monitor-wait PNSE?

That investigation should be based on:

- a minimal repro outside Carbide if possible;
- direct comparison with the runtime's browser scheduling model;
- less confidence in the current custom-sync-context theory comments;
- careful isolation of host-side await behavior vs user-code await behavior.

### Recommendation 4: Use localized dependency workarounds aggressively

When Carbide encounters failures from specific libraries rather than from its own console surface, prefer the smallest dependency-local workaround first.

Examples:

- disable Roslyn parallel/concurrent build paths where necessary;
- prefer async APIs over sync wrappers whenever the dependency offers both;
- avoid or replace libraries whose browser path still uses blocking primitives internally.

### Recommendation 5: Keep worker-hosted ST runtime and MT runtime as separate fallback lanes

Carbide should keep both of these on the table, but as distinct options:

- **ST runtime on WebWorker** for message-driven/non-UI workloads;
- **MT/custom runtime** only for the highest-compatibility lane.

Those are not interchangeable, and the original T2.1 report leaned too quickly toward the heavier one.

## Final conclusion

The T2.1 roadblock is real, but the original investigation report overstates both its scope and the inevitability of the most expensive workaround.

The strongest stable conclusion is still that blocking waits are fundamentally incompatible with single-threaded browser main-thread WebAssembly. That part should not be watered down.

What should change is the larger interpretation.

Carbide is **not** facing proof that browser-hosted C# console apps with real async are impossible. Instead, Carbide is facing a narrower but still important runner/runtime integration problem where:

- some suspended-await patterns already work;
- some fail with PNSE;
- some appear to hang;
- the public upstream model says non-blocking browser async is supposed to work.

That means the right next move is not to jump immediately to a custom threaded runtime. The right next move is to narrow the failing shape, revise the docs, ship the known-good subset, and only escalate to worker-hosted or multithreaded runtime options when the narrower fixes have actually been exhausted.

## Primary sources

- [Original Carbide T2.1 investigation report](carbide-T21-investigation-report__2026-04-20__17-11-37-000000.md)
- [Carbide T2.1 prior-art research](carbide-T21-prior-art-research__2026-04-20__17-40-00-000000.md)
- [`dotnet/runtime#61308` - "Cannot wait on monitors on this runtime"](https://github.com/dotnet/runtime/issues/61308)
- [`dotnet/runtime#61381` - Roslyn / `.Wait()` discussion and `concurrentBuild: false` workaround](https://github.com/dotnet/runtime/issues/61381)
- [`dotnet/runtime#69409` - browser/main-thread synchronization-context discussion](https://github.com/dotnet/runtime/issues/69409)
- [`dotnet/aspnetcore#54365` - multithreaded Blazor WASM design discussion](https://github.com/dotnet/aspnetcore/issues/54365)
- [`dotnet/runtime#95452` - ST .NET runtime on WebWorker docs/sample effort](https://github.com/dotnet/runtime/issues/95452)
- [`dotnet/runtime#126438` - request to keep main .NET thread on browser UI thread when threading is enabled](https://github.com/dotnet/runtime/issues/126438)
- [`dotnet/runtime` `src/mono/wasm/features.md`](https://github.com/dotnet/runtime/blob/main/src/mono/wasm/features.md)
- [`dotnet/runtime` `src/mono/wasm/threads.md`](https://github.com/dotnet/runtime/blob/main/src/mono/wasm/threads.md)
- [`dotnet/runtime` `src/mono/browser/runtime/scheduling.ts`](https://github.com/dotnet/runtime/blob/main/src/mono/browser/runtime/scheduling.ts)
- [`dotnet/runtime` `JSSynchronizationContext.cs`](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Runtime.InteropServices.JavaScript/src/System/Runtime/InteropServices/JavaScript/JSSynchronizationContext.cs)
