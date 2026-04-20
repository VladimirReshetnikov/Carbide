# Carbide T2.1 — investigation report ("Cannot wait on monitors" on awaits that suspend)

- Created (UTC): 2026-04-20T17:11:37Z
- Repository HEAD: 0933154cf (after T3 landed, before any T2.1 attempt commits)

Status: **investigation, not a fix**. This report documents what T2.1 actually is, which theories were tested, which were ruled out, and which options remain open for future work. It replaces the earlier `carbide-T21-detailed-plan__2026-04-20__16-30-00-000000.md` whose root-cause section was wrong (now deleted).

Audience: repository owner Vladimir; future contributors picking this up.

Accompanying artifact: [`artifacts/carbide-gh-T21-artifact/`](artifacts/carbide-gh-T21-artifact/README.md) — the mini Spectre.Console + GitHub REPL we attempted to build on top of Carbide T3 as a consumer-shaped stress test of async suspension. It boots, renders its banner, and trips on the first `await Console.In.ReadLineAsync()`. Preserved as a reference, not a working demo.

## 1. Symptom recap

Any C# `await` inside `Project.runInteractive` that **actually suspends** trips:

```
System.PlatformNotSupportedException: Cannot wait on monitors on this runtime.
   at Carbide.Core.Services.ProjectCompiler.RunInteractiveAsync(String projectId, InteractiveOptions options)
```

"Actually suspends" means the awaited task's `IsCompleted` is `false` at the await site, so the state machine stops and waits for a continuation. Pre-completed awaits (e.g. `tcs.SetResult(); await tcs.Task;`) proceed synchronously and **do not trip**.

The trip fires with the state-machine frame as the stack's outermost managed frame; no deeper user-code frame appears. It fires synchronously during the suspension path — before any completion attempt, timer, or JS callback runs.

The T2 browser fixtures `interactive-readline`, `interactive-color`, `interactive-sync-throw` pass only because they either don't suspend on an await at all, or they pre-deliver input so the awaiter resolves synchronously. `interactive-readkey`, `interactive-ctrlc`, `interactive-resize` hit exactly this trap and were `test.skip`ped at T2.

## 2. The minimal reproducer

Inside `Project.runInteractive`, with `CarbideSyncContext` installed as `SynchronizationContext.Current`:

```csharp
// Fails with "Cannot wait on monitors":
var tcs = new TaskCompletionSource(TaskCreationOptions.None);
await tcs.Task;           // never completes — the TRIP fires here, synchronously, before any completion happens
```

Also fails:

```csharp
await Task.Delay(50);                         // trips
await Carbide.Terminal.CarbideConsole.DelayAsync(50);  // trips (even with our callback-JSImport rewrite)
```

Works:

```csharp
var tcs = new TaskCompletionSource(TaskCreationOptions.None);
tcs.SetResult();
await tcs.Task;           // awaits a pre-completed task — proceeds synchronously
await Task.CompletedTask; // same
await Task.Yield();       // YieldAwaiter.OnCompleted posts via SC.Post; CarbideSyncContext runs inline
```

The "works" list all avoid the state machine's wait-for-continuation-to-fire path. `Task.Yield()` in particular demonstrates that `CarbideSyncContext.Post` + state-machine interaction is fine — it's *specifically* `Task`-valued awaits that stall the state machine that trip.

## 3. The origin of the error message

`mono/metadata/monitor.c:1323` guards the infinite-timeout branch of `mono_monitor_wait_internal`:

```c
#ifdef DISABLE_THREADS
    if (ms == MONO_INFINITE_WAIT) {
        mono_error_set_platform_not_supported (error, "Cannot wait on monitors on this runtime.");
        return FALSE;
    }
#endif
```

In single-threaded browser-wasm (`DISABLE_THREADS` on), any `Monitor.Wait(obj)` with an infinite timeout throws `PlatformNotSupportedException` with exactly this message. So somewhere in the state-machine suspension path, .NET is calling `Monitor.Wait(obj, Timeout.Infinite)`.

The canonical `Monitor.Wait(INFINITE)` call site inside the BCL's async machinery is the fallback that `ManualResetEventSlim.Wait()` takes when its spin+kernel-event path has exhausted. A `ManualResetEventSlim.Wait()` fires during:

- `Task.Wait()` / `Task.Result` — blocking sync wait on a Task
- `ThreadPool` worker acquisition (wait-for-work) when no work is queued
- `SemaphoreSlim.Wait` (blocking)

We have not yet pinpointed exactly *which* of these fires in our state-machine suspension. The stack trace is obscured by the async state machine; breakpoint / debug instrumentation inside `AsyncTaskMethodBuilder.AwaitUnsafeOnCompleted` or `Task.UnsafeSetContinuationForAwait` would localize it.

## 4. Theories tested and rejected

Each theory below was instrumented and tested against the minimal reproducer on Mono-WASM browser single-threaded (Carbide's target).

### 4.1 Theory: JSImport Promise-to-Task marshaler forces `RunContinuationsAsynchronously`

**Source:** `JSMarshalerArgument.Task.cs:55` constructs the bridging TCS with `TaskCreationOptions.RunContinuationsAsynchronously`. This *does* force continuations through `TaskScheduler.Default`.

**Test:** replaced the JSImport's Promise-returning `delay` with a callback-based `delayCallback([JSMarshalAs<JSType.Function>] Action)` that completes a locally-owned `TaskCreationOptions.None` TCS. JSImport is now `void`; no Promise marshaling.

**Result:** still trips. The JS-side `delayCallback` logs show the setTimeout never fires before the PNS — the trip happens **at the await suspension**, not at completion. So `RunContinuationsAsynchronously` is neither necessary nor sufficient to produce the trip.

**Status:** rejected as root cause. The callback-based rewrite is a correct direction in general (it avoids one unnecessary hop) and is left in place in the current branch, but it is not by itself a fix.

### 4.2 Theory: `ConfigureAwait(false)` clears captured `SynchronizationContext` and dispatches through ThreadPool

**Source:** `ProjectCompiler.RunInteractiveAsync`'s outer `await task.ConfigureAwait(false)` on user code's Task.

**Test:** dropped `ConfigureAwait(false)` so the continuation captures `CarbideSyncContext`.

**Result:** still trips. Verified via diagnostic `Console.WriteLine` in user code that `SynchronizationContext.Current` is `CarbideSyncContext` both before and after the await; the capture path is correct.

**Status:** rejected as root cause. Change is kept (correctness — avoid unnecessary capture drop).

### 4.3 Theory: `CarbideSyncContext.Post` runs inline, which breaks the state machine's assumption that continuations run later

**Source:** `CarbideSyncContext.Post(d, state)` directly invokes `d(state)` synchronously.

**Test:** rewrote `Post` to enqueue via a JS macrotask (`setTimeout(cb, 0)`) through a new `scheduleMacrotask` JSImport, so continuations genuinely defer to the next event-loop tick.

**Result:** still trips. Diagnostic prints confirm the macrotask enqueue works; the trip still fires synchronously at suspension.

**Status:** rejected. Reverted (inline `Post` is simpler and equally broken, so the macrotask complexity buys nothing today).

### 4.4 Theory: `SynchronizationContext.Current` is being cleared between our install and user invoke

**Source:** `ProjectCompiler.RunInteractiveAsync` installs `CarbideSyncContext` at line 587, then `await`s compilation, then `reflectedEntry.Invoke` runs user code. The ambient SC could be cleared by the intervening awaits.

**Test:** re-installed `CarbideSyncContext` immediately before `reflectedEntry.Invoke` (a second `SetSynchronizationContext` call). Diagnostic printed `SC = CarbideSyncContext` at user-code entry.

**Result:** still trips. SC *is* correctly installed; the trip happens regardless.

**Status:** rejected. Change is kept (belt-and-suspenders — harmless redundancy).

### 4.5 Theory: the SC must also be installed at `InitAsync` boot (pre-`runInteractive`)

**Test:** already present as of T2 (`CompilationInterop.InitAsync:29-33` installs `CarbideSyncContext` if `Current` is null).

**Result:** irrelevant — the earlier install is still live at `runInteractive` entry, confirmed by prints.

**Status:** rejected.

## 5. What we now believe

The trip is a **Mono-WASM single-threaded runtime limitation**, not a Carbide bug. When a C# state machine actually suspends on an incomplete `Task` inside `DISABLE_THREADS` mode, *something* in the suspension machinery (very likely `AsyncTaskMethodBuilder`'s internal `ExecutionContext` flow or `IAsyncStateMachineBox` allocation path — both of which historically interact with `ManualResetEventSlim.Wait` and the ThreadPool queue) eventually reaches the `Monitor.Wait(INFINITE)` fallback, which trips the `DISABLE_THREADS` guard.

This matches the official Mono-WASM posture:

- `FEATURE_WASM_MANAGED_THREADS` on → `JSSynchronizationContext` provides a proper continuation scheduler via `Channel<WorkItem>`, and the runtime knows how to drive it. Real await-suspension works.
- `FEATURE_WASM_MANAGED_THREADS` off (single-threaded) → no `JSSynchronizationContext` is installed by default; async methods that never actually suspend work fine, but a suspension reaches into ThreadPool / Monitor primitives that are stubbed out.

Carbide targets single-threaded browser-wasm because (a) the COOP/COEP cross-origin-isolation requirement for multi-threaded wasm is onerous for a drop-in JS library and (b) our compile-and-run surface historically didn't need real async suspension. T2 introduced the first real need, and the T2.1 label was an early admission that the T2 implementation had papered over it by pre-delivering inputs in test fixtures.

## 6. Options for a real fix

Listed roughly in order of engineering cost (cheapest first).

### Option A — Ship single-threaded forever; rewrite all Carbide-side async to never genuinely suspend

Every Carbide-side await either resolves synchronously (pre-delivered input, already-completed JSImport) or runs through a **polling** pattern where user code explicitly calls a sync check and JS periodically invokes a C# "pump" JSExport. Essentially: treat the user program as a state machine Carbide drives by hand, one sync step at a time.

- **Scope:** rewrite `BrowserTerminalReader` to expose `TryDequeueLine` / `TryDequeueKey` sync methods; rewrite `CarbideConsole.ReadLineAsync` / `ReadKeyAsync` / `DelayAsync` as cooperative "resume token" APIs; rewrite the T1 run path so `RunInteractiveAsync` is *not* a `async Task` but a sync "start" that returns an interaction handle, with a separate JS-driven `pump` that runs one Carbide step per event-loop tick.
- **Blast radius:** ~500-800 LOC across `packages/core/src/Terminal/` and `Services/ProjectCompiler.cs`; changes the public shape of `CarbideConsole.*Async` methods (they'd no longer return `Task`, or would return a pseudo-Task that's always completed when dequeued); breaks source compatibility with any Carbide 0.x user code that relied on the Task-based API.
- **User-code compatibility:** stock `await`s in user code (which is the whole point of T3 for pre-compiled libraries) still don't work. The demo can't use `await httpClient.GetStringAsync(...)` directly; we'd need `CarbideHttp.FetchAsync(url, onDone: result => ...)` callback shapes throughout.
- **Delivers:** one-shot command runner demo, T2 fixtures restructured to avoid suspension, no real "wait for input" semantics.
- **Does not deliver:** the original T2/T3 promise of running unmodified pre-compiled libraries that do real async work.

### Option B — Ship a custom-built Mono-WASM runtime with `FEATURE_WASM_MANAGED_THREADS` enabled

Build `dotnet/runtime` from source with the multi-threaded wasm configuration; Carbide consumes that instead of the stock .NET SDK's browser-wasm runtime. `JSSynchronizationContext` becomes available and real async suspension works.

- **Scope:** fork `dotnet/runtime`, build with `-p:WasmEnableThreads=true`, package the resulting `dotnet.js` + `dotnet.native.wasm` + BCL DLLs into `@carbide/core`'s `_framework/`. Replace the existing publish overlay step with a pull-from-custom-runtime step.
- **Blast radius:** ~2 GB local checkout of `dotnet/runtime`, emscripten toolchain, CI-time runtime build (~20-40 min cold), ship ~5-10 MB additional `.wasm` per Carbide release.
- **Cross-origin-isolation requirement:** multi-threaded wasm **requires** the hosting page to send `Cross-Origin-Opener-Policy: same-origin` + `Cross-Origin-Embedder-Policy: require-corp` headers. This is a significant integration burden — third-party sites that want to embed Carbide have to reconfigure their servers. It also means Carbide can't be used from plain file:// URLs or from ES-module CDNs that don't send those headers.
- **User-code compatibility:** **all** async patterns work — `Task.Delay`, `HttpClient`, `await` on arbitrary `Task` — because `JSSynchronizationContext` is the Microsoft-blessed path and every BCL async primitive has been tested against it.
- **Delivers:** the full original T2/T3 promise, plus T2.1, plus everything the demo was supposed to do.
- **Does not deliver:** easy embedding in arbitrary third-party pages (COOP/COEP requirement).

### Option C — Patch `monitor.c` to treat `Monitor.Wait(INFINITE)` as "yield to event loop, then resume" in `DISABLE_THREADS`

Modify `mono/metadata/monitor.c:1324` so that instead of setting a `PlatformNotSupportedException`, it calls out to an emscripten-blessed yield (`emscripten_sleep` with an infinite loop, or pumps the event queue via `mono_main_thread_schedule_background_job`) and returns `TRUE` once the monitor is signaled. Effectively, we give up `DISABLE_THREADS`'s strict enforcement and rely on the single-threaded event loop to drain pending callbacks during the "wait."

- **Scope:** ~50-100 LOC of C changes to `monitor.c`, plus careful review of every other `DISABLE_THREADS` guard in the runtime (there are ~20-40 of them — `thread.c`, `wait.c`, `semaphore.c`, etc.). The change needs `emscripten_sleep` which requires `ASYNCIFY=1` at link time — a ~20% binary-size hit and a ~10-30% runtime slowdown.
- **Ship path:** same as Option B — fork + custom runtime build + shipped assets.
- **Blast radius:** similar to Option B on the build-infra side, but no COOP/COEP requirement. However: anything that genuinely expects `Monitor.Wait(INFINITE)` to block is now unreliable (though there's arguably nothing on single-threaded browser-wasm that *should* block like that anyway).
- **User-code compatibility:** similar to Option B — real awaits work. But corner cases around ASYNCIFY's stack-unwinding may bite user code that does heavy recursion or very long sync sections (ASYNCIFY pays per frame on the stack).
- **Delivers:** T2.1 plus full async suspension without needing cross-origin isolation.
- **Does not deliver:** a supported configuration — we'd be shipping a modified runtime that diverges from Microsoft's.

### Option D — Replace the Task-based `await` surface with a Carbide-specific `IEnumerator`-based coroutine runtime

Carbide ships a C# source-level rewriter that transforms user `async`/`await` into an `IEnumerator<YieldInstruction>` state machine (Unity-style). No runtime `Task` is ever actually suspended; the coroutine runs step-by-step and Carbide pumps it between JS event-loop ticks.

- **Scope:** source generator + companion YieldInstruction API (~300-500 LOC). Only user code that opts in is rewritten — pre-compiled libraries are untouched (so Spectre.Console still works in sync mode, but anything calling `await` in a pre-compiled library still fails the same way).
- **User-code compatibility:** **restrictive** — user code has to use Carbide's coroutine API instead of `async`/`await`, OR we reach all the way to rewriting user IL post-compile. The latter is very invasive.
- **Delivers:** a working coroutine model for user code that opts in.
- **Does not deliver:** the ability to `await` pre-compiled library methods.

### Option E — Leave T2.1 unshipped and adopt "sync-only user code" as the supported contract

Document that Carbide's interactive run supports user code that **never suspends** on an await. Real input is delivered pre-emptively where possible; async libraries are used only for fire-and-forget patterns where the caller doesn't await the result. The T2 fixtures stay skipped.

- **Scope:** zero code; doc-only change.
- **User-code compatibility:** forbids exactly the patterns T2 and T3 were meant to enable. The `carbide-gh` demo's REPL doesn't work; only its one-shot form (already a workable Option A for the demo) works.
- **Delivers:** no technical progress, but honest scoping.

## 7. Recommendation

Pursue **Option B** if Carbide is intended for hosted use (Vladimir's own deploys, controlled CDN) where COOP/COEP is manageable. The once-and-done cost of building + shipping a multi-threaded runtime buys the full async surface across every workload — T2.1, real `HttpClient`, Spectre's `Status`/`Progress` animations, Ctrl+C cancellation mid-operation, etc. It's the path the Mono-WASM team themselves built for.

Pursue **Option A** if Carbide is intended as a plain ESM library embeddable in arbitrary third-party pages (`<script type="module" src="https://.../carbide.js">`). The restriction is severe but the ergonomics on the host side are unchanged from M1/M2.

**Do not** pursue Option C or D without a strong reason — both ship a non-standard runtime or a non-standard C# programming model, and both would need multi-year-scale maintenance to track upstream changes.

**Do not** quietly ship **Option E**. T2 and T3 advertise semantics that Option E doesn't deliver; silently regressing them confuses users and contributors.

## 8. What lives in the current branch as of this report

These changes were made during T2.1 investigation and should be kept or reverted as part of the chosen option.

### Keep (correctness improvements independent of T2.1)

- `packages/core/src/Services/ProjectCompiler.cs:687` — second `SetSynchronizationContext(CarbideSyncContext.Instance)` right before `reflectedEntry.Invoke`. Defensive against any intervening `ConfigureAwait(false)` clearing SC; harmless if SC is already installed.
- `packages/core/src/Services/ProjectCompiler.cs:718-735` — `await userTask` without `ConfigureAwait(false)`. Keeps the continuation on `CarbideSyncContext` instead of dispatching through `TaskScheduler.Default`.

### Revert or keep-as-an-island (not a fix, but also not a regression)

- `packages/core/src/Terminal/CarbideTerminalInterop.cs:53-64` — `DelayAsync` JSImport replaced by `DelayCallback([JSMarshalAs<JSType.Function>] Action)`. Does not fix T2.1 but also doesn't regress anything; cleaner interop pattern.
- `packages/core/src/Terminal/CarbideConsole.cs:316-345` — `DelayAsync` rewired to use `DelayCallback`. Same as above.
- `packages/core/src/ts/terminal/bridge.ts:63-72` — `delay` renamed to `delayCallback`. Paired with the C# change above.

### Already done

- The earlier `docs/planning/milestones/carbide-T21-detailed-plan__2026-04-20__16-30-00-000000.md` (whose root-cause section was wrong) has been deleted; this report replaces it.
- The in-repo `examples/carbide-gh/` directory has been moved to [`docs/reports/artifacts/carbide-gh-T21-artifact/`](artifacts/carbide-gh-T21-artifact/README.md) and labeled as a non-working reference artifact accompanying this report.

## 9. Open investigation leads for whoever picks this up

- Attach a Chromium debugger to the browser running Carbide. Break on `throw` inside the generated state machine. Walk the frames upward from the PNS throw site to find the exact BCL frame. My best guess: it's inside `ThreadPool.UnsafeQueueUserWorkItemInternal` or `ManualResetEventSlim.Wait` during `AsyncStateMachineBox`'s first-suspension allocation, but I did not confirm this in-runtime.
- Compare Carbide's single-threaded boot to a minimal repro project that uses `dotnet new wasmbrowser` + a trivial `await Task.Delay(50)`. If the minimal project **also** trips, confirm upstream Mono-WASM's single-threaded mode legitimately does not support real suspension, and file a runtime-level bug referencing this report.
- Revisit `FEATURE_WASM_MANAGED_THREADS` + the COOP/COEP constraint with Carbide's actual embedding targets. If all real consumers are under Vladimir's control, Option B becomes dramatically more attractive.
