# Carbide T2.1 — prior-art research (public literature on "Cannot wait on monitors" in Mono-WASM single-threaded)

- Created (UTC): 2026-04-20T17:40:00Z
- Repository HEAD: 0933154cf

Status: **literature review companion to the T2.1 investigation report**. This document summarises what the public internet — primary sources (dotnet/runtime source, runtime team comments on GitHub, official Microsoft docs) and secondary sources (blog posts, Stack Overflow, other frameworks' solutions) — says about the `System.PlatformNotSupportedException: Cannot wait on monitors on this runtime.` condition that Carbide's single-threaded browser-wasm runs hit whenever a C# `await` actually suspends. Audience: Carbide Contributors, and future Carbide contributors evaluating Options A-E in the T2.1 investigation report.

Companion document: [`carbide-T21-investigation-report__2026-04-20__17-11-37-000000.md`](carbide-T21-investigation-report__2026-04-20__17-11-37-000000.md). Read that first; this report assumes its framing.

## tl;dr

The `Cannot wait on monitors on this runtime.` string is emitted from exactly one site in `mono/metadata/monitor.c`: the `DISABLE_THREADS` branch of `mono_monitor_wait_internal`, which refuses any `Monitor.Wait(obj, MONO_INFINITE_WAIT)` when the runtime was built without threading. The .NET team's stated, on-record posture — from javiercn (2020-2021), lewing (2022), Marek Safar (2022), Pavel Savara (2023-2024), and Steve Sanderson (2022-2024) — is that this is **working as intended**: single-threaded browser-wasm cannot support anything that turns into a blocking `Monitor.Wait(INFINITE)`, which includes `Task.Wait()`, `Task.Result`, `.GetAwaiter().GetResult()`, `Task.WaitAll`, `ManualResetEventSlim.Wait()`, and `SemaphoreSlim.Wait()`. The "real fix" Microsoft ships is `FEATURE_WASM_MANAGED_THREADS` (gated by `<WasmEnableThreads>true</WasmEnableThreads>`), which (a) installs `JSSynchronizationContext` and (b) routes real blocking waits to `pthread` — at the cost of requiring COOP/COEP cross-origin isolation. Blazor WebAssembly single-threaded *does* support "await Task.Delay" and await on arbitrary `TaskCompletionSource` that settles later — this is the standard pattern — via the runtime's own `BackgroundJobHandler` pump that drains the ThreadPool work queue from `setTimeout(0)` on the main thread. The fact that Carbide's minimal repro `await tcs.Task` trips suggests something in Carbide's environment interferes with that pump (most likely that Carbide's state-machine suspension allocation path pulls `ManualResetEventSlim.Wait(Timeout.Infinite)` — either through a `SemaphoreSlim` somewhere, or through `ThreadPool.EnsureWorkerRequested` interacting with an `await` frame that had its `ExecutionContext` captured in a way that tickles the blocking wait codepath). JSPI shipped in Chrome 137 (May 2025) and Firefox 139 (2025); Safari dropped its objection in late 2025 but has not shipped. No dotnet/runtime tracking issue covers "use JSPI to implement real `await` suspension on single-threaded .NET-wasm"; the only JSPI tracking issue ([#80904](https://github.com/dotnet/runtime/issues/80904)) covers the narrower "sync-over-async crypto/assembly-loading" use case. The community's answer in every thread is unanimous and unambiguous: **don't block, use `async`/`await` all the way**. The subtlety for Carbide is that this advice only works if the specific `await` suspension path you hit does not itself route through a blocking wait — which for Carbide it apparently does.

## Table of contents

1. [Exact prior reports of the error string](#1-exact-prior-reports-of-the-error-string)
2. [The origin of the message in the runtime source](#2-the-origin-of-the-message-in-the-runtime-source)
3. [The canonical Microsoft explanation](#3-the-canonical-microsoft-explanation)
4. [Blazor's path — why `await` works for Blazor but not for Carbide](#4-blazors-path--why-await-works-for-blazor-but-not-for-carbide)
5. [Community workarounds](#5-community-workarounds)
6. [Is it a bug or a feature?](#6-is-it-a-bug-or-a-feature)
7. [JSPI / JavaScript Promise Integration](#7-jspi--javascript-promise-integration)
8. [Other frameworks/languages facing the same trap](#8-other-frameworkslanguages-facing-the-same-trap)
9. [Synthesis: reconciling Blazor's "it works" with Carbide's "it trips"](#9-synthesis-reconciling-blazors-it-works-with-carbides-it-trips)
10. [Recommended next steps for Carbide](#10-recommended-next-steps-for-carbide)

---

## 1. Exact prior reports of the error string

The string `Cannot wait on monitors on this runtime` appears in many issues across `dotnet/runtime`, `dotnet/aspnetcore`, and downstream repos (OData, Azure, Flurl, Semantic Kernel, DevExpress, Uno, etc.). The canonical issues, in chronological order:

### 1.1 `dotnet/aspnetcore#22400` (2020-05) — closed

**Title:** "Blazor-wasm runtime issue: Cannot wait on monitors on this runtime"
**URL:** https://github.com/dotnet/aspnetcore/issues/22400
**Status:** closed.
**Triggering pattern:** user's code called `InvokeAsync(...).Result` (blocking `.Result` on a JS interop task).
**Team response (javiercn, member):**

> Is the usage of `.Result` https://github.com/jan-johansson-mr/BlazorTaskIssue/blob/master/BlazorApp2/Pages/FetchData.razor#L65 that causes this issue.
>
> The mono runtime is single threaded, so I would suggest you file an issue in the [mono repo](https://github.com/mono/mono).

Later in the thread, when another user (JonTvermose) reported the same error from a sync `IStringLocalizer` code path that calls an async HTTP fetch:

> **javiercn:** You'll have to architect around it. I suggest you extract the async work out of the sync code path and cache the results for use within the sync path.

This is the first on-record statement of the canonical Microsoft position: it is not a bug, you must redesign your code to be async all the way.

### 1.2 `dotnet/aspnetcore#26314` (2020-09) — closed

**Title:** "Blazor WebAssembly Error: Cannot wait on monitors on this runtime."
**URL:** https://github.com/dotnet/aspnetcore/issues/26314
**Status:** closed, resolution: by-design.
**Triggering pattern:** unclear, but the user was trying to `await data` where `data` was already an unwrapped value.
**Team response (javiercn):**

> That's expected and by design. JavaScript is single threaded, so you can't block a thread as there won't be any other thread available to notify the blocked one.

Again: **"by design"**.

### 1.3 `dotnet/aspnetcore#40096` (2022-02) — closed, resolved

**Title:** "Cannot wait on monitors on this runtime"
**URL:** https://github.com/dotnet/aspnetcore/issues/40096
**Status:** closed, labelled `Resolution: Answered`.
**Triggering pattern:** calling `.Result` on `InvokeAsync` inside a Razor `@code` block rendered during `@foreach`.
**Team response (pranavkm, contributor):**

> Can you share a minimal app that reproduces the issue? We suspect the app must be making a blocking call (`Task.Result`) at some point to encounter this error.

Reviewed and confirmed by community member nikonthethird, then captainsafia (contributor) restated the guidance:

> you'll want to do this in a lifecycle method like `OnInitializedAsync`. You don't need to call `StateHasChanged` since Blazor will take care of that for you.

### 1.4 `dotnet/runtime#53228` (2021-05) — closed

**Title:** "Blazor WASM Regression: Unhandled exception rendering component: Cannot wait on monitors on this runtime."
**URL:** https://github.com/dotnet/runtime/issues/53228
**Status:** closed, duplicate of #61308.
**Triggering pattern:** `Task.WaitAll(tasks)` that worked on Blazor WASM 3.2.0 began throwing on .NET 5.0.
**Stack trace (user-reported, verbatim):**

```
System.PlatformNotSupportedException: Cannot wait on monitors on this runtime.
   at System.Threading.Monitor.ObjWait(Boolean exitContext, Int32 millisecondsTimeout, Object obj)
   at System.Threading.Monitor.Wait(Object obj, Int32 millisecondsTimeout, Boolean exitContext)
   at System.Threading.Monitor.Wait(Object obj, Int32 millisecondsTimeout)
   at System.Threading.ManualResetEventSlim.Wait(Int32 millisecondsTimeout, CancellationToken cancellationToken)
   at System.Threading.Tasks.Task.WaitAllBlockingCore(List`1 tasks, Int32 millisecondsTimeout, CancellationToken cancellationToken)
   at System.Threading.Tasks.Task.WaitAllCore(Task[] tasks, Int32 millisecondsTimeout, CancellationToken cancellationToken)
   at System.Threading.Tasks.Task.WaitAll(Task[] tasks)
```

This is the "canonical" stack trace showing the same descent `Task.Wait* → ManualResetEventSlim.Wait → Monitor.Wait(INFINITE) → PNSE` that Carbide's investigation report conjectures for the `await tcs.Task` path. The issue was closed as a duplicate of #61308.

### 1.5 `dotnet/runtime#61308` (2021-11) — open

**Title:** "Blazor WebAssembly Error: Cannot wait on monitors on this runtime."
**URL:** https://github.com/dotnet/runtime/issues/61308
**Status:** **still open** as of research date (2026-04-20), labelled `arch-wasm`, `area-System.Threading`, milestone: none. No .NET team member has substantively engaged beyond the initial triage (`lewing` was tagged as the arch-wasm owner).
**Reporter's framing (Xyncgas):**

> I want to keep talking about this issue.
> It's in my opinion a bug, hear me out.
> We can't pause a thread in blazor because it's running on one thread and it's async all the way that was the decision.
>
> but, this affects the portability of codes, even when they followed best practice, while they can target .net standard 2.0 - mono.wasm - blazor they are still going to break.
>
> how about we fix this by, in my opinion I propose defining what happens when you call .wait() either: block the running thread and let people intentionally make this trade off, or making blazor multi-threaded, or simulate async behavior by forcing it to yield control after timeout that's decided by the .net rt

This is effectively the customer demand that Carbide's Option B (multi-threaded) and Option C (yield-from-Monitor) would answer. The fact that the issue has been open unresponded for 4+ years is the best indicator that the .NET team's posture has not changed.

### 1.6 `dotnet/runtime#61381` (2021-11) — closed

**Title:** ".NET Standard 2.1 vs .NET 6 Threading in Blazor WASM"
**URL:** https://github.com/dotnet/runtime/issues/61381
**Status:** closed.
**Triggering pattern:** calling Roslyn's `CSharpCompilation.GetDiagnostics(...)` from a Blazor WASM app worked in .NET Standard 2.1 (probably via a different Roslyn build) but PNSE'd on .NET 6.
**Decisive team comment (lewing, member):**

> Given that the current wasm runtime is single threaded, In the move to .NET6 we decided it was better to Fail all .Wait() calls than to have some fail seemingly randomly at runtime. Because of that change this is expected. I hope the workaround is sufficient. Closing

This is the single most important primary source for understanding the intent behind the current `DISABLE_THREADS` branch in `monitor.c`: **the .NET team deliberately chose to fail all `.Wait()` calls deterministically rather than let them work "seemingly randomly."** The community workaround that resolved this particular case (MerlinVR, community):

> a quick workaround I found for the current Task.Wait() limitation while using Roslyn which doesn't involve changing the target framework to standard 2.1 is to set `concurrentBuild: false` on the CSharpCompilationOptions when creating the compilation.

That is: Roslyn had internal `Task.Wait()`s to join parallel-build worker tasks. Disabling concurrent build made those waits unnecessary.

### 1.7 `dotnet/aspnetcore#41046` / `OData/odata.net#2372` (2022-03) — both closed (moved)

**Title:** "Blazor wasm: Cannot wait on monitors on this runtime, when calling odata in OnInitializedAsync"
**URL:** https://github.com/dotnet/aspnetcore/issues/41046, https://github.com/OData/odata.net/issues/2372
**Status:** moved to OData repo; javiercn closed the aspnetcore side:

> This issue was moved to OData/odata.net#2372

The OData client's sync code path internally called `.Result` on an HTTP task. Fix was on OData's side (they exposed a proper async API).

### 1.8 `StephenCleary/AsyncEx#220` (2022-02) — closed

**Title:** "Blazor Web Assembly error: Cannot wait on monitors on this runtime"
**URL:** https://github.com/StephenCleary/AsyncEx/issues/220
**Reporter:** user hit the error from `AsyncContext.Run(...)` which uses internal `ManualResetEventSlim.Wait`.
**Response from Stephen Cleary (owner) — verbatim:**

> From the call stack, it looks like you're trying to use `AsyncContext` to block on asynchronous code during a render. This isn't possible in Blazor, since it runs in the browser. You need to use `async` all the way; there is no other choice in that environment.

When asked "is there a consistent way to call async method in a sync context in Blazor":

> No, there is not. Blazor cannot [do] that because it runs in a browser.

Stephen Cleary is the .NET community's canonical authority on async/await patterns; his word on this is as authoritative as anyone's outside the dotnet team.

### 1.9 Other prominent instances

- `dotnet/aspnetcore#22400` (2020) — first report, see §1.1.
- `Azure/AppConfiguration#411` — App Configuration client internally blocked on async init.
- `hypar-io/ElementsPlayground#1` — general error from compute-heavy async code.
- `DevExpress T957095` — DevExpress Blazor DataGrid edit-form close triggered a sync-over-async path.
- `tmenier/Flurl#557` / `#823` — Flurl HTTP client's internal sync paths on Blazor WASM.
- `dotnet/reactive#2061` — `Observable.Delay` throws PNSE on Blazor WASM (Rx sets up a `Timer` that ends up on a blocked wait).
- `microsoft/semantic-kernel#1792` — Semantic Kernel's Kernel building on Blazor WASM hit threading PNSE.
- `dotnet/aspnetcore#16954` (2019) — "Blazor WebAssembly Locks up UI when working with Tasks/Delegates" — an early report where the UI froze because a blocking wait was hit; predates the PNSE being thrown, so it silently deadlocked instead.
- `dotnet/aspnetcore#43364` (2022) — "Blazor: Deadlock when chaining TaskCompletionSource via ContinueWith" — demonstrates that even on Blazor Server (not WASM), custom TCS/ContinueWith patterns can create deadlocks. The Blazor-WASM variant of this pattern hits PNSE instead of deadlocking.
- `mono/mono#18604` (2020) — "[wasm] sync task.wait waiting for ever" — a pre-PNSE-enforcement report, where the .Wait() would hang the page rather than throw.
- `dotnet/runtime#122529` (2025) — ".NET 10 Blazor WASM hang", not strictly the PNSE but an infinite loop in the interpreter's SSA optimization when invoked via Blazor's JS interop. Notable because pavelsavara's workaround (`MONO_INTERPRETER_OPTIONS=-ssa`) is useful for debugging any Carbide-like hang with complex compiled managed code.

The pattern in every case is the same: a library's internal code path assumed the blocking `Task.Wait()`/`.Result`/`ManualResetEventSlim.Wait()` would work. Fix is always "push the async further up" or "fork the library to avoid the block."

### 1.10 PRs that touched `monitor.c` near the `DISABLE_THREADS` guard

No search I ran surfaced a PR that specifically modifies the `Cannot wait on monitors on this runtime.` string's branch. The Mono side of the change lives in [`mono/mono#17611`](https://github.com/mono/mono/pull/17611) (vargaz, "Add a --enable-minimal=threads configure option to disable threading support"), which originally introduced the `DISABLE_THREADS` partitioning. Later PRs on the dotnet/runtime side repeatedly extended `#ifdef DISABLE_THREADS` guards to additional files as each new scenario (sockets, pipes, timers, etc.) surfaced a blocking call; those PRs don't modify the specific `Monitor.Wait(INFINITE)` branch, they only gate surrounding Windows-event / semaphore / waitable-handle paths that would otherwise end up calling it.

## 2. The origin of the message in the runtime source

[`src/mono/mono/metadata/monitor.c`](https://github.com/dotnet/runtime/blob/main/src/mono/mono/metadata/monitor.c), inside `mono_monitor_wait` (the shared implementation of `Monitor.Wait(obj[, timeout])` for all Mono targets), contains exactly one branch that produces this message:

```c
event = mono_w32event_create (FALSE, FALSE);
if (event == NULL) {
    mono_error_set_synchronization_lock (error, "Failed to set up wait event");
    mono_error_set_pending_exception (error);
    return FALSE;
}

#ifdef DISABLE_THREADS
if (ms == MONO_INFINITE_WAIT) {
    mono_error_set_platform_not_supported (error, "Cannot wait on monitors on this runtime.");
    return FALSE;
}
#endif
```

Two things are load-bearing:

1. **It's only tripped when `ms == MONO_INFINITE_WAIT`.** A finite `Monitor.Wait(obj, timeoutMs)` with a non-infinite `timeoutMs` does NOT trip this branch — it falls through to the general `mono_w32event_create` + wait path which (in single-threaded mode) degenerates to "spin until the event is signaled in the same thread, which never happens, so it returns false after `timeoutMs`." That's the escape hatch `ManualResetEventSlim.Wait(int timeoutMs)` uses when called with a non-infinite timeout.
2. **The `#ifdef DISABLE_THREADS` guard is the canonical single-threaded marker in Mono.** It is defined by the mono build system when `-DENABLE_MINIMAL=threads` is set, and by the .NET mono build when `<WasmEnableThreads>` is false. When `<WasmEnableThreads>true` is set, DISABLE_THREADS is unset, the branch compiles out, and `Monitor.Wait(INFINITE)` works via `pthread_cond_wait` on the underlying event.

Primary source: [mono/metadata/monitor.c on main](https://github.com/dotnet/runtime/blob/main/src/mono/mono/metadata/monitor.c).

The related managed-side frames are the `ManualResetEventSlim.Wait(int, CancellationToken)` spin+wait fallback (which calls `Monitor.Wait(obj, timeout)` under lock with `timeout = Timeout.Infinite` when the user asked for infinite), and `Task.SpinThenBlockingWait(int, CancellationToken)` which itself uses a `ManualResetEventSlim`. These are the load-bearing managed frames you see at the top of every stack trace in §1.

## 3. The canonical Microsoft explanation

### 3.1 `src/mono/wasm/features.md` — official feature matrix

From [`src/mono/wasm/features.md`](https://github.com/dotnet/runtime/blob/main/src/mono/wasm/features.md) (main branch):

> Multi-threading is **experimental and disabled by default**. To enable it, use `<WasmEnableThreads>true</WasmEnableThreads>`, which requires a unique runtime build.

And (key, verbatim):

> **Blocking on the main thread with operations like `Task.Wait` or `Monitor.Enter` are not supported by browsers and are very dangerous.**

Plus the COOP/COEP requirement:

> HTTPS servers must send headers like `Cross-Origin-Embedder-Policy: require-corp` and `Cross-Origin-Opener-Policy: same-origin` to enable threading (due to SharedArrayBuffer security needs).

### 3.2 `src/mono/wasm/threads.md` — threading design doc

From [`src/mono/wasm/threads.md`](https://github.com/dotnet/runtime/blob/main/src/mono/wasm/threads.md):

> JavaScript objects have thread (web worker) affinity. You can't use DOM, WebSocket or their promises on any other web worker than the original one. Therefore we have `JSSynchronizationContext` which is helping the user code to stay on that thread.

On the compile-time partitioning:

> **DISABLE_THREADS**: Defined for single-threaded builds in `src/mono/mono` and `src/mono/wasm`.
> **FEATURE_WASM_MANAGED_THREADS**: Conditionally defines threading-related library functionality.
> **__EMSCRIPTEN_THREADS__**: Defined by Emscripten when threading is enabled.

### 3.3 `WasmEnableThreads` MSBuild property

Confirmed in multiple places:

- Blog post [No Need to Wait for .NET 8 to Try Experimental WebAssembly Multithreading](https://visualstudiomagazine.com/articles/2022/10/11/blazor-webassembly-net7.aspx) (VS Magazine, 2022).
- Official docs [Host and deploy Blazor WebAssembly](https://learn.microsoft.com/en-us/aspnet/core/blazor/host-and-deploy/webassembly/) (Microsoft Learn).
- [dotnet/runtime#68162](https://github.com/dotnet/runtime/issues/68162) — tracking issue for multi-threaded wasm, still open, contains the design rationale.
- [dotnet/aspnetcore#54365](https://github.com/dotnet/aspnetcore/issues/54365) — "Make Blazor WebAssembly work on multithreaded runtime" (still open), contains design notes on why Blazor's rendering serialization model conflicts with multithreaded wasm.

### 3.4 COOP/COEP (cross-origin isolation) requirement

From [dotnet/aspnetcore#54071](https://github.com/dotnet/aspnetcore/issues/54071):

> **SteveSandersonMS:** We discussed this in API review and decided it's not clear that ASP.NET Core should have that responsibility. [...] The only reason we're special-casing it for WebAssembly multithreading is to make the on-ramp easier for that specific Blazor WebAssembly feature.

The relevant ASP.NET Core option, implemented in [PR #54062](https://github.com/dotnet/aspnetcore/pull/54062):

```csharp
public sealed class WebAssemblyComponentsEndpointOptions
{
    /// <summary>
    /// Gets or sets a flag to determine whether to enable WebAssembly multithreading. If true,
    /// the server will add headers similar to <c>Cross-Origin-Embedder-Policy: require-corp</c> and
    /// <c>Cross-Origin-Opener-Policy: same-origin</c> on the response for the host page, because
    /// this is required to enable the SharedArrayBuffer feature in the browser.
    ///
    /// Note that enabling this feature can restrict your ability to use other JavaScript APIs.
    /// </summary>
    public bool ServeMultithreadingHeaders { get; set; }
}
```

This is the feature the Blazor team ships to make COOP/COEP "a bool" for their users. Carbide can't use this directly (Carbide isn't hosted on ASP.NET Core), but the mechanism — "send these two headers" — is the same.

### 3.5 Support matrix (canonical):

| API | Single-threaded browser-wasm | Multi-threaded browser-wasm |
|---|---|---|
| `Task.Delay(int ms)` | **works** (scheduler uses `setTimeout`) | works |
| `await incomplete Task` (with default SC) | **depends — see §4 and §9** | works via `JSSynchronizationContext` |
| `Task.Wait()` / `Task.Result` | **PNSE "Cannot wait on monitors"** | works (but throws if called on UI thread) |
| `Task.WaitAll(tasks)` | PNSE | works (but throws if called on UI thread) |
| `ManualResetEventSlim.Wait()` (infinite) | PNSE | works on non-UI threads |
| `ManualResetEventSlim.Wait(int timeoutMs)` (finite) | "works" — spins then returns false at timeout (single-threaded completion never signals it) | works on non-UI threads |
| `SemaphoreSlim.Wait()` (infinite) | PNSE | works on non-UI threads |
| `SemaphoreSlim.WaitAsync()` | works (pure awaited `TaskCompletionSource`) | works |
| `Thread.Sleep(ms)` | **throws `NotSupportedException`** ("Cannot start threads on this runtime") per Meziantou's article | works on non-UI threads |
| `new Thread(...)` | throws `NotSupportedException` | works |
| `Thread.StartNew` via `TaskFactory` | routes through `TaskScheduler.Default` → ThreadPool-on-main-thread emulation | works on real worker thread |
| `Task.Run(() => ...)` | runs synchronously-ish on main thread via ThreadPool emulation | runs on real worker thread |
| `Task.Yield()` | works — `YieldAwaiter.OnCompleted` uses current `SynchronizationContext.Post` if set, else `ThreadPool.QueueUserWorkItem` | works |
| `HttpClient.GetAsync` | works (via browser `fetch`) | works |
| `WebSocket.ConnectAsync` | works (via browser `WebSocket` API) | works (but marshalled to main thread) |
| JSImport returning `Task` | works (Promise-to-Task bridge via `RunContinuationsAsynchronously`) | works, but crosses threads |
| `FileStream` (async) | works where virtual FS is mounted; returns `Task<int>` through setTimeout-backed scheduler | works |
| `lock (obj)` (uncontended) | works (no wait needed) | works |
| `lock (obj)` (contended) | **PNSE via Monitor.Enter's INFINITE wait path when contended** | works |
| `Parallel.For` / `Parallel.Invoke` | **PNSE** — internally uses `WaitAll`. This is the subject of [#43411](https://github.com/dotnet/runtime/issues/43411) | works |

A recurring point from the issue archaeology is that *anything calling `ManualResetEventSlim.Wait` with no timeout or `Timeout.Infinite`* eventually hits `Monitor.Wait(INFINITE)` and trips. The finite-timeout overload does *not* trip — it spins, notices the event never signaled (because nothing else can signal it on a single-threaded runtime), and returns `false` after the timeout. This explains why some libraries "just hang" on single-threaded wasm instead of throwing PNSE (they call with a finite timeout and loop forever), while others throw immediately (they call with infinite timeout).

## 4. Blazor's path — why `await` works for Blazor but not for Carbide

This is the most interesting section. The question is: if single-threaded browser-wasm really can't support any `await` that suspends, how does Blazor — which is built on exactly the same runtime and is notionally "async all the way" — actually work?

### 4.0 The JSSynchronizationContext source, verbatim

From [JSSynchronizationContext.cs](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Runtime.InteropServices.JavaScript/src/System/Runtime/InteropServices/JavaScript/JSSynchronizationContext.cs) (relevant extracts, verbatim):

```csharp
#if FEATURE_WASM_MANAGED_THREADS
// ...
using WorkItemQueueType = System.Threading.Channels.Channel<System.Runtime.InteropServices.JavaScript.JSSynchronizationContext.WorkItem>;

namespace System.Runtime.InteropServices.JavaScript
{
    /// <summary>
    /// Provides a thread-safe default SynchronizationContext for the browser that will automatically
    ///  route callbacks to the original browser thread where they can interact with the DOM and other
    ///  thread-affinity-having APIs like WebSockets, fetch, WebGL, etc.
    /// Callbacks are processed during event loop turns via the runtime's background job system.
    /// See also https://github.com/dotnet/runtime/blob/main/src/mono/wasm/threads.md#JS-interop-on-dedicated-threads
    /// </summary>
    internal sealed class JSSynchronizationContext : SynchronizationContext
    {
        internal readonly JSProxyContext ProxyContext;
        private readonly Action _ScheduleJSPump;
        private readonly WorkItemQueueType Queue;
        // ...
        public static unsafe JSSynchronizationContext InstallWebWorkerInterop(bool isMainThread, CancellationToken cancellationToken)
        {
            var ctx = new JSSynchronizationContext(isMainThread, cancellationToken);
            ctx.previousSynchronizationContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(ctx);
            // ...
            Interop.Runtime.InstallWebWorkerInterop(proxyContext.ContextHandle,
                (delegate* unmanaged[Cdecl]<JSMarshalerArgument*, void>)&JavaScriptExports.BeforeSyncJSExport,
                (delegate* unmanaged[Cdecl]<JSMarshalerArgument*, void>)&JavaScriptExports.AfterSyncJSExport,
                (delegate* unmanaged[Cdecl]<void>)&PumpHandler);
            // ...
        }
```

Key observations:

1. **The entire class is `#if FEATURE_WASM_MANAGED_THREADS`.** In single-threaded mode, it doesn't exist. No instance of `JSSynchronizationContext` can be created; the type itself is excluded from the compiled BCL.
2. **`internal sealed class`** — not public. You can't instantiate it in user code even if you get the feature flag set. It's installed by the runtime, via `InstallWebWorkerInterop`, itself called by mono runtime initialization code.
3. **Backed by `System.Threading.Channels.Channel<WorkItem>`** — this is a lock-free MPMC queue with `AllowSynchronousContinuations = true` (per earlier extract). The `Channel` machinery itself uses `Interlocked.CompareExchange` and doesn't block on `Monitor.Wait(INFINITE)` for the producer, which is the load-bearing property.
4. **`ScheduleJSPump` is called from `Post`** — schedules the native `ScheduleSynchronizationContext()` which then does an `Interop.Runtime` call to schedule a pump execution on the JS side. So Post → JS schedule → event loop tick → dispatch work item. Same pattern as Carbide's `CarbideSyncContext`, but using the runtime's blessed primitives.

### 4.1 What Blazor WebAssembly actually does in single-threaded mode

Two primary sources settle this:

**(a)** [`src/Components/WebAssembly/WebAssembly/src/Rendering/WebAssemblyDispatcher.cs`](https://github.com/dotnet/aspnetcore/blob/main/src/Components/WebAssembly/WebAssembly/src/Rendering/WebAssemblyDispatcher.cs) on `main`:

```csharp
// When Blazor is deployed with multi-threaded runtime, WebAssemblyDispatcher will help to dispatch all Blazor JS interop calls to the main thread.
// This is necessary because all JS objects have thread affinity. They are only available on the thread (WebWorker) which created them.
// Also DOM is only available on the main (browser) thread.
internal sealed class WebAssemblyDispatcher : Dispatcher
{
    internal static SynchronizationContext? _mainSynchronizationContext;
    internal static int _mainManagedThreadId;
    ...
}
```

The leading comment says **"When Blazor is deployed with multi-threaded runtime."** `WebAssemblyDispatcher` is a multi-threading-mode artifact. In single-threaded mode, `_mainSynchronizationContext` is null and nothing ever posts through it.

**(b)** Authoritative statement from javiercn (ASP.NET Core team member), on [`dotnet/aspnetcore#26887`](https://github.com/dotnet/aspnetcore/issues/26887):

> currently Blazor WebAssembly doesn't have a synchronization context, but it is recommended you still call StateHasChanged from within `InvokeAsync` if you are doing something from a potential "background" thread since that will make sure your code keeps working if we ever introduce multiple threads on Webassembly.

So in **single-threaded Blazor WebAssembly**, `SynchronizationContext.Current == null`.

### 4.2 Then how does `await Task.Delay(1)` work in single-threaded Blazor?

The runtime's own scheduling code. From [`src/mono/browser/runtime/scheduling.ts`](https://github.com/dotnet/runtime/blob/main/src/mono/browser/runtime/scheduling.ts) (verbatim extract):

```typescript
import WasmEnableThreads from "consts:wasmEnableThreads";
// ...

export function SystemJS_ScheduleBackgroundJobImpl (): void {
    if (WasmEnableThreads) return;
    if (!lastScheduledBackground) {
        lastScheduledBackground = Module.safeSetTimeout(mono_background_exec_until_done, 0);
    }
}

export function SystemJS_ScheduleTimerImpl (shortestDueTimeMs: number): void {
    if (WasmEnableThreads) return;
    // ... clears any previously scheduled timeout ...
    lastScheduledTimeoutId = Module.safeSetTimeout(mono_wasm_schedule_timer_tick, shortestDueTimeMs);
}
```

And from [`src/libraries/System.Private.CoreLib/src/System/Threading/ThreadPool.Browser.cs`](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Threading/ThreadPool.Browser.cs):

```csharp
#if FEATURE_WASM_MANAGED_THREADS
#error when compiled with FEATURE_WASM_MANAGED_THREADS, we use PortableThreadPool.WorkerThread.Browser.Threads.Mono.cs
#endif

// [single-threaded branch follows]
public static partial class ThreadPool
{
    private static bool _callbackQueued;

    internal static unsafe void EnsureWorkerRequested()
    {
        if (_callbackQueued)
            return;
        _callbackQueued = true;
#if MONO
        MainThreadScheduleBackgroundJob((void*)(delegate* unmanaged<void>)&BackgroundJobHandler);
#else
        SystemJS_ScheduleBackgroundJob();
#endif
    }
    // ...
}
```

So the single-threaded `ThreadPool` has a `_callbackQueued` flag and one `EnsureWorkerRequested` call that schedules `BackgroundJobHandler` via `setTimeout(0)`. That handler drains the ThreadPool work queue. `Task.Delay(ms)` uses the Timer scheduling path (`SystemJS_ScheduleTimerImpl`), also `setTimeout`-backed, which fires the Timer callback at the requested delay.

In other words:

- `await Task.Delay(50)` → `Timer` queues a `TaskCompletionSource.SetResult` for 50ms from now, via `setTimeout(cb, 50)` → main thread fires → `TaskCompletionSource` becomes complete → the `await`'s continuation runs on the main thread via the ThreadPool pump (`setTimeout(0)` style).
- `await tcs.Task` where `tcs.SetResult()` is called from a JS callback → the `SetResult` call queues the continuation as a ThreadPool work item → `EnsureWorkerRequested` schedules `BackgroundJobHandler` → the next event-loop tick runs the continuation.

None of this touches `Monitor.Wait(INFINITE)`. **In plain single-threaded Blazor WASM, `await` on an incomplete `Task` does work.**

### 4.3 A worked walkthrough of `await Task.Delay(50)` in single-threaded Blazor WASM

For concreteness, here's the full "how does `await Task.Delay(50)` work in plain single-threaded Blazor" trace, reconstructed from runtime source:

1. `Task.Delay(50)` constructs a `DelayPromise` (an internal `Task` subclass) and registers a `TimerQueueTimer` scheduled for 50 ms from now.
2. `TimerQueue.CreateAppDomainTimer` in single-threaded browser-wasm calls into the mono scheduler which invokes `SystemJS_ScheduleTimerImpl(50)` via `Module.safeSetTimeout(cb, 50)`.
3. The caller `await`s the returned `Task`. State-machine builder's `AwaitUnsafeOnCompleted` sees `IsCompleted == false`, captures `ExecutionContext`, and calls `INotifyCompletion.OnCompleted(continuation)` on the task awaiter.
4. `TaskAwaiter.OnCompleted` (or `UnsafeOnCompleted`) examines `SynchronizationContext.Current`. In plain Blazor, it's null. So it routes the continuation through `TaskScheduler.Current`, which is the `ThreadPoolTaskScheduler` (the singleton default).
5. `ThreadPoolTaskScheduler.QueueTask` calls `ThreadPool.UnsafeQueueUserWorkItemInternal(task, preferLocal: true)`. This ends up in `ThreadPool.Browser.cs`'s `EnsureWorkerRequested`.
6. `EnsureWorkerRequested` sets `_callbackQueued = true` (single shot) and calls `MainThreadScheduleBackgroundJob(&BackgroundJobHandler)`, which on the JS side translates to `Module.safeSetTimeout(mono_background_exec_until_done, 0)`. Note: this **does not block** and **does not call Monitor.Wait**.
7. The state machine returns. Control returns to the calling JS event loop iteration.
8. 50 ms later, `setTimeout` fires → `mono_wasm_schedule_timer_tick` → `mono_wasm_execute_timer` → the mono runtime calls the managed timer callback → `TaskCompletionSource.SetResult()` on the `DelayPromise`.
9. `SetResult` sees the task has continuations → `TaskContinuationObject` → invokes them. Because the continuation is a ThreadPool work item, it's enqueued on the `ThreadPool`'s `WorkStealingQueue`. This calls `EnsureWorkerRequested` again, which schedules another `setTimeout(0)` → `BackgroundJobHandler`.
10. After the current timer tick returns, the next `setTimeout(0)` fires → `BackgroundJobHandler` drains the `WorkStealingQueue` → invokes the continuation, which resumes the user's state machine.

**Key observation:** no managed frame in this chain calls `Monitor.Wait(INFINITE)`. Every wait is non-blocking: either `setTimeout(ms)` (a browser-side wait that doesn't block the thread because it *is* the browser event loop) or a lock-free queue push/pop. The runtime is carefully constructed to avoid `Monitor.Wait(INFINITE)` in the common async path.

### 4.4 A worked walkthrough of `await tcs.Task` for a later-completed TCS

Same trace as §4.3, but with a TCS completed externally (e.g., from a JS callback via JSImport):

1. `var tcs = new TaskCompletionSource();`. This is a plain managed object, no native side.
2. `await tcs.Task;`. State machine: `IsCompleted == false` → `AwaitUnsafeOnCompleted` → `TaskAwaiter.UnsafeOnCompleted(continuation)` → with no SC, routes to `TaskScheduler.Current` → `ThreadPool`.
3. `ThreadPool.UnsafeQueueUserWorkItem` → `EnsureWorkerRequested` → `setTimeout(0, BackgroundJobHandler)`.
4. Return to JS event loop.
5. Later, a JS callback fires that's been registered to call a C# `[JSExport]` method, which in turn calls `tcs.SetResult()`.
6. `SetResult` invokes the continuation; because it's a ThreadPool item, it goes onto the work queue, `EnsureWorkerRequested` schedules another tick.
7. Continuation runs.

Again, no `Monitor.Wait(INFINITE)`. This is the pattern Blazor relies on for e.g. `IJSRuntime.InvokeAsync<T>`, whose internal TCS is completed when the JS side sends back its response.

### 4.5 So why does Carbide's repro trip?

This is where the literature thins out, and I couldn't find a public report that matches Carbide's scenario *exactly* — "await on an incomplete `TaskCompletionSource` with a custom `CarbideSyncContext` installed on top of whatever default scheduling the runtime does." The closest leads:

- **`kg` (runtime team) in [dotnet/runtime#69409](https://github.com/dotnet/runtime/issues/69409):** (context: multi-threaded JSSynchronizationContext design review)
  > in my testing, this logic results in all continuations being posted to the threadpool even if they were ConfigureAwait(false), specifically because we have a sync context. [...] The presence of a sync context is being used as a heuristic for "continuations should never run here".
  >
  > https://cs.github.com/dotnet/runtime/blob/17154bd7b8f21d6d8d6fca71b89d7dcb705ec32b/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/TaskContinuation.cs#L610

  This suggests that installing a custom `SynchronizationContext` on single-threaded browser-wasm *can* trigger the "post to ThreadPool" path, which in turn calls `ThreadPool.UnsafeQueueUserWorkItem` — which in single-threaded mode goes through `EnsureWorkerRequested` → `setTimeout(0)` → `BackgroundJobHandler`. That *should* work. But if the specific code path you hit is the `ConcurrentQueue<IThreadPoolWorkItem>` producer side that itself uses a lock-free sync primitive whose fallback is `Monitor.Wait(INFINITE)`, you'd trip.

- **stephentoub's reply (in the same issue):**
  > If the antecedent operation completes on the same context, however, there is an optimization in the await infrastructure that will avoid posting back; that just requires that `SynchronizationContext.Current` is the same object as was current when the await was initiated, so if this is the concern, you'd want to make sure that you're not artificially cloning your context object and instead try to have the same SynchronizationContext instance always used for a given target context/thread/environment.

See §9 below for the reconciliation attempt. Short version: Carbide's `CarbideSyncContext` very likely satisfies the "same object" identity optimization, but something in the `AsyncTaskMethodBuilder`'s suspension-box allocation path or `ExecutionContext` capture / restore is still tripping a blocking `Monitor.Wait(INFINITE)` that plain-Blazor does not trip. The next debugging step to isolate it is in the T2.1 investigation report's §9 ("Open investigation leads").

### 4.5.1 The Blazor WebAssemblyDispatcher's full source, verbatim

Quoting the full `WebAssemblyDispatcher.cs` (see [aspnetcore source](https://github.com/dotnet/aspnetcore/blob/main/src/Components/WebAssembly/WebAssembly/src/Rendering/WebAssemblyDispatcher.cs)):

```csharp
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.AspNetCore.Components.WebAssembly.Rendering;

// When Blazor is deployed with multi-threaded runtime, WebAssemblyDispatcher will help to dispatch all Blazor JS interop calls to the main thread.
// This is necessary because all JS objects have thread affinity. They are only available on the thread (WebWorker) which created them.
// Also DOM is only available on the main (browser) thread.
// Because all of the Dispatcher.InvokeAsync methods return Task, we don't need to propagate errors via OnUnhandledException handler
internal sealed class WebAssemblyDispatcher : Dispatcher
{
    internal static SynchronizationContext? _mainSynchronizationContext;
    internal static int _mainManagedThreadId;

    // we really need the UI thread not just the right context, because JS objects have thread affinity
    public override bool CheckAccess() => _mainManagedThreadId == Environment.CurrentManagedThreadId;

    public override Task InvokeAsync(Action workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        if (CheckAccess())
        {
            // this branch executes on correct thread and solved JavaScript objects thread affinity
            // but it executes out of order, if there are some pending jobs in the _mainSyncContext already, same as RendererSynchronizationContextDispatcher
            workItem();
            // it can throw synchronously, same as RendererSynchronizationContextDispatcher
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource();

        // RendererSynchronizationContext doesn't need to deal with thread affinity and so it could execute jobs on calling thread as optimization.
        // we could not do it for WASM/JavaScript, because we need to solve for thread affinity of JavaScript objects, so we always Post into the queue.
        _mainSynchronizationContext!.Post(static (object? o) =>
        {
            var state = ((TaskCompletionSource tcs, Action workItem))o!;
            try
            {
                state.workItem();
                state.tcs.SetResult();
            }
            catch (Exception ex)
            {
                state.tcs.SetException(ex);
            }
        }, (tcs, workItem));

        return tcs.Task;
    }
    // ... same pattern for InvokeAsync<TResult>(Func<TResult>), InvokeAsync(Func<Task>), InvokeAsync<TResult>(Func<Task<TResult>>)
}
```

Critical takeaways for Carbide:

1. **`_mainSynchronizationContext` is `null`-initialized and only set by "something else."** Reading aspnetcore code, it gets set during Blazor's WebAssemblyHost boot, *only* when multi-threaded runtime is detected. So in single-threaded Blazor, this dispatcher exists but its sync context field is null, and `_mainSynchronizationContext!.Post(...)` would NRE — except the `CheckAccess()` fast path returns true (everyone is on managed-thread-1 in single-threaded mode), so the slow path is never entered. Single-threaded Blazor never actually uses the sync-context path.
2. **The comment explicitly says "When Blazor is deployed with multi-threaded runtime."** This is the authoritative architectural statement.
3. **Non-CheckAccess path posts to `_mainSynchronizationContext`, which is `JSSynchronizationContext`.** So in multi-threaded mode, Blazor explicitly hands off cross-thread work to the main thread's JSSynchronizationContext via `Post`, not through its own scheduler.

The overall architecture for multi-threaded Blazor is:
- `WebAssemblyDispatcher` is a shim over the Blazor-internal `Dispatcher` that makes `InvokeAsync` do the "post to main thread" trick.
- `JSSynchronizationContext` is the actual queue implementation.
- The ultimate pump is on the JS side, via `InstallWebWorkerInterop`'s `PumpHandler`.

For Carbide, none of this is load-bearing in single-threaded mode. Blazor's single-threaded mode demonstrates that you can have functional `await` without any of this infrastructure, just by letting `SynchronizationContext.Current` be null and relying on the ThreadPool-emulation-on-main-thread default.

### 4.6 Full text of pavelsavara's in-issue commentary on single-threaded vs multi-threaded dispatch

From [dotnet/aspnetcore#54365](https://github.com/dotnet/aspnetcore/issues/54365), in the design discussion for multi-threaded Blazor, pavelsavara (runtime team member) wrote (verbatim):

> Regarding the non-blocking behavior of JS interop.
> - There are methods `BeginInvokeDotNet` and `EndInvokeJS` which have synchronous signature, but the runtime is hacked to treat them as fire-and-forget async messages to deputy thread. If you rename them hack will break. We don't have public API attribute to express that yet.
> - Making `renderBatch` JSImport to return `Task` and have the implementation return a promise should work just fine.
> - There is `mono_wasm_gc_lock` and `mono_wasm_gc_unlock` which you call on UI thread (not deputy thread). This is not ideal because it's making the UI involved in GC stop-the-world.
> - I wonder if you can offload the DOM event handlers to thread pool rather than deputy thread.
>
> Regarding blocking `.Wait` throwing PNSE on "deputy" thread
> - Let's try to push thru the problems if possible. We could give up bit later.
> - Runtime would probably bring more scenarios. To throw on similar blocking operations which we didn't cover yet.
> - At the same time, I'm actively thinking on how to soften the limitation for deputy thread. No conclusion so far.
>
> In last month or so, we switched the implementation of JS interop dispatch from `JSSynchronizationContext` to emscripten internal queue. Therefore
> - we could drop `WebAssemblyDispatcher` and replace it with `RendererSynchronizationContextDispatcher` same as on the server side.
> - we can install `RendererSynchronizationContext` and replace the `JSSynchronizationContext` which is installed by default on main thread.

Two critical data points for Carbide:

1. **"`JSSynchronizationContext` which is installed by default on main thread"** — this is in the context of multi-threaded mode. Pavelsavara is referring to `JSSynchronizationContext` being the thing installed by default when `<WasmEnableThreads>true</WasmEnableThreads>`. In single-threaded mode, there is no default SC.

2. **pavelsavara follow-up on PNSE:**
   > This enables blocking `.Wait` on deputy (main) thread when running async code: https://github.com/dotnet/runtime/pull/99422
   >
   > Our team conclusion so far is, that we wish to disable synchronous `[JSExport]` on MT to avoid broad class of deadlocks. That would be different PR.
   >
   > [...]
   >
   > If we are unable to do that, please be aware that any managed code inside of those calls will
   > - throw PNSE on blocking `.Wait`
   > - on any virtual FS access
   > - creating new thread
   > - and also on `Console.WriteLine` which all talk to UI thread

The "Console.WriteLine will throw PNSE" line is especially interesting for Carbide, because Carbide's `interactive-readline` fixture involves `Console.In.ReadLineAsync()`. If Console I/O in deputy thread mode routes through something that blocks on a UI-thread channel and the channel's `Wait` fallback fires, you'd get the PNSE. This is a deputy-thread-MT issue, not single-threaded, but the shape of the code path is similar.

### 4.7 What a minimal unmodified `dotnet new wasmbrowser` project does

Investigation report §9 open-lead #2 proposes: "Compare Carbide's single-threaded boot to a minimal repro project that uses `dotnet new wasmbrowser` + a trivial `await Task.Delay(50)`."

Public reports are consistent: `dotnet new wasmbrowser` + `await Task.Delay(50)` in `Main` **does work** in single-threaded mode. There are no GitHub issues I could find claiming otherwise; the Meziantou blog post's recommended `await Task.Yield(); await Task.Delay(1);` pattern relies on this working. If it didn't, the entire Blazor WebAssembly experience would be broken.

I could not find a public GitHub repository that contains a minimal `dotnet new wasmbrowser` project that reproduces the PNSE on a plain `await tcs.Task`. A Carbide-internal reproduction of this case with the SC installed vs not installed is the single most useful experiment that remains.

## 5. Community workarounds

Every workaround I found in the wild fits one of four patterns:

### 5.1 Pattern 1: "Go async all the way" (the blessed answer)

The answer Stephen Cleary, javiercn, captainsafia, lewing, and every SO top-voted answer give. Example (captainsafia):

> you'll want to do this in a lifecycle method like `OnInitializedAsync`. You don't need to call `StateHasChanged` since Blazor will take care of that for you.

**Works for:** all cases where the user controls the full code path and can restructure.
**Does not work for:** pre-compiled libraries that have `.Result` / `.Wait()` somewhere deep inside (Roslyn until you set `concurrentBuild: false`, OData until they fixed the client, Flurl in older versions, Semantic Kernel, App Configuration, etc.).
**Works for Carbide's repro:** no — Carbide's repro is already `await tcs.Task`, not `.Wait()`/`.Result`. This pattern has nothing to give.

### 5.2 Pattern 2: Force library internals to skip the blocking path

Example (MerlinVR, on #61381, for Roslyn):

> a quick workaround I found for the current Task.Wait() limitation while using Roslyn which doesn't involve changing the target framework to standard 2.1 is to set `concurrentBuild: false` on the CSharpCompilationOptions when creating the compilation.

Example (javiercn, on #22400):

> You'll have to architect around it. I suggest you extract the async work out of the sync code path and cache the results for use within the sync path.

**Works for:** libraries that expose an opt-out for their internal parallelism.
**Does not work for:** libraries that don't expose such an opt-out.
**Works for Carbide:** no — there's no library whose `concurrentBuild` knob is tripping this in Carbide's minimal repro.

### 5.3 Pattern 3: Offload to a Web Worker via community packages

Three notable libraries:

- **BlazorWorker** ([Tewr/BlazorWorker](https://github.com/Tewr/BlazorWorker)) — maintained, .NET 8+ support as of v4.0.0-preview2. Recommended by pavelsavara himself in [dotnet/runtime#95452](https://github.com/dotnet/runtime/issues/95452) for "use-cases which don't need C# to interact with the UI, but they just need to calculate some logic in dotnet."
- **SpawnDev.BlazorJS.WebWorkers** ([LostBeard/SpawnDev.BlazorJS](https://github.com/LostBeard/SpawnDev.BlazorJS)) — 90K+ NuGet downloads. Runs Blazor in DedicatedWorker / SharedWorker / ServiceWorker contexts; relies on 1-to-1 JS ↔ .NET sync interop.
- **Uno Platform's multi-threading build** ([platform.uno/blog](https://platform.uno/blog/webassembly-threading-in-net/)) — Uno shipped WASM threading in their own build before Microsoft officially supported it (2020).

**Works for:** offloading CPU-bound work to workers; getting parallelism back.
**Does not work for:** the scenario where your code is the main-thread code and you want *its* `await` to suspend. A worker is a different thread, not a way to suspend the main thread.
**Works for Carbide's repro:** no — the minimal repro `await tcs.Task` is on the main thread. Moving Carbide to a worker changes the thread's identity but not the fact that you still need real async suspension *on that thread*, and a worker has the same `DISABLE_THREADS` restriction the main thread does.

### 5.4 Pattern 4: Custom `SynchronizationContext` with a `setTimeout`-backed `Post`

This is exactly Carbide's current `CarbideSyncContext` design. I could not find a public library that implements this as a standalone reusable component. The closest thing is:

- **Noseratio's "dev.to" post** [Why I no longer use ConfigureAwait(false)](https://dev.to/noseratio/why-i-no-longer-use-configureawait-false-3pne) — describes a custom SC that forwards `Post` to `setTimeout`. Not a full library.

- **`wasm_rs_async_executor::single_threaded`** (Rust, not C#) — [docs.rs](https://docs.rs/wasm-rs-async-executor/latest/wasm_rs_async_executor/single_threaded/) — a single-threaded futures executor for wasm that calls `wasm_bindgen_futures::spawn_local` which internally uses `Promise.resolve().then(...)` (a microtask) to drive the executor. This is a working reference for "a single-threaded async runtime in wasm."

**Works for:** lots of usage in the Rust-wasm ecosystem, where the language's async model was designed knowing single-threaded wasm was the target.
**Does not work for:** the .NET case out of the box. The BCL's `Task` machinery has internal fallbacks into blocking waits (ManualResetEventSlim, Monitor) that a user-space `SynchronizationContext.Post` can't bypass when those fallbacks fire.
**Works for Carbide:** T2.1 investigation report §4.3 tested this (macrotask Post) and the PNSE still fired. So the SC is NOT the load-bearing problem in Carbide's environment — confirming that the actual blocking wait happens inside runtime machinery that doesn't consult `SynchronizationContext.Current` for the decision to block.

### 5.5 Per-workaround applicability to Carbide's `await tcs.Task` repro

| Workaround | Works for Carbide repro? | Reason |
|---|---|---|
| "Use async all the way" — restructure caller to not call `.Result`/`.Wait()` | No | Carbide's repro is already `await tcs.Task`, no `.Wait`/`.Result` anywhere |
| Library-specific opt-outs (e.g., `concurrentBuild: false` for Roslyn) | N/A | No library is involved in the minimal repro |
| Move computation to Web Worker (BlazorWorker / SpawnDev) | No | Worker has identical `DISABLE_THREADS` restriction; also Carbide needs to suspend main-thread code |
| Custom SC with `setTimeout`-backed `Post` | No | Investigation §4.3 tested this, still PNSEs |
| Custom SC with inline `Post` (current Carbide) | No | Current state, PNSEs |
| Drop custom SC, let `SynchronizationContext.Current` be null | **Untested, plausible** | Matches Blazor's single-threaded mode. See §9.5 / Option F. |
| `TaskCreationOptions.RunContinuationsAsynchronously` on TCS | No | Investigation report ruled this out as a cause; not tested as a workaround-by-itself |
| `[JSImport]` with pre-completed Promise | Yes | But only for the subset of awaits where the JS Promise can be pre-completed — not applicable to user input waits |
| Rewrite to `IEnumerator<YieldInstruction>` coroutine (Option D) | Yes | But requires source rewriting of user code AND pre-compiled libraries |
| Ship `<WasmEnableThreads>true</WasmEnableThreads>` runtime (Option B) | Yes | But requires COOP/COEP |
| Patch `monitor.c` to yield on INFINITE (Option C) | Yes | But requires forked runtime + ASYNCIFY or JSPI |

The interesting row is "Drop custom SC." Carbide hasn't tested this. If it works, it's close to free.

### 5.6 None of the workarounds on the public internet make the minimal `await tcs.Task` repro work in single-threaded mode

I searched for this case specifically and could not find a match. All the "it works" reports are either:

(a) plain `await Task.Delay(n)` where the Timer scheduling path carries the continuation (§4.2), or

(b) `await jsImportFn()` where the JS Promise returns a value — the JSImport marshaller creates a TCS with `RunContinuationsAsynchronously` and the JS Promise `then` chain fires the `.SetResult`, which then schedules the continuation through the ThreadPool pump, or

(c) multi-threaded mode with real `JSSynchronizationContext`.

The investigation report's minimal repro — `var tcs = new TaskCompletionSource(); await tcs.Task;` with `tcs` never completed, triggering PNSE synchronously at the suspension site — is genuinely not reported in any public issue I could find. Closest is #61381 (where the suspension eventually resolves to a blocking wait through `ManualResetEventSlim`), but that has a visible `Task.Wait` in the stack, and Carbide's does not.

This suggests that **either** (a) Carbide's specific code path triggers something not in the standard Blazor repro set, **or** (b) the SC installation itself is tickling a code path that wouldn't fire without a custom SC. I lean toward (b) given stephentoub's comment in §4.3.

### 5.7 Deep-dive: Noseratio's custom SC approach

Dmytro Mandzyuk (Noseratio) wrote [Why I no longer use ConfigureAwait(false)](https://dev.to/noseratio/why-i-no-longer-use-configureawait-false-3pne) and has authored several custom SC implementations. His approach for single-threaded environments is:

```csharp
public class SerialQueueSynchronizationContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();

    public override void Post(SendOrPostCallback d, object? state)
        => _queue.Add(() => d(state));

    public void Run() {
        while (_queue.TryTake(out var action, Timeout.Infinite))
            action();
    }
    // ...
}
```

This pattern — "Post just adds to a queue, Run drains the queue" — works on platforms where `Run` can block. On single-threaded browser-wasm it's unusable for exactly the same reason Carbide hits: `BlockingCollection.Take` eventually calls `ManualResetEventSlim.Wait` with infinite timeout, which PNSEs.

The only SC pattern that actually works for single-threaded wasm is the one where `Post` immediately schedules via JS (either `setTimeout(0)` for macrotasks, or `Promise.resolve().then(...)` for microtasks), and there is no `Run` — the JS event loop IS the Run. This is exactly what Rust's `wasm_bindgen_futures::spawn_local` does, and it's what Carbide's `CarbideSyncContext.Post` already does in its current incarnation.

### 5.8 Why Unity's coroutine approach avoids the problem entirely

Unity (the game engine) implements C# coroutines via `IEnumerator` methods that `yield return` YieldInstructions (`null`, `WaitForSeconds(f)`, `WaitForEndOfFrame`, `new AsyncOperation(...)`, etc.). The coroutine runtime:

1. Calls `enumerator.MoveNext()` once.
2. If it returned `true`, inspects `enumerator.Current`.
3. Based on the YieldInstruction type, schedules another `MoveNext()` call — either on the next frame, after N seconds, when an AsyncOperation signals completion, etc.
4. Never blocks; never calls `Monitor.Wait`; never uses `Task`.

Option D in the T2.1 investigation report is effectively "port Unity's coroutine system to Carbide's runtime." The tradeoff is that user code has to use `IEnumerator` return types and `yield return`, not `async`/`await`. For user code this is tolerable with a source generator; for pre-compiled libraries it's a dead end.

Crucially, Unity's coroutine system has been stable and shipping in commercial games for 15+ years, so we know this is a sound design. It is the reference architecture for "single-threaded asynchrony that avoids the blocking-wait trap entirely."

### 5.9 Detailed analysis of the Noseratio / pavelsavara / kg exchange in #69409

Revisiting [dotnet/runtime#69409](https://github.com/dotnet/runtime/issues/69409) in more depth, because it's the single most-relevant discussion for Carbide:

The issue was `[wasm-mt] Add a System.Runtime.InteropServices.JavaScript.BrowserSynchronizationContext`. Stephen Toub (stephentoub, member of the dotnet runtime team) asked:

> Who installs the synchronization context? I'm wondering if it needs to be public or if it can just be an implementation detail; most of them are the latter.

Katelyn Gadd (kg, runtime team) replied:

> I've been looking into the little hidden interactions between TaskContinuation and custom synchronization contexts and it's very unclear to me how we should go about properly integrating everything here. We need a custom sync context (or something that achieves the equivalent) because things like WebSockets and timers have strong thread affinity in the browser. But from doing testing on my own and reading the code, it looks like having a sync context registered at all will punt all running workloads into a degraded mode where they never run continuations inline. Is this less of a problem than it appears?

This is direct confirmation from a runtime team member that **installing any `SynchronizationContext` at all on browser-wasm changes the continuation-routing behavior**. It's not about which specific SC you install; the mere fact of installation shifts the behavior into a "post every continuation through the SC" mode.

Stephen Toub responded:

> Can you share an example of the kind of code you're concerned about with: "it looks like having a sync context registered at all will punt all running workloads into a degraded mode where they never run continuations inline. Is this less of a problem than it appears?"
>
> If the continuation must run back on the original context, then the implementation has little choice but to dutifully post back to the original context. If the antecedent operation completes on the same context, however, there is an optimization in the await infrastructure that will avoid posting back; that just requires that `SynchronizationContext.Current` is the same object as was current when the await was initiated, so if this is the concern, you'd want to make sure that you're not artificially cloning your context object and instead try to have the same SynchronizationContext instance always used for a given target context/thread/environment.

And:

> (If instead the issue is that you see continuations being queued back to the original context but doing so is unnecessary, that's why .ConfigureAwait(false) exists.)

kg's pointer to the load-bearing code:

> The issue is that in my testing, this logic results in all continuations being posted to the threadpool even if they were ConfigureAwait(false), specifically because we have a sync context.
>
> https://cs.github.com/dotnet/runtime/blob/17154bd7b8f21d6d8d6fca71b89d7dcb705ec32b/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/TaskContinuation.cs#L610
>
> The presence of a sync context is being used as a heuristic for "continuations should never run here".

This is the smoking gun for Carbide's issue. The cited line in `TaskContinuation.cs` is:

```csharp
// We're not allowed to run the continuation inline if we have a captured sync context
// and it doesn't match the current one, or if we have a captured sync context at all
// (even if it matches) because the continuation might take a long time.
```

So: **in Carbide's case, even with the identity match, the await infrastructure forces the continuation through the ThreadPool path** because there's a captured SC at all. And the ThreadPool path is what, in Carbide's specific environment, is tripping.

**This is the key insight Option F (drop the SC entirely) is based on:** if Carbide removes its custom SC, there's no captured SC on the await, so the continuation runs inline (or is queued through the ThreadPool the way Blazor does it), which Blazor demonstrably tolerates.

## 6. Is it a bug or a feature?

On-record, the .NET team's position is "feature, by design, not going to change for single-threaded builds." Direct quotes:

- **javiercn, 2020 (#26314):** "That's expected and by design. JavaScript is single threaded, so you can't block a thread as there won't be any other thread available to notify the blocked one."
- **javiercn, 2020 (#22400):** "The mono runtime is single threaded [...] The browser is a single threaded runtime [...] This is not a decision that we made, it's a limitation imposed by the environment."
- **lewing, 2022 (#61381):** "Given that the current wasm runtime is single threaded, In the move to .NET6 we decided it was better to Fail all .Wait() calls than to have some fail seemingly randomly at runtime. Because of that change this is expected."
- **Marek Safar, 2022 (#61381):** "Microsoft.CodeAnalysis.CSharp has different builds for NS2.0 and NETCORE3.1. [...] Most threading APIs are PNSE for browsers right now."
- **SteveSandersonMS, 2024 (#54365):** "we're not open to sacrificing any performance from the single-threaded build [...] It's OK to have significant behavioral and performance changes when people opt into multithreading, **but not when they don't**."
- **pavelsavara, 2024 (#54365):** "In last month or so, we switched the implementation of JS interop dispatch from `JSSynchronizationContext` to emscripten internal queue. Therefore [...] we can install `RendererSynchronizationContext` and replace the `JSSynchronizationContext` which is installed by default on main thread." (Note: this is in the multi-threaded context, not single-threaded.)

The roadmap items that would lift the restriction are:

1. **Multi-threaded mode reaching GA maturity** (ongoing, tracked in [#68162](https://github.com/dotnet/runtime/issues/68162) and [#85592](https://github.com/dotnet/runtime/issues/85592)). As of .NET 9 / .NET 10, still behind `<WasmEnableThreads>true` and still gated by the COOP/COEP requirement. Blazor WebAssembly **itself** does not yet fully ship on multi-threaded runtime (tracked in [dotnet/aspnetcore#54365](https://github.com/dotnet/aspnetcore/issues/54365), still open).
2. **JSPI adoption** (tracked narrowly in [#80904](https://github.com/dotnet/runtime/issues/80904), see §7). As of 2026 nothing shipping .NET-side yet.
3. **WASI-threads** — out of scope for browser wasm.
4. **CoreCLR port** — per Uno's 2025 blog post, .NET 12 (2027) is expected to bring CoreCLR to WebAssembly; at that point the whole threading story may be rethought. No public roadmap commitment yet to specifically lifting the single-threaded block-on-await restriction.

The issue that *would* be the right tracking issue for "fix this in single-threaded mode" is [#61308](https://github.com/dotnet/runtime/issues/61308), which has been open and untriaged for 4.5 years. That is about as clear a "this is not on the roadmap" signal as GitHub provides.

### 6.1 Chronology of the runtime team's posture

Pulling the timeline together from §1 and above:

- **2020** (Mono still separate from dotnet/runtime): javiercn: "The mono runtime is single threaded, so I would suggest you file an issue in the mono repo."
- **2020-09** (#26314): javiercn: "That's expected and by design. JavaScript is single threaded, so you can't block a thread as there won't be any other thread available to notify the blocked one."
- **2021-05** (#53228 opened): user reports regression from Blazor 3.2 → .NET 5, `Task.WaitAll` stops working. Closed as duplicate of...
- **2021-11** (#61308): Xyncgas's request to "define what happens when you call .wait()". Untriaged and untriaged.
- **2021-11** (#61381): AlmightyLks reports Roslyn compilation failing on .NET 6. lewing: "we decided it was better to Fail all .Wait() calls than to have some fail seemingly randomly at runtime. Because of that change this is expected." Closed.
- **2022-02** (AsyncEx #220): Stephen Cleary confirms "Blazor cannot [block] because it runs in a browser."
- **2022-10** (#68162): Experimental multithreading tracking issue. "Experimental and disabled by default."
- **2022-11** (#69409): `BrowserSynchronizationContext` API proposal for *multi-threaded* mode — first tentative move toward making the sync context public. Behind `<WasmEnableThreads>true</WasmEnableThreads>`.
- **2023** (#80904): SteveSandersonMS proposes JSPI for sync-over-async cases (crypto, assembly loading). lewing marks as "Future", no deliverables.
- **2024** (#54365): Blazor-itself-on-multithreaded-runtime; pavelsavara's detailed comment on PNSE-throwing-on-.Wait — "Let's try to push thru the problems if possible. We could give up bit later." Still open.
- **2024** (#54071): ServeMultithreadingHeaders API. Ships. Confirms COOP/COEP is "one of the things we have to deal with to make multi-threaded wasm accessible to Blazor devs."
- **2025-05**: JSPI ships in Chrome 137.
- **2025** (various): JSPI ships in Firefox 139. Safari removes its objection.
- **2026-04** (today): #61308 still open, still unresponded. No JSPI integration work on the dotnet runtime side for browser-wasm has been announced.

### 6.2 What would it take to change the posture

Three possible triggers for the team to revisit:

1. **A high-profile library that cannot be fixed otherwise** — Roslyn came close in 2021 but had the `concurrentBuild: false` escape hatch. If a major library (Entity Framework Core, Semantic Kernel as it matures, etc.) started hitting this in a way that couldn't be worked around, the team's hand might be forced.
2. **JSPI maturing to the point where it's universally available** — once Safari ships JSPI, there's a credible "use JSPI for sync-over-async" implementation path that doesn't require ASYNCIFY. Even so, this is a different problem than "make await work in single-threaded".
3. **CoreCLR-on-wasm** (Uno's 2027 projection) — a new runtime could potentially rethink the threading model from scratch. But CoreCLR has its own deep assumptions about threading (GCs, JIT warmup, etc.) that make "single-threaded CoreCLR" a novel design problem.

None of these look likely to produce action in Carbide's relevant timeframe.

## 7. JSPI / JavaScript Promise Integration

### 7.1 What JSPI is

Phase-4 W3C WebAssembly proposal ([WebAssembly/js-promise-integration](https://github.com/WebAssembly/js-promise-integration)). Lets wasm code `await` a JS Promise by having the VM suspend the wasm call stack and resume it when the Promise settles. This is stack-switching primitive, implemented in the wasm engine itself.

- **Chrome:** shipping in Chrome 137 (May 2025). See [V8 blog post](https://v8.dev/blog/jspi).
- **Firefox:** shipping in Firefox 139 (2025), still behind a flag in some configurations.
- **Safari:** dropped their standards-positions objection in late 2025 ([WebKit/standards-positions#422](https://github.com/WebKit/standards-positions/issues/422)), but not yet shipped.

From the V8 blog post:

> it is not permitted to cause JavaScript code to be suspended by using JSPI.

That is: JSPI suspends the wasm call that awaits a JS Promise. It does not suspend JavaScript itself.

### 7.2 Does JSPI solve Carbide's problem in theory?

**Not directly, because the `await` in C# is not a call into JavaScript.** Carbide's repro is `await tcs.Task` where `tcs` is a pure managed object. There's no JS Promise for JSPI to suspend on. For JSPI to help, you'd need the runtime to:

1. Detect that a managed `await` is going to suspend on a `Task` that isn't backed by a JS Promise.
2. Create a dummy JS Promise that resolves when the `Task` completes (equivalent to the existing TCS-to-Promise path, but inverted).
3. Use JSPI's `Suspending`/`Promising` wrappers to suspend the wasm stack until that dummy Promise resolves.
4. Rewind the stack.

This is a nontrivial runtime-level rewrite. It is what [dotnet/runtime#80904](https://github.com/dotnet/runtime/issues/80904) contemplates, but at that issue's scope is smaller: "solve the sync-over-async crypto / assembly-loading problem," where you have a sync API whose implementation is necessarily async. Carbide's problem is async-over-async-over-Monitor.Wait, which is weirder.

From #80904 (SteveSandersonMS, 2023):

> Today, .NET on WebAssembly is unable to implement clean support for cases where .NET APIs need to be synchronous, but they can only be implemented asynchronously inside a browser. Examples: Crypto APIs, Assembly loading (e.g., for lazy loading), where the assembly content must be fetched from the network. An upcoming standard should give us the option to solve these problems: https://v8.dev/blog/jspi, a.k.a. JSPI
>
> JSPI works simply by suspending the WebAssembly runtime until a JS promise completes. Clearly this has the drawback that a UI would be frozen for this period, but that's no different from (say) a Windows Forms UI being suspended for the microseconds needed for a crypto operation.

And lewing's response:

> This is definitely an interesting area and I think is similar to how Wasi is approaching things. I'm marking it as Future just to indicate there are no specific deliverables in net8.0 not that we won't consider it in that time frame.

Pavelsavara follow-up:

> If we are willing to block UI for duration of the DLL download, we could implement it via deprecated synchronous XMLHttpRequest. But I think that if we use JSPI and emscripten ASYNCIFY=2 it would not block the UI. I'm not sure if JSPI could be used without asyncify. In multi-threading both those scenarios are solvable with more grace.

### 7.3 Is there any sign .NET-wasm will implement JSPI-for-await soon?

No. The only tracking issue ([#80904](https://github.com/dotnet/runtime/issues/80904)) is milestoned "Future" with no deliverables. As of April 2026, there's no announcement in .NET 10, .NET 11 previews, or elsewhere about JSPI-for-managed-await.

**Takeaway for Carbide:** JSPI is a theoretically interesting substrate for a future Option C+ (patch `monitor.c` to yield to event loop via JSPI instead of failing) but no one in Microsoft is working on that integration today. A JSPI-based Carbide would require Carbide to fork + modify `dotnet/runtime` C source, same level of effort as Option C.

### 7.4 Emscripten's JSPI support

Emscripten supports JSPI as of its 3.x series via the `ASYNCIFY=2` flag (sometimes called "JSPI mode"). This differs from classic `ASYNCIFY=1` in that the wasm code isn't rewritten at compile time to save/restore stack frames; instead, the engine itself suspends the wasm call stack natively. From Emscripten's docs on [Asynchronous Code](https://emscripten.org/docs/porting/asyncify.html):

> With JSPI, the stack is only unwound/rewound when suspending to JavaScript, so the overhead is much smaller than with Asyncify.

The .NET runtime does not currently ship with either `ASYNCIFY=1` or `ASYNCIFY=2` enabled for the browser-wasm build. Enabling either requires runtime team action.

### 7.5 JSPI's "javascript-promise-integration" proposal details

From [WebAssembly/js-promise-integration Overview](https://github.com/WebAssembly/js-promise-integration/blob/main/proposals/js-promise-integration/Overview.md):

> A suspendable WebAssembly function is one that is marked, as part of import/export, to allow the WebAssembly computation to suspend while waiting for a Promise to resolve. Such a function's import signature is wrapped with a `Suspending` wrapper; its export signature is wrapped with a `Promising` wrapper.

The crucial subtlety: JSPI only operates at the import/export boundary. Internal wasm function calls cannot suspend via JSPI. This means the `Monitor.Wait` call inside the mono runtime can't simply "be JSPI-enabled" — the JSPI suspension point would have to be at a wasm→JS call (an import). This is why pavelsavara's comment mentions "emscripten ASYNCIFY=2" rather than pure JSPI: the ASYNCIFY compile-time pass is what enables suspension from any wasm function, while JSPI provides the efficient primitive at the boundary.

For Carbide's Option C to use JSPI instead of classic ASYNCIFY, the runtime would need to:
1. Identify `monitor.c`'s `mono_monitor_wait_internal` as a suspension point.
2. Route the infinite-timeout branch to call a JS function that returns a pending Promise.
3. Wrap that JS call with JSPI's `Suspending` wrapper.
4. When the monitor is eventually signaled, resolve the Promise to wake the wasm side.

Nontrivial but not insurmountable. The infrastructure for the "wrap imports with Suspending" part is already in Emscripten.

## 8. Other frameworks/languages facing the same trap

### 8.1 Rust / wasm-bindgen

Rust's async model was designed with single-threaded wasm as a first-class target. `wasm_bindgen_futures::spawn_local` schedules a future to run on the microtask queue (`Promise.resolve().then(...)`). This works because Rust futures are polled — the caller of `spawn_local` does not block. The executor simply polls whenever a waker is called, and the waker schedules a microtask.

Key references:
- [wasm_bindgen_futures::spawn_local](https://rustwasm.github.io/wasm-bindgen/api/wasm_bindgen_futures/fn.spawn_local.html)
- [`wasm-rs-async-executor/single_threaded`](https://docs.rs/wasm-rs-async-executor/latest/wasm_rs_async_executor/single_threaded/)
- [rustwasm/wasm-bindgen#3633](https://github.com/rustwasm/wasm-bindgen/issues/3633) — tracking JSPI support in wasm-bindgen.

The Rust model works because the language's async is *pull-based* (`poll`-driven) rather than *push-based* (state-machine-allocated-on-heap with a callback registered via `AwaitUnsafeOnCompleted`). There's no runtime-level blocking wait anywhere in the Rust executor; the executor's `run()` loop is ordinary `loop { poll; if not ready, wait for waker }`, and "wait for waker" is implemented as "return from `poll` and trust the browser to call us back."

**Cross-pollination takeaway for Carbide:** Carbide can't retrofit .NET's Task onto a poll-based model without source-level rewriting. But Option D (custom coroutine runtime via source generator) is essentially this approach — it is what Unity's coroutine system does, and it's what Rust-wasm does. Viable if you're willing to abandon the Task-based API for user code.

### 8.2 Emscripten ASYNCIFY

[emscripten.org/docs/porting/asyncify.html](https://emscripten.org/docs/porting/asyncify.html). Not a language feature — a compile-time pass that rewrites compiled wasm to save/restore the native call stack on every call that might suspend. Binary-size and runtime cost: typically 15-50% larger binary, 5-30% slower runtime. Emscripten uses it to support `emscripten_sleep()`, synchronous `emscripten_wget()`, and synchronous POSIX I/O. Pyodide used it historically; has switched to JSPI where available.

Pavelsavara's comment (§7.2) mentions `emscripten ASYNCIFY=2` as one potential path, but the .NET team has not shipped this.

**For Carbide:** Option C in the investigation report (patch `monitor.c` to yield via `emscripten_sleep`) would specifically require ASYNCIFY=1 at link time, with the 15-50% binary-size cost and 5-30% runtime cost. That's the exact tradeoff Rick Strahl's [blog post on Blazor startup perf](https://weblog.west-wind.com/posts/2018/jul/31/web-assembly-and-blazor-reassembling-the-web) warns about from the opposite direction.

### 8.3 Pyodide (Python on wasm)

From Pyodide's [JavaScript Promise Integration in Pyodide](https://blog.pyodide.org/posts/jspi/) blog post:

> JavaScript Promise Integration (JSPI) is a new web standard that solves the sync/async problem by allowing calls that seem synchronous from Python's perspective but are actually asynchronous from JavaScript's perspective. JSPI enables this by stack switching. [...] If a browser supports JSPI, both asyncio.run() and event_loop.run_until_complete() will use stack switching to run async tasks.

Pyodide is the canonical case study of "legacy language with blocking sync APIs running in single-threaded wasm." They burned 4+ years on Asyncify, hit its limits, and have now migrated to JSPI where available.

Pyodide's `run_sync()` function (integrated since Pyodide 0.27.7 in 2025) **is** the JSPI-based "run this async code synchronously" primitive. It's what .NET-wasm would eventually need to provide for Carbide's use case, but .NET has shipped nothing analogous.

### 8.3.1 Pyodide's `syncify()` / `run_sync()` — the prior art for sync-over-async in a managed runtime

Pyodide's specific mechanism is useful reference for Carbide's Option C:

> With JSPI, `pyodide.ffi.run_sync(coroutine)` runs an async Python function synchronously by creating a suspended wasm coroutine, awaiting the async result from JS (via JSPI), and resuming the wasm side when done. This is used for `urllib3`'s synchronous HTTP path which would otherwise be impossible on single-threaded wasm.

Pyodide's blog post on JSPI: [blog.pyodide.org/posts/jspi/](https://blog.pyodide.org/posts/jspi/):

> When synchronous JavaScript code needs an asynchronous result, it "blocks" by suspending the Python interpreter's stack. The browser event loop continues; when the async work completes, the interpreter's stack is restored and execution continues as though nothing happened.

This is exactly the primitive Carbide's `Monitor.Wait(INFINITE)` would need. Pyodide achieved this by making their `syncify()` function:
1. Accept an async JS function or Python coroutine.
2. Use JSPI's `Suspending` wrapper on a JS function that wraps the async work.
3. Call that wrapped function from sync wasm code.
4. The engine suspends the wasm stack until the underlying Promise settles.
5. When the Promise settles, the wasm stack is resumed with the resolved value.

For .NET to adopt this pattern for `Monitor.Wait`, the infrastructure plumbing would be:
1. Modify `mono_monitor_wait_internal` to call an Emscripten-exported JS function `mono_yield_and_wait_for_monitor(monitorObj)` instead of failing.
2. The JS function returns a Promise that resolves when the monitor is signaled (via the managed-side `Monitor.Pulse*` code path also being modified to resolve the Promise).
3. The JS function is registered as a JSPI `Suspending` import.

This is a 2-3 order of magnitude larger project than Option C as currently described in the investigation report, but it's the cleanest direction for JSPI-based Option C+.

### 8.3.2 Pyodide's WebLoop asyncio implementation

For the Python-side pre-JSPI story, Pyodide provides a WebLoop that emulates Python's asyncio by hooking into the browser's event loop via `setTimeout`:

> Add web event loop for supporting asyncio ([PR #958](https://github.com/pyodide/pyodide/pull/958)).

This WebLoop is the Python analog of Carbide's `CarbideSyncContext` with setTimeout-based Post. It worked for Python because Python's asyncio doesn't have a hidden `Monitor.Wait(INFINITE)` fallback — asyncio is fully cooperative and runs on top of whatever event loop you give it. Python's asyncio event loop is conceptually the Unity-style pump, not the .NET Task state machine.

**Key lesson from Pyodide:** Python's fully-cooperative asyncio made WebLoop work. C#'s hybrid model where Task can either be cooperative (via SC) or blocking (via ThreadPool fallback with Monitor.Wait) makes CarbideSyncContext not sufficient by itself. The fix has to be at the runtime level, not at the SC level.

### 8.4 Uno Platform

Uno Platform has been the most aggressive .NET-on-wasm consumer with threading experience. They shipped multi-threaded wasm builds years before Microsoft officially supported it ([their 2020 post](https://platform.uno/blog/webassembly-threading-in-net/)). Their current state-of-the-web post ([2025/2026](https://platform.uno/blog/the-state-of-webassembly-2025-2026/)) notes:

> the team is working on transitioning from the Mono runtime to the CoreCLR runtime [...] multithreading as a focus area once CoreCLR ships in .NET 12 (2027).

And:

> one approach that's possible is to use feature detection for JSPI and fall back to the slower Asyncify approach if JSPI isn't available.

Uno's pragmatic approach: offload computation to Web Workers via their `DispatcherQueue.HasThreadAccess = false` + `DispatchAsync` pattern, and wait for the runtime to get better. They don't have a Carbide-shaped "compile and run C# in the browser" use case, so their story doesn't directly map.

### 8.5 Swift on WebAssembly

[SwiftWasm's Concurrency guide](https://book.swiftwasm.org/getting-started/concurrency.html) documents that Swift's `async`/`await` works on single-threaded wasm because Swift's concurrency runtime is poll-based, similar to Rust. Not directly applicable to .NET.

### 8.6 Cross-pollination summary

| Runtime | Single-threaded async model | How they avoid the Carbide trap |
|---|---|---|
| .NET-wasm (single-threaded) | Task-based, push-based state machines; SC.Current null by default | Runtime carefully avoids `Monitor.Wait(INFINITE)` in the default scheduling path; trips when custom SC or blocking-wait-using library is added |
| .NET-wasm (multi-threaded) | Task-based; JSSynchronizationContext installed | Real pthread scheduling allows true blocking waits on non-UI threads |
| Rust-wasm (wasm-bindgen-futures) | Future-based, poll-driven | Executor never blocks; `poll` returns to browser event loop; `Waker` re-schedules via microtask |
| Swift-wasm | async/await, poll-driven under the hood | Same as Rust — poll-based concurrency, no blocking waits |
| Pyodide (Python-wasm) | asyncio-based; historically Asyncify, now JSPI | Stack-switching primitive at the runtime level |
| Emscripten (C/C++-wasm) | `emscripten_sleep`, `emscripten_wget_sync` (sync APIs) | `ASYNCIFY=1` or `ASYNCIFY=2` (JSPI) stack rewriting or engine-level suspension |
| Unity (C# + custom runtime) | `IEnumerator<YieldInstruction>` coroutines | Poll-based; coroutine runtime calls `MoveNext()` on schedule, never blocks |

Carbide is the only entry in this table with the *.NET-wasm single-threaded, custom SC* configuration. Every other setup either (a) uses a poll-based concurrency model that doesn't block, (b) has a runtime-level stack-switching primitive (Asyncify/JSPI), or (c) accepts the Microsoft-blessed "don't block" contract with the default (null) SC.

## 9. Synthesis: reconciling Blazor's "it works" with Carbide's "it trips"

Here's my best-reasoned account of the discrepancy, synthesizing §4 and the T2.1 investigation report:

1. **Plain single-threaded Blazor:** `SynchronizationContext.Current == null` (javiercn, §4.1). `await tcs.Task` in a Blazor component sets up its continuation via `AwaitUnsafeOnCompleted`, which — when no sync context is captured — hands off the continuation to the default `TaskScheduler` (the `ThreadPoolTaskScheduler`). In single-threaded wasm, `ThreadPool.UnsafeQueueUserWorkItem` goes through `EnsureWorkerRequested` → `MainThreadScheduleBackgroundJob` → `setTimeout(0, BackgroundJobHandler)`. When the completion fires (e.g., from a JS callback calling `SetResult`), the same path re-wakes the pump. **No `Monitor.Wait(INFINITE)` in this path.**

2. **Carbide:** `SynchronizationContext.Current == CarbideSyncContext` because Carbide installs it. Per stephentoub in [#69409](https://github.com/dotnet/runtime/issues/69409):
   > If the antecedent operation completes on the same context, however, there is an optimization in the await infrastructure that will avoid posting back

   The identity check for "same context" is `ReferenceEquals(previous, current)`. Carbide's `CarbideSyncContext.Instance` is a singleton, so this is satisfied. The await infrastructure should route the continuation through `CarbideSyncContext.Post`, which Carbide has tested as inline AND as `setTimeout(0)`-deferred, both of which trip (investigation §4.3).

3. **Therefore the `Monitor.Wait(INFINITE)` is NOT on the `Post(continuation)` path.** It's happening somewhere else in the machinery of the suspension itself — most likely in one of:
   - **`AsyncTaskMethodBuilder.AwaitUnsafeOnCompleted`'s state-machine box allocation path** — uses `ConcurrentQueue` internals that may spin then block on a lock if contended under the covers.
   - **`ExecutionContext.Capture()`/`Restore()`** — used on the suspension; on mono-wasm the internal TLS access may route through a `ManualResetEventSlim` initialization that takes a lock.
   - **`Task`'s internal `m_stateFlags` transition** — specifically the `TrySetContinuation` code path that uses `Monitor.Enter` to append to the `TaskContinuationObject` chain; the `Enter` is always-finite, but the corresponding `Wait` if a contention-timeout fallback fires could be infinite.

   This is *exactly* the hypothesis the investigation report §3 opens with. The public literature does not disambiguate which of these is fires; my best prior is `AwaitUnsafeOnCompleted`'s state-machine box allocation, because:

   a. The trip is deterministic — always fires when the await would suspend, never when it completes synchronously. That matches a code path that only executes on suspension (the `UnsafeOnCompleted` callback registration side), not the continuation side.

   b. It fires before any JS callback runs — matching a synchronous setup path, not an asynchronous completion path.

   c. It trips with both inline and macrotask-deferred `Post` — ruling out the Post path itself.

4. **Why doesn't plain Blazor hit this?** Because plain Blazor has no `SynchronizationContext.Current`. The state machine's suspension path, on seeing `SynchronizationContext.Current == null`, takes a different branch — one that doesn't go through the logic that in Carbide's case trips into a runtime primitive. I couldn't find a public verbatim source for this branch difference; it would be a productive area for on-runtime debugging (investigation report §9).

5. **Practical test to confirm:** run Carbide's minimal repro WITHOUT installing `CarbideSyncContext` (i.e., let `SynchronizationContext.Current` remain null). My prediction: `await Task.Delay(50)` works, `await tcs.Task` with a later `tcs.SetResult()` also works. This would conclusively pin the trip to "custom sync context + runtime suspension path interaction." At that point the fix is not "find what's tripping Monitor.Wait(INFINITE)" but "don't install a sync context at all" — and work around the DOM-access-on-main-thread issue differently (e.g., just by virtue of single-threaded wasm always being on the main thread, there's nowhere else to go). This is an under-explored option in the investigation report.

### 9.1 Why a custom SC might trip where Blazor's default (null) SC does not

To be more precise about the kg/stephentoub exchange from §5.9: with a custom SC installed:

- `TaskAwaiter.UnsafeOnCompleted` is called during the suspension.
- Inside it, `TaskContinuationObject` is allocated with `SynchronizationContext` captured.
- When the continuation needs to be scheduled, the code path at `TaskContinuation.cs:610` checks "do we have a captured SC?" If yes, it does NOT run the continuation inline, and it does NOT use `TaskScheduler.Current`; instead it routes through `SynchronizationContext.Post`.
- In Carbide's case, `CarbideSyncContext.Post` is called. Inline or deferred both still fire the PNSE *before the Post call is reached*. That's why the investigation report §4.3 found that varying `Post` didn't help.

This means **the PNSE fires during the state-machine setup phase, not during the continuation scheduling phase**. Specifically, it fires somewhere between `AwaitUnsafeOnCompleted` and the return from `Post`. Candidate sites:

**(a) `AsyncTaskMethodBuilder<T>.SetStateMachine` / `SetException` / internal box allocation.** The `IAsyncStateMachineBox` is allocated lazily when the first actual suspension happens. The allocation uses `s_asyncMethodBuilderCache` which is a `Volatile`-backed pointer, but may involve `ConditionalWeakTable` under the covers. `ConditionalWeakTable` uses locking, and contended access could trip.

**(b) `ExecutionContext.Capture()` / `CapturedContext.Run()`.** If the SC is being cloned or the EC has a flow that's never been realized on this thread before, there may be a lazy initialization of a `ThreadLocal<T>` somewhere in the infrastructure that uses `SemaphoreSlim` to guard the init.

**(c) `ThreadPool.UnsafeQueueUserWorkItemInternal`'s first-time path.** Maybe the first time the ThreadPool is used after SC install, it runs a lazy init path that blocks on ManualResetEventSlim.

**(d) `TaskScheduler.FromCurrentSynchronizationContext()`.** If Carbide's `Post` uses this somewhere, or the BCL's await wiring creates it behind the scenes, it may internally use a lock + wait.

The exact candidate is not disambiguable from public reports. All of them are possibilities. The Chromium debugger approach in the investigation report §9 would resolve this.

### 9.2 A test sequence to disambiguate

Recommended diagnostic protocol (using Chrome DevTools "Pause on exceptions → pause on caught"):

1. Run Carbide with `SetSynchronizationContext(null)` before the `await tcs.Task` repro. Expected: works. If it works, the SC is the problem. If it fails with the same PNSE, something else in Carbide's boot is at fault.
2. If step 1 works: re-install `CarbideSyncContext` but with `SynchronizationContext.SetSynchronizationContext(SynchronizationContext.Current)` immediately after — so the "current" SC is the same identity reference as before the await. This tests whether the stephentoub optimization triggers.
3. Run with Chrome's "Pause on exceptions" enabled, set to pause on caught. The first pause should be inside the mono runtime's `mono_error_set_platform_not_supported` call. Walk the C stack upward to find the first *managed* frame — that's the actual culprit.
4. Compile managed side with `MONO_INTERPRETER_OPTIONS=-ssa` (per pavelsavara in #122529) to avoid the SSA optimization that sometimes hides the real frames.
5. Check whether the first managed frame is `ManualResetEventSlim.Wait`, `SemaphoreSlim.Wait`, or something else entirely.

This would give Carbide a specific runtime-bug-report for dotnet/runtime, which would likely get a fix or at least a workaround suggestion from the runtime team.

### 9.3 Alternative theory: the Microsoft.CodeAnalysis (Roslyn) code path

Carbide's repro was traced to fire synchronously at the `await` site inside `ProjectCompiler.RunInteractiveAsync`. But Carbide's full stack includes invoking Roslyn to compile the user's code BEFORE the interactive run. Roslyn has known blocking `Task.Wait` usage (see #61381) which can be worked around with `concurrentBuild: false`.

**Question to verify in Carbide's code:** does `ProjectCompiler.RunInteractiveAsync` call into Roslyn with `CSharpCompilationOptions` that have `concurrentBuild: true` (the default)? If so, the first-compilation path might be leaving some internal Roslyn state in a "we started a parallel worker" state, and the very first await in user code is hitting that worker's Wait.

This is worth checking even if Option F works. It may explain the original theory 4.2 (about `ConfigureAwait(false)`): the ConfigureAwait dropping the SC capture changed which code path the first await's continuation took.

### 9.4 Does `await Task.Yield()` really work in Carbide?

The investigation report says:

> `await Task.Yield()` → `YieldAwaiter.OnCompleted` posts via `SC.Post`; `CarbideSyncContext` runs inline

If Task.Yield works but `await tcs.Task` (later completed) doesn't, the difference must be in whether `OnCompleted` (the safe variant) vs `UnsafeOnCompleted` (the fast variant) is used. `Task.Yield()` uses `YieldAwaiter` which calls `OnCompleted` (the safe variant — it captures ExecutionContext). A `TaskAwaiter` awaiting an incomplete task calls `UnsafeOnCompleted` (the fast variant — does not capture EC, relies on ThreadPool to have flowed it).

Difference: **the EC handling**. Task.Yield's OnCompleted probably captures the EC eagerly during Post; Task's UnsafeOnCompleted assumes EC is captured by ThreadPool. In single-threaded wasm, the "ThreadPool" is the setTimeout-based pump, which may not have the right EC capture semantics.

If this is the theory, the fix could be narrower: force all Carbide user awaits to go through `OnCompleted` rather than `UnsafeOnCompleted`. But this requires rewriting the state machine, which takes us back to Option D territory.

### 9.5 Minimal diagnostic repro suggested for the dotnet/runtime team

If Option F (drop SC) doesn't fix Carbide, the cleanest path to a fix from Microsoft is to file a new runtime issue with a minimal repro that:

1. Targets `net10.0-browserwasm` (single-threaded).
2. Has a trivial `Program.Main` that:
   ```csharp
   SynchronizationContext.SetSynchronizationContext(new MinimalSC());
   var tcs = new TaskCompletionSource();
   await tcs.Task;
   ```
3. Observes the PNSE at the await.

If this reproduces (it's unclear whether it does, because stepping away from Carbide's specific code may hide the issue), this is a clean issue that lewing/pavelsavara would likely engage with. If it does NOT reproduce outside Carbide, that strongly suggests the issue is specific to something Carbide's boot is doing — in which case Option F becomes even more likely to work.

A "MinimalSC" for this purpose:

```csharp
sealed class MinimalSC : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state)
        => d(state);
    public override SynchronizationContext CreateCopy() => this;
}
```

(This is roughly what Carbide's `CarbideSyncContext` does today.)

## 10. Recommended next steps for Carbide

Referencing Options A-E from the T2.1 investigation report:

### Implications for Option A (sync-only user code, polling pump)

**Unchanged.** This remains a technically-feasible fallback, but the research confirms it's the path no one else in the .NET ecosystem takes as their primary answer. Blazor, Uno, Rust-wasm, Pyodide all provide *real* async suspension in single-threaded mode via their own mechanisms. Only the "internals that call Task.Wait()" libraries (OData, Roslyn with concurrentBuild, etc.) end up effectively Option-A-shaped, and even they are considered broken/deficient.

One important nuance: Option A as stated in the investigation report contemplates JS-driven polling via JSExport pump. This is subtly different from the Blazor/Rust-wasm model where the runtime itself drives a pull-based model. For Carbide's specific case where user code needs to `await Console.In.ReadLineAsync()` (waiting for user input), Option A would require:

- `Console.In.ReadLineAsync` → returns a `CarbideReadLineHandle` (not a Task).
- User writes `await Console.In.ReadLineAsync()` → handled by Carbide's source generator → rewrite to `for (var handle = Console.In.ReadLineAsync(); !handle.IsReady; handle.Poll()) { ... }`.
- Carbide JS pump calls a JSExport method on every `setTimeout(0)` tick to advance the handle.

This is workable but restrictive. User code in Option A cannot use any third-party async library that returns a `Task` — the whole ecosystem of `System.Net.Http`, `System.Text.Json` streaming APIs, `Microsoft.Extensions.*` async extensions etc. becomes off-limits. That's a severe capability regression from what T2/T3 advertised.

### Implications for Option B (ship multi-threaded runtime with WasmEnableThreads=true)

**More attractive** after this research for hosted-Carbide. Specifically:

- Multi-threaded mode installs `JSSynchronizationContext` automatically. That's the one Microsoft-blessed, Blazor-team-tested, all-BCL-async-primitives-validated sync context.
- The COOP/COEP requirement is well-understood and has a canonical Microsoft path (the `ServeMultithreadingHeaders` option). For Carbide hosted on Carbide Contributors' own deploys, it's purely "add two HTTP headers."
- Tracked issue for Blazor-itself-on-multithreaded ([dotnet/aspnetcore#54365](https://github.com/dotnet/aspnetcore/issues/54365)) is still open as of April 2026 — Blazor itself hasn't fully shipped on multithreaded runtime. But Carbide doesn't need Blazor; it needs the underlying runtime, which IS GA for the multi-threaded mode (with `<WasmEnableThreads>true`).
- Binary size hit is ~5-10 MB extra assets. For Carbide's "CDN library for embedders" use case this is significant; for "hosted app on Carbide Contributors' deploys" this is fine.

**Less attractive** for drop-in-CDN-library Carbide. Because of the COOP/COEP requirement it genuinely doesn't work as a plain `<script type="module">` import from a third-party page that hasn't configured its headers.

Additional considerations surfaced by this research:

- **Deputy-thread mode caveat.** The multi-threaded browser-wasm runtime as of .NET 9+ has a "deputy thread" mode where all managed code runs on a background worker, and the UI thread is reserved for JS. This is what pavelsavara was optimizing in #54365. For Carbide, deputy-thread mode would mean user-compiled C# code runs on a worker, which means `Console.WriteLine` (which pavelsavara warns throws PNSE in deputy mode because it talks to the UI thread) may need reworking. This is a significant caveat Option B's current description doesn't cover.
- **Simple multi-threaded mode (no deputy thread).** The pre-deputy multi-threaded mode has .NET code running on the main thread with WebWorkers available for `Task.Run(..)`. This is the mode that was documented in `features.md`. This is almost certainly what Carbide wants: main-thread managed code (so `Console.WriteLine`, DOM access, etc. work) + the `JSSynchronizationContext` that gives real async suspension. Confirming which mode is the default in .NET 10 is a documentation gap.
- **Build-from-source requirement for control.** If Carbide wants to lock down which multi-threaded mode is used, the safest path is forking `dotnet/runtime` and building with specific flags. This is Option B's ~2GB/20-40 min-build-time cost.
- **AOT support.** Multi-threaded mode works in both interpreter and AOT modes per the `features.md` doc, so this isn't a new constraint.

### Implications for Option C (patch monitor.c + ASYNCIFY)

**Less attractive** after this research. Specifically:

- No one in the Microsoft/mono team is working on this path, so you'd be shipping and maintaining a fork in perpetuity.
- Pyodide tried this (Asyncify) and migrated away to JSPI; strong signal that the Asyncify path has long-term maintenance/performance costs that outweigh its benefits.
- The ~15-50% binary-size hit and 5-30% runtime slowdown are real.

**More attractive** if JSPI is on the table. Specifically: you could imagine a hybrid Option C+ where the runtime patch uses JSPI instead of ASYNCIFY for the `monitor.c` yield. Binary-size cost ~0, runtime cost negligible. BUT: requires Chrome 137+ / Firefox 139+ only, Safari users blocked, and you're still maintaining a runtime fork.

### Implications for Option D (custom coroutine runtime via source generator)

**Moderately more attractive** after this research. Rust-wasm proves that a single-threaded wasm language CAN have ergonomic async if the async model is poll-based rather than state-machine-push-based. Unity has shipped their coroutine model for 15+ years. Swift's `async`/`await` works on single-threaded wasm for the same reason.

The approach:
1. Source generator rewrites user `async`/`await` → `IEnumerator<YieldInstruction>`-returning methods.
2. Carbide runtime hosts an Unity-style coroutine loop driven from JS `setTimeout(0)` / microtask scheduling.
3. User code uses a `CarbideTask<T>` type (not `Task<T>`) — or we generate BOTH: real Task for the "runs synchronously or trivially" path, coroutine for the "needs real suspension" path.

This is much more work than Option B for the user-code case but keeps the CDN-embeddable property.

**Caveat from research:** pre-compiled third-party libraries still can't work. If the user's code references Spectre.Console and Spectre.Console internally calls `Task.Delay` (which it does for animated output), that call is NOT going through Carbide's source generator and still ends up in the runtime path that works in Blazor but trips in Carbide. So Option D only works if Carbide can either (a) ship an IL rewriter that handles pre-compiled library IL post-hoc (very invasive), or (b) accept that only user code gets the ergonomic async.

### Implications for Option E (sync-only supported contract)

**Unchanged** — still honest scoping, but this report strengthens the reasoning to NOT silently ship Option E. The community's answer to "Cannot wait on monitors" is unanimous: use async-all-the-way, don't block. If Carbide ships with "can't use await in user code at all," that's regression well below what every other .NET-wasm runtime (Blazor, Uno, even BlazorWorker-offloaded Blazor) delivers today.

### Implications for Option D (custom coroutine runtime via source generator) — further detail

The research reinforces Option D's viability through multiple independent examples:

- **Rust-wasm** ships a working poll-based async executor (wasm_bindgen_futures) that works perfectly in single-threaded wasm — proof of concept that the architectural pattern can work.
- **Unity** has shipped coroutines for 15+ years with no threading support at all — proof of concept that C# programmers can effectively use coroutine-style async.
- **Swift-wasm** also uses poll-based async — another data point that poll-based wins.

The source generator approach for Option D has a concrete reference implementation in Unity's `MonoBehaviour.StartCoroutine(IEnumerator)`. The user writes:

```csharp
IEnumerator MyCoroutine() {
    yield return new WaitForSeconds(1.0f);
    yield return carbideConsole.ReadLineAsync();
    yield return carbideHttp.GetAsync("https://...");
}
```

Under the hood, a CarbideCoroutineScheduler calls `MoveNext()` periodically (e.g., every `setTimeout(0)` tick), inspects `Current`, and decides when to call `MoveNext()` again. No `Task`, no `Monitor.Wait`.

The remaining unsolved problem for Option D is pre-compiled library compatibility. This is genuinely hard because:

- Pre-compiled libraries emit `async`-state-machine CIL, not `IEnumerator`-coroutine CIL.
- The post-compile rewriter would need to recognize async methods, restructure their state machines, and somehow resolve calls to BCL `Task.Delay` / `Task.FromResult` / `HttpClient.GetAsync` etc. which can't be rewritten (they're in the BCL, not in user/library code).
- Unity solves this by providing async→coroutine adapters for specific APIs (e.g., `UnityWebRequest.SendWebRequest()` returns a `UnityWebRequestAsyncOperation` that implements custom yield semantics). A Carbide equivalent would need to wrap `HttpClient.GetAsync` in a coroutine-friendly shim, and to NOT use `await` on the underlying Task.

So Option D's realistic scope is: "user code and Carbide-authored shims work as coroutines; pre-compiled third-party libraries that use Task-based async don't work." This might be acceptable as a v1, but it means Spectre.Console's animated progress bars (which internally `await Task.Delay`) still won't work.

### New option surfaced by this research — "Option F: drop CarbideSyncContext, use runtime default"

Per §9.5 above: **test what happens if Carbide doesn't install its own `SynchronizationContext` at all.** Let `SynchronizationContext.Current` remain null (or whatever the runtime defaults to in single-threaded mode, which is null). The runtime's own ThreadPool-pump-driven continuation path is what Blazor uses and it handles `await tcs.Task` correctly. Carbide may not actually need its custom SC for correctness; it may only need it for DOM-affinity routing, which in single-threaded mode is trivially satisfied because there's only one thread.

**Experimental cost:** ~0 LOC — just comment out the `SetSynchronizationContext(CarbideSyncContext.Instance)` call in `CompilationInterop.InitAsync` and `ProjectCompiler.RunInteractiveAsync` and re-run T2's `interactive-readline` fixture. If it works, this is by far the cheapest resolution to T2.1 and it restructures the whole options list — Options A, B, C, D become unnecessary for the "make await work in user code" dimension.

**Risk:** if the custom SC is load-bearing for something *other* than main-thread routing (e.g., sequencing Carbide's own internal events vs user code), dropping it has consequences. Worth establishing what else the SC is doing before testing this.

**Recommendation:** try Option F as a 30-LOC experiment BEFORE committing to B, C, or D. If it works, pick the best parts of the investigation report's "keep" changes and ship T2.1 with a native runtime SC. If it doesn't work, the investigation is now strictly about "what in Carbide's specific state-machine-suspension path calls `Monitor.Wait(INFINITE)`" — attach Chromium debugger, break on `mono_error_set_platform_not_supported`, walk the C stack and the managed stack, and file a runtime bug with a specific minimal .NET-only repro (no Carbide code involved) if the cause is in the BCL.

### Option F in detail

**What Option F does concretely:**

Remove every call to `SynchronizationContext.SetSynchronizationContext(CarbideSyncContext.Instance)` in Carbide's codebase. Leave `SynchronizationContext.Current` as whatever the mono runtime has at boot (which, per `javiercn` and the JSSynchronizationContext source, is `null` in single-threaded browser-wasm).

**Why this is plausibly sufficient:**

Carbide's Interactive run is single-threaded. There is no other thread for continuations to race to. The purpose of a custom SC in multi-threaded browser-wasm is to marshal continuations *back* to the main thread after they completed on a worker thread. In single-threaded wasm, the continuation is ALREADY on the main thread; there's nowhere else for it to be. The SC adds nothing.

**What might still be load-bearing for the custom SC:**

1. **Ordering.** If Carbide's design needs to order Carbide-internal continuations (e.g., rendering updates) vs user-code continuations in a specific way, a custom SC could enforce that. But if this is the case, it could just as well be enforced by a lighter-weight scheduler object, not by replacing SC.
2. **Blocking JS callbacks.** If Carbide has JS code that calls into `[JSExport]` synchronously and expects the managed-side handler to complete before returning, and that handler does an `await` — the `await`'s continuation has to fire from somewhere. In single-threaded mode, it will fire from the next `setTimeout(0)` tick, which is after the JS call has returned. This is an ordering question, not an SC question, and it's the same whether you have a custom SC or not.
3. **Testing/headless paths.** Carbide's test infrastructure may rely on the SC for deterministic testing. If so, the SC is load-bearing for test-only purposes and could be scoped to test mode only.

**Smoke test protocol for Option F:**

1. Create a git branch `t21-option-f-no-sc`.
2. Comment out or conditionally disable the two `SetSynchronizationContext` calls (in `CompilationInterop.InitAsync` and `ProjectCompiler.RunInteractiveAsync`).
3. Run the T2 minimal repro (`var tcs = new TaskCompletionSource(); await tcs.Task;`).
4. Observe. Expected outcomes:
   - (a) It works identically to plain Blazor → Option F is a fix.
   - (b) It still PNSEs → something other than SC install is the cause; revert and proceed to Chromium debugging.
   - (c) It has different behavior (e.g., different exception, different deadlock pattern) → the SC was masking a different bug; investigate that.

**Engineering cost:** ~15 LOC diff, ~1 hour test run.

**Risk if Option F works but has regressions:**

The SC in Carbide might be required for some use cases not covered by the minimal repro. For instance, if Carbide's rendering loop relies on `SC.Post` ordering for interleaving user code and rendering. If removing the SC causes renders to interleave wrong, that's a regression for T2 fixtures that currently pass. Mitigation: keep the SC but don't install it globally; use it as a local dispatcher for the rendering loop only.

### Priority ordering of options after this research

1. **Option F first** (cheapest, may just fix it).
2. **If F fails, Chromium debugging to find the exact failing frame.** This is mandatory before picking between B/C/D.
3. **If F fails AND the root cause is in Carbide-internal code:** fix it there (cheaper than any runtime change).
4. **If F fails AND the root cause is in the BCL or mono runtime:** file an issue with minimal repro, pick Option B or D.
5. **Option B** if the repro needs multi-threaded runtime and COOP/COEP is acceptable.
6. **Option D** if COOP/COEP is unacceptable.
7. **Option C** only if both B and D are ruled out and forking the runtime is acceptable.
8. **Option A** only if all of the above fail and Carbide must ship something.
9. **Option E** is the honest "ship scope reduction" fallback but should not be chosen silently.

## Appendix A — Primary-source quotes index (for fast cross-reference)

Collected here for ease of citation in future Carbide docs:

### A.1 On "is it a bug or a feature?"

**javiercn (ASP.NET Core team, 2020):**
> That's expected and by design. JavaScript is single threaded, so you can't block a thread as there won't be any other thread available to notify the blocked one.

**javiercn (2020):**
> The mono runtime is single threaded, so I would suggest you file an issue in the [mono repo](https://github.com/mono/mono).

**javiercn (2020):**
> The browser is a single threaded runtime (except for wasm threads that are not widely supported on all platforms). If a library blocks the main thread, there's no additional thread where work can resume and unblock the blocked thread. This is not a decision that we made, it's a limitation imposed by the environment.

**lewing (.NET runtime team, 2022):**
> Given that the current wasm runtime is single threaded, In the move to .NET6 we decided it was better to Fail all .Wait() calls than to have some fail seemingly randomly at runtime. Because of that change this is expected.

**marek-safar (.NET runtime team, 2022):**
> Most threading APIs are PNSE for browsers right now.

**Stephen Cleary (community authority, 2022):**
> From the call stack, it looks like you're trying to use `AsyncContext` to block on asynchronous code during a render. This isn't possible in Blazor, since it runs in the browser. You need to use `async` all the way; there is no other choice in that environment.

### A.2 On the single-threaded Blazor architecture

**javiercn (2020):**
> currently Blazor WebAssembly doesn't have a synchronization context, but it is recommended you still call StateHasChanged from within `InvokeAsync` if you are doing something from a potential "background" thread since that will make sure your code keeps working if we ever introduce multiple threads on Webassembly.

**Microsoft Learn ASP.NET docs:**
> Blazor's server-side synchronization context attempts to emulate a single-threaded environment so that it closely matches the WebAssembly model in the browser, which is single threaded.

**scalablecory (runtime team, on #61381):**
> iirc WASM is single-threaded, so a Task.Wait() that blocks would be a deadlock, right? If so, I think Blazor/WASM is working fine here, and you may need to call an async version of that method and/or file a bug in dotnet/roslyn.

### A.3 On the custom-SC-trips-inline-optimization issue

**kg (runtime team, 2022, #69409):**
> it looks like having a sync context registered at all will punt all running workloads into a degraded mode where they never run continuations inline. Is this less of a problem than it appears?

**kg (2022):**
> The issue is that in my testing, this logic results in all continuations being posted to the threadpool even if they were ConfigureAwait(false), specifically because we have a sync context. [...] The presence of a sync context is being used as a heuristic for "continuations should never run here".

**stephentoub (runtime team, 2022):**
> If the continuation must run back on the original context, then the implementation has little choice but to dutifully post back to the original context. If the antecedent operation completes on the same context, however, there is an optimization in the await infrastructure that will avoid posting back; that just requires that `SynchronizationContext.Current` is the same object as was current when the await was initiated, so if this is the concern, you'd want to make sure that you're not artificially cloning your context object and instead try to have the same SynchronizationContext instance always used for a given target context/thread/environment.

### A.4 On multi-threaded mode design

**SteveSandersonMS (ASP.NET Core team, 2024):**
> We're not open to sacrificing any performance from the single-threaded build. [...] It's OK to have significant behavioral and performance changes when people opt into multithreading, but not when they don't.

**pavelsavara (.NET runtime team, 2024):**
> In last month or so, we switched the implementation of JS interop dispatch from `JSSynchronizationContext` to emscripten internal queue.

**pavelsavara (2024):**
> Our team conclusion so far is, that we wish to disable synchronous `[JSExport]` on MT to avoid broad class of deadlocks.

**pavelsavara (2024) on what throws PNSE in deputy-thread MT mode:**
> any managed code inside of those calls will throw PNSE on blocking `.Wait`, on any virtual FS access, creating new thread, and also on `Console.WriteLine` which all talk to UI thread.

### A.5 On JSPI and future direction

**SteveSandersonMS (2023, #80904) on proposing JSPI adoption:**
> Today, .NET on WebAssembly is unable to implement clean support for cases where .NET APIs need to be synchronous, but they can only be implemented asynchronously inside a browser. Examples: Crypto APIs, Assembly loading (e.g., for lazy loading), where the assembly content must be fetched from the network.

**SteveSandersonMS clarification:**
> we are definitely not willing to [block the browser's UI event loop] [...] we are willing to [pause execution of the .NET code]. Hopefully this clarifies why synchronous XMLHttpRequest isn't a valid solution whereas JSPI could be.

**lewing (2023) on JSPI tracking:**
> This is definitely an interesting area and I think is similar to how Wasi is approaching things. I'm marking it as Future just to indicate there are no specific deliverables in net8.0 not that we won't consider it in that time frame.

**pavelsavara (2023) on JSPI vs multi-threading:**
> if we use JSPI and emscripten `ASYNCIFY=2` it would not block the UI. I'm not sure if JSPI could be used without asyncify. In multi-threading both those scenarios are solvable with more grace.

### A.6 On the COOP/COEP requirement

**danroth27 (ASP.NET Core team, 2024):**
> These headers aren't really specific to multithreading - they are used to enable cross-origin isolation in the browser. It's similar to other general web security features like CORS and CSP.

**SteveSandersonMS (2024):**
> We discussed this in API review and decided it's not clear that ASP.NET Core should have that responsibility. [...] The only reason we're special-casing it for WebAssembly multithreading is to make the on-ramp easier for that specific Blazor WebAssembly feature.

## Appendix B — Map of the scheduling primitives

For the benefit of future Carbide contributors debugging similar issues, here's the map of what calls what in the single-threaded browser-wasm scheduling path (extracted from [scheduling.ts](https://github.com/dotnet/runtime/blob/main/src/mono/browser/runtime/scheduling.ts) and [ThreadPool.Browser.cs](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Threading/ThreadPool.Browser.cs)):

```
User C# code: await Task.Delay(50)
  ↓
Task.Delay(int ms) → constructs DelayPromise with TimerQueueTimer
  ↓
TimerQueue.CreateAppDomainTimer (in single-threaded Browser variant)
  ↓
mono runtime: mono_wasm_schedule_timer → JS callback
  ↓
[JS] SystemJS_ScheduleTimerImpl(shortestDueTimeMs):
   Module.safeSetTimeout(mono_wasm_schedule_timer_tick, shortestDueTimeMs)
  ↓ (event loop tick, 50ms later)
[JS] mono_wasm_schedule_timer_tick:
   cwraps.mono_wasm_execute_timer()
  ↓ (back to managed)
TimerQueue fires → DelayPromise.SetResult()
  ↓
AsyncTaskMethodBuilder sees IsCompleted now true → invokes continuation
  ↓
ThreadPool.Browser.cs: EnsureWorkerRequested():
   MainThreadScheduleBackgroundJob(&BackgroundJobHandler)
  ↓
[JS] setTimeout(0, BackgroundJobHandler)
  ↓ (next event loop tick)
[Managed] BackgroundJobHandler drains ThreadPool work queue
  ↓
State machine's continuation runs — user code resumes
```

All of this happens without any managed-side `Monitor.Wait(INFINITE)`. The "scheduling" is all JS-side `setTimeout` + callback plumbing, not any managed sync primitive.

For `await tcs.Task`:

```
User C# code: await tcs.Task
  ↓
AsyncTaskMethodBuilder.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine)
  ↓
Task.UnsafeOnCompleted(continuation) sets up continuation
  ↓
[return to event loop]
  ↓ (later)
JS callback → [JSExport] → managed-side handler calls tcs.SetResult()
  ↓
Task's continuation list fires → queue work item through ThreadPool (EnsureWorkerRequested → setTimeout(0))
  ↓ (next event loop tick)
[Managed] BackgroundJobHandler → state machine continuation runs
```

Same pattern, no Monitor.Wait.

The question that remains unanswered in public literature: **exactly which managed frame in this chain makes the `Monitor.Wait(INFINITE)` call that trips Carbide's repro?** The investigation report's §9 has the debugging protocol for answering this; this research report confirms that no one else has answered it publicly.

## Appendix C — The ThreadPool.Browser.cs source, relevant extracts, verbatim

For the record, because this is the single most important runtime code path for understanding how Blazor's async "just works" in single-threaded mode:

```csharp
// src/libraries/System.Private.CoreLib/src/System/Threading/ThreadPool.Browser.cs

#if FEATURE_WASM_MANAGED_THREADS
#error when compiled with FEATURE_WASM_MANAGED_THREADS, we use PortableThreadPool.WorkerThread.Browser.Threads.Mono.cs
#endif
// [single-threaded-only from here]

[System.Runtime.Versioning.UnsupportedOSPlatformAttribute("browser")]
public sealed class RegisteredWaitHandle : MarshalByRefObject
{
    internal RegisteredWaitHandle() { }
    internal bool Repeating => false;
    public bool Unregister(WaitHandle? waitObject)
    {
        throw new PlatformNotSupportedException();
    }
}

public static partial class ThreadPool
{
    // Indicates whether the thread pool should yield the thread from the dispatch loop to the runtime periodically
    internal static bool YieldFromDispatchLoop(int currentTickCount) => true;

    private const bool IsWorkerTrackingEnabledInConfig = false;

    private static bool _callbackQueued;

    public static bool SetMaxThreads(int workerThreads, int completionPortThreads)
        => workerThreads == 1 && completionPortThreads == 1;

    public static void GetMaxThreads(out int workerThreads, out int completionPortThreads)
    {
        workerThreads = 1;
        completionPortThreads = 1;
    }
    // ... similar for SetMinThreads, GetMinThreads, GetAvailableThreads ...

    public static int ThreadCount => 1;
    public static long CompletedWorkItemCount => 0;

    [DynamicDependency("BackgroundJobHandler")]
    internal static unsafe void EnsureWorkerRequested()
    {
        if (_callbackQueued)
            return;
        _callbackQueued = true;
#if MONO
        MainThreadScheduleBackgroundJob((void*)(delegate* unmanaged<void>)&BackgroundJobHandler);
#else
        SystemJS_ScheduleBackgroundJob();
#endif
    }

    internal static void NotifyWorkItemProgress() { }
    internal static bool NotifyThreadBlocked() => false;
    internal static void NotifyThreadUnblocked() { }
    // ...
}
```

Key observations:

1. **`ThreadCount => 1`** — there is literally one "worker thread" in single-threaded wasm, which is the main browser thread, which is the mono-managed-thread-1.
2. **`_callbackQueued` is a single bit** — there's at most one pending `BackgroundJobHandler` scheduled at a time. Setting new work while one is scheduled doesn't create a new setTimeout; it just sets the bit and relies on the existing scheduled handler to drain everything on the next tick.
3. **`RegisteredWaitHandle.Unregister` throws PNSE.** This means `RegisteredWaitHandle` APIs (which are used by `ThreadPool.RegisterWaitForSingleObject`) are formally unsupported.
4. **`YieldFromDispatchLoop` returns true always** — the ThreadPool dispatch loop is expected to yield back to the runtime periodically. In single-threaded wasm, this is the mechanism that prevents a batch of ThreadPool work items from monopolizing the main thread indefinitely.

## Appendix D — The scheduling.ts source, verbatim

Quoting [src/mono/browser/runtime/scheduling.ts](https://github.com/dotnet/runtime/blob/main/src/mono/browser/runtime/scheduling.ts):

```typescript
import WasmEnableThreads from "consts:wasmEnableThreads";

import cwraps from "./cwraps";
import { Module, loaderHelpers } from "./globals";
import { forceThreadMemoryViewRefresh } from "./memory";

let spread_timers_maximum = 0;

export function prevent_timer_throttling (): void {
    if (WasmEnableThreads) return;
    if (!loaderHelpers.isChromium) {
        return;
    }

    // this will schedule timers every second for next 6 minutes, it should be called from WebSocket event, to make it work
    const now = new Date().valueOf();
    const desired_reach_time = now + (1000 * 60 * 6);
    const next_reach_time = Math.max(now + 1000, spread_timers_maximum);
    const light_throttling_frequency = 1000;
    for (let schedule = next_reach_time; schedule < desired_reach_time; schedule += light_throttling_frequency) {
        const delay = schedule - now;
        globalThis.setTimeout(prevent_timer_throttling_tick, delay);
    }
    spread_timers_maximum = desired_reach_time;
}

function prevent_timer_throttling_tick () {
    if (WasmEnableThreads) return;
    // ... triggers mono_wasm_execute_timer and mono_background_exec_until_done ...
}

function mono_background_exec_until_done () {
    if (WasmEnableThreads) return;
    lastScheduledBackground = undefined;
    Module.maybeExit();
    if (!loaderHelpers.is_runtime_running()) return;
    try {
        cwraps.mono_background_exec();
    } catch (ex) {
        loaderHelpers.mono_exit(1, ex);
    }
}

let lastScheduledBackground: any = undefined;
export function SystemJS_ScheduleBackgroundJobImpl (): void {
    if (WasmEnableThreads) return;
    if (!lastScheduledBackground) {
        lastScheduledBackground = Module.safeSetTimeout(mono_background_exec_until_done, 0);
    }
}

let lastScheduledTimeoutId: any = undefined;
export function SystemJS_ScheduleTimerImpl (shortestDueTimeMs: number): void {
    if (WasmEnableThreads) return;
    if (lastScheduledTimeoutId) {
        globalThis.clearTimeout(lastScheduledTimeoutId);
        lastScheduledTimeoutId = undefined;
    }
    lastScheduledTimeoutId = Module.safeSetTimeout(mono_wasm_schedule_timer_tick, shortestDueTimeMs);
}

function mono_wasm_schedule_timer_tick () {
    if (WasmEnableThreads) return;
    Module.maybeExit();
    forceThreadMemoryViewRefresh();
    if (!loaderHelpers.is_runtime_running()) return;
    lastScheduledTimeoutId = undefined;
    try {
        cwraps.mono_wasm_execute_timer();
    } catch (ex) {
        loaderHelpers.mono_exit(1, ex);
    }
}
```

Key observations:

1. **Every function short-circuits on `if (WasmEnableThreads) return;`** — this entire JS module is single-threaded-mode code. In multi-threaded mode, scheduling goes through a different module.
2. **`prevent_timer_throttling`** is a Chromium-specific workaround for the browser's tendency to throttle timers on backgrounded tabs. This is unrelated to Carbide's issue but noteworthy as a "real-world quirk of wasm scheduling."
3. **`mono_background_exec`** is the key native-side function — it's what drains the mono-side background job queue. It's called via `setTimeout(0)`, and when it returns, if more work was queued during its execution, the `_callbackQueued = false` reset in `BackgroundJobHandler` (on the managed side) arms another `setTimeout(0)` for the next tick.
4. **Everything is macrotask-based (`setTimeout(0)`)**, not microtask-based. This matters for scheduling ordering: every macrotask gives the browser a chance to paint, handle mouse/keyboard events, etc.

## Appendix E — Summary of what would cleanly fix this "properly" at the runtime level

If a hypothetical .NET 12 were to ship a clean fix for the "cannot wait on monitors" problem in single-threaded mode, the three clean approaches would be:

**(1) JSPI-based `Monitor.Wait(INFINITE)`.** Modify `mono_monitor_wait_internal`'s `DISABLE_THREADS` branch to call a JSPI-suspending JS function instead of failing. The JS function returns a Promise that resolves when the monitor is signaled. Requires Chrome 137+/Firefox 139+/Safari (future). Binary-size cost: negligible.

**(2) ASYNCIFY-based `Monitor.Wait(INFINITE)`.** Same code change as (1) but calling a classic Asyncify-wrapped JS function. Works on all browsers today. Binary-size cost: 15-50% increase for the whole wasm binary.

**(3) Cooperative ThreadPool.** Change the BCL such that `ManualResetEventSlim.Wait()` with `Timeout.Infinite` on single-threaded wasm doesn't fall through to `Monitor.Wait(INFINITE)` at all — instead it yields back to the event loop, lets the pump drain, and then retries. Requires BCL changes, not runtime changes. Probably most surgical fix but the code-review surface is large because many BCL primitives use this pattern.

None of these are on the public roadmap as of April 2026. Option (1) is the most Microsoft-congruent direction (given their JSPI tracking issue) but the scope is scoped to sync-over-async cases, not blocking-wait-in-managed cases.

## Appendix F — If you're the future maintainer wondering "did anyone figure this out?"

If you're reading this from the future looking for the answer to "did the Carbide team figure out exactly what was tripping the PNSE on `await tcs.Task`?", and the answer isn't in this report or the investigation report, check:

- [dotnet/runtime#61308](https://github.com/dotnet/runtime/issues/61308) — if this ever gets a "fixed in X" note, that's the answer.
- Carbide's own git history after 2026-04-20 — the fix, if found, should be documented there.
- Any new dotnet/runtime issues tagged `arch-wasm` + `area-System.Threading` opened by a Carbide Contributor after 2026-04-20.
- JSPI adoption announcements in .NET 11+ release notes — if `WasmEnableJSPI` or similar property becomes available, that may obsolete this whole problem class.

## Key URLs (priority reading order)

1. [**dotnet/runtime#61308**](https://github.com/dotnet/runtime/issues/61308) — the open, untriaged, 4-year-old tracking issue for this exact error. Read the comment chain for the team's position.
2. [**dotnet/runtime#61381**](https://github.com/dotnet/runtime/issues/61381) — lewing's "we decided it was better to Fail all .Wait() calls than to have some fail seemingly randomly" quote; the single most important primary source for the runtime team's posture.
3. [**src/mono/mono/metadata/monitor.c**](https://github.com/dotnet/runtime/blob/main/src/mono/mono/metadata/monitor.c) — the `DISABLE_THREADS` branch that emits the error message.
4. [**src/mono/browser/runtime/scheduling.ts**](https://github.com/dotnet/runtime/blob/main/src/mono/browser/runtime/scheduling.ts) — the single-threaded wasm `setTimeout`-based scheduling pump for `Task.Delay` and ThreadPool work items.
5. [**src/libraries/System.Private.CoreLib/src/System/Threading/ThreadPool.Browser.cs**](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Threading/ThreadPool.Browser.cs) — how the single-threaded ThreadPool drains work items via `BackgroundJobHandler`.
6. [**src/Components/WebAssembly/WebAssembly/src/Rendering/WebAssemblyDispatcher.cs**](https://github.com/dotnet/aspnetcore/blob/main/src/Components/WebAssembly/WebAssembly/src/Rendering/WebAssemblyDispatcher.cs) — Blazor's multi-threaded dispatcher; comment confirms it's multi-threaded-mode-only.
7. [**src/libraries/System.Runtime.InteropServices.JavaScript/src/System/Runtime/InteropServices/JavaScript/JSSynchronizationContext.cs**](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Runtime.InteropServices.JavaScript/src/System/Runtime/InteropServices/JavaScript/JSSynchronizationContext.cs) — the multi-threaded-only `JSSynchronizationContext`. Opens with `#if FEATURE_WASM_MANAGED_THREADS`, confirming it doesn't exist in single-threaded mode.
8. [**dotnet/runtime#69409**](https://github.com/dotnet/runtime/issues/69409) — API proposal thread for the sync context; kg + stephentoub discussion of how custom sync context interacts with continuation dispatch; directly relevant to the Carbide-vs-Blazor discrepancy in §9.
9. [**dotnet/runtime#80904**](https://github.com/dotnet/runtime/issues/80904) — the only JSPI tracking issue on dotnet/runtime, milestoned "Future", scoped narrowly to sync-over-async crypto/assembly-loading (NOT "make real async work in single-threaded").
10. [**dotnet/aspnetcore#54365**](https://github.com/dotnet/aspnetcore/issues/54365) — tracking issue for Blazor-itself-on-multithreaded-runtime, still open, contains SteveSandersonMS's "not open to sacrificing any performance from the single-threaded build" posture and pavelsavara's design notes on the current multi-threaded dispatch.
11. [**pavelsavara's multi-threading design gist**](https://gist.github.com/pavelsavara/c81ef3a9e4000d67f49ddb0f1b1c2284) — Pavel Savara's own notes on the single-threaded vs multi-threaded tradeoffs, especially around JS interop and spin-wait.
12. [**Meziantou's "Don't freeze UI" post**](https://www.meziantou.net/don-t-freeze-ui-while-executing-cpu-intensive-work-in-blazor-webassembly.htm) — canonical community guidance on what DOES work in single-threaded Blazor WASM (`await Task.Yield()`, `await Task.Delay(1)`). Confirms by implication that these DO work, which means the runtime-level suspension path is functional.
13. [**StephenCleary/AsyncEx#220**](https://github.com/StephenCleary/AsyncEx/issues/220) — Stephen Cleary's definitive "there is no other choice" statement on single-threaded Blazor async.
14. [**V8 JSPI blog post**](https://v8.dev/blog/jspi) — canonical explanation of JSPI, including the crucial "it is not permitted to cause JavaScript code to be suspended by using JSPI" constraint.
15. [**Uno Platform "State of WebAssembly 2025/2026"**](https://platform.uno/blog/the-state-of-webassembly-2025-2026/) — current roadmap view for .NET-wasm from the most-invested third-party consumer, including CoreCLR-on-wasm landing in .NET 12 (2027).
