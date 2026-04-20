# Carbide T2.1 — empirical pinpoint (what specifically passes vs fails, and why)

- Created (UTC): 2026-04-20T18:51:01Z
- Repository HEAD: e76335fb50d731aaf065ed253db7e1cc2f0a997a

Status: **empirical findings, superseding the follow-up research's "some suspended awaits work" framing**. Produced by running paired probes (same C# source, different JS-side timing) via `packages/core/test/browser/t21-matrix-probe.html` against the current `HEAD` runtime.

Reconciles and resolves the apparent contradiction between:

- The [original T2.1 investigation report](carbide-T21-investigation-report__2026-04-20__17-11-37-000000.md)'s claim that any await that genuinely suspends trips PNSE.
- The [follow-up research report](carbide-T21-follow-up-research-report__2026-04-20__18-20-09__6554172dc064.md)'s observation that the `interactive-readline` Playwright fixture passes, disproving the blanket claim.

Both are empirically correct **as stated**. They describe *different test setups*, and the difference between the setups is the single variable that controls pass vs fail.

## 1. The one-line rule

> An `await` inside Carbide's run path works **iff the Task is already complete at the moment the state machine inspects `awaiter.IsCompleted`**. If the state machine actually has to suspend and wait for a later completion, it trips `PlatformNotSupportedException: Cannot wait on monitors on this runtime`, regardless of what produces the completion.

This is a sharpening of the original T2.1 report's wording — "any await that genuinely suspends" — to remove any ambiguity about what "genuinely suspends" means. It means **`awaiter.IsCompleted == false` at the syntactic `await` site**.

## 2. The paired probes

Probe fixture: [`packages/core/test/browser/t21-matrix-probe.html`](../../packages/core/test/browser/t21-matrix-probe.html). Each probe pairs one C# source with one JS-side action; the difference between paired probes is when the JS side triggers Task completion.

All ran against `HEAD = e76335fb5` with the default T2 + T3 wiring (`CarbideSyncContext` installed, T3 forked `System.Console.dll` overlaid).

| Probe | Source shape | Completion timing | Result |
|---|---|---|---|
| PAIR-A-early | `await Console.In.ReadLineAsync()` | `deliverKeys` fires 2 rAF ticks after `runInteractive` — *before* user code reaches the await | **PASS** |
| PAIR-A-late | Identical source | `deliverKeys` fires only after `"ready\n"` appears in terminal — *after* user code reaches the await | **FAIL (PNSE)** |
| PAIR-B-early-preset | `var tcs = new TCS<int>(None); tcs.SetResult(42); await tcs.Task;` | TCS completed *before* the await statement | **PASS** |
| PAIR-B-late-timer | `var tcs = new TCS<int>(None); _ = Task.Delay(50).ContinueWith(_ => tcs.TrySetResult(42)); await tcs.Task;` | TCS completed by a `Task.Delay` timer that fires *after* the await suspends | **FAIL (PNSE)** |
| PAIR-C-yield | `await Task.Yield();` | n/a — `YieldAwaiter.OnCompleted` reschedules continuation via `SC.Post` on the *same* tick | **PASS** |

Key observations from the table:

- **PAIR-A** isolates timing. Same source, same `Console.In.ReadLineAsync()` path (which is the backing of the existing passing `interactive-readline.spec.mjs` fixture). Whether it passes or fails is entirely determined by when the JS side delivers input.
- **PAIR-B** isolates completion mechanism. Pre-set TCS passes; the same TCS completed by a timer fails. Nothing about the completion path is involved — only whether completion happened before the `await` inspects `IsCompleted`.
- **PAIR-C** shows `Task.Yield()` works. That's because `YieldAwaiter.IsCompleted` returns `false` (always), but `OnCompleted` immediately posts the continuation via `SynchronizationContext.Post`, which `CarbideSyncContext` implements as an **inline** call. So the state machine *never actually suspends* in the pausing-and-waiting sense; control is just re-entered from inside `Post`.

## 3. Why `interactive-readline.spec.mjs` passes in Playwright

Reading [`test/browser/interactive-readline.html`](../../packages/core/test/browser/interactive-readline.html) closely:

```js
const handle = project.runInteractive({ terminal });

// Let Carbide fire `Console.Write("name? ")` and the await to begin. Two rAF ticks
// is plenty on a headless Chromium.
await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
// Simulate typing "abc" then Enter.
terminal.deliverKeys("abc\r");

const runResult = await handle.exitPromise;
```

The comment in that file — "*Let Carbide fire `Console.Write("name? ")` and the await to begin*" — is misleading. The `deliverKeys` call fires only 2 rAF ticks after `runInteractive` starts. On a headless Chromium, the Mono-WASM runtime has not finished booting in 2 rAF ticks; it's still loading assemblies, JITing, and hasn't reached the user's `await Console.In.ReadLineAsync()` yet. By the time user code actually evaluates the await, the JSExport for `DeliverStdIn` has already been called and the `BrowserTerminalReader`'s internal `_lines` queue has "abc" waiting.

The `BrowserTerminalReader.ReadLineAsync(ct)` implementation then takes the fast path:

```csharp
public override ValueTask<string?> ReadLineAsync(CancellationToken ct)
{
    if (_lines.Count > 0)
    {
        return new ValueTask<string?>(_lines.Dequeue());  // <-- synchronous
    }
    ...
}
```

— it returns a synchronously-completed `ValueTask<string?>`. The user's `await` therefore sees `awaiter.IsCompleted == true` and skips the whole suspension path. No PNSE.

**`interactive-readline.spec.mjs` passes because its 2-rAF delay is a race that the runtime's boot-and-reach-await time reliably loses, *not* because Carbide supports genuine await suspension on single-threaded browser WASM.**

Proof: the `PAIR-A-late` probe uses the exact same user source and deliberately waits for `"ready\n"` (proof of "await has begun") before delivering keys. That flip from implicit-early to explicit-late flips the result from PASS to FAIL, holding everything else constant.

## 4. Reconciliation with the two earlier reports

### Original T2.1 investigation ([link](carbide-T21-investigation-report__2026-04-20__17-11-37-000000.md))

The narrow technical claim — *"any C# `await` inside `Project.runInteractive` that **actually suspends** trips PNSE"* — is **correct**. The follow-up report's counterexample does not hold up under probe: `interactive-readline` is a synchronous-fast-path case that doesn't exercise real suspension.

### Follow-up research ([link](carbide-T21-follow-up-research-report__2026-04-20__18-20-09__6554172dc064.md))

Two of its three claims survive:

- Blocking waits (`.Wait()`, `.Result`) really are fundamentally unsupported. ✅
- Upstream .NET has a working non-blocking async story on single-threaded browser WASM. ✅ (confirmed by the `scheduling.ts` / `JSSynchronizationContext` sources.)

The third claim — that Carbide's problem is narrower than the original report because a suspended-await case already works — is **disproved by these probes**. The `interactive-readline` case is synchronously pre-completed at the await; it is not a suspended-await success.

The simpler-workaround recommendations in the follow-up report are still useful as staging advice (ship the line-oriented subset, scope expectations, etc.), but they are policy recommendations, not a path to making suspended awaits work.

## 5. What this means for the fix options

All five original options from the T2.1 investigation report (A–E) remain as-framed. The empirical pinpoint tightens what "fixing T2.1" would have to achieve:

> **The fix must make `awaiter.IsCompleted == false` followed by a later completion resume correctly, without tripping `Monitor.Wait(INFINITE)` in the state-machine suspension machinery.**

Against that criterion:

- **Option A** (rewrite Carbide's run path to never genuinely suspend) now has a concrete definition of "never genuinely suspend": user code must only `await` Tasks that are already complete. In practice this means rewriting all `CarbideConsole.*Async` methods as pollable sync APIs driven by a JS-side ticking loop, and forbidding user code from using any API (including `Task.Delay`, `HttpClient.*Async`, any library doing real async I/O) that completes on the main thread after the await site. This is restrictive but a lot of REPL-shaped programs can be rewritten to fit.
- **Option B** (multi-threaded runtime) still works because multi-threaded Mono-WASM installs `JSSynchronizationContext`, which routes continuations through a proper pump.
- **Option C** (patch `monitor.c` + ASYNCIFY) would specifically target the `Monitor.Wait(INFINITE)` fallback that's the proximate cause.
- **Option D** (coroutine source-rewriter) = Option A expressed at compile time.
- **Option E** (document-and-scope) is honest about what we can ship.

The investigator's new "F.next" lead (debugger-break on the PNSE throw site to find the exact BCL frame that calls `Monitor.Wait(INFINITE)`) is still the highest-value next experiment. With the empirical pinpoint in hand, it becomes: *in the suspension path of `AsyncTaskMethodBuilder.AwaitUnsafeOnCompleted` when `IsCompleted == false`, which code path eventually calls `Monitor.Wait(INFINITE)` on `DISABLE_THREADS` runtime, and why does stock Blazor WASM not hit it when running the equivalent `await Task.Delay(50)` code?*

## 6. Passing vs failing suspended-await cases in Carbide today

Updating the table from the follow-up report with this sharper lens:

| Scenario | Before pinpoint | After pinpoint |
|---|---|---|
| `interactive-readline` fixture | "Real suspended await already works" | **Synchronous fast-path due to race** — input delivered before user code reaches the await |
| `interactive-resize` fixture | Fails fast with PNSE | Late delivery → real suspension → PNSE |
| `interactive-ctrlc` fixture | Fails fast with PNSE | Late delivery → real suspension → PNSE |
| `interactive-readkey` fixture | "Appears to hang" | Different failure mode — `async Task<>` wrapper's internal `await` on the key-waiter TCS suspends before any keystroke can arrive, then resumption attempts trip PNSE or silently fail to resume |
| `await Task.Delay(50)` in user code | Fails fast with PNSE | Timer completes after suspension → PNSE |
| `await Task.Yield()` in user code | Not previously tested | PASSES — `YieldAwaiter` never sets `IsCompleted` but `OnCompleted` posts via `CarbideSyncContext.Post` synchronously inline |

There is now a single consistent predictor: whether the awaited Task is already complete at the `await` site, or equivalently whether the awaiter's `OnCompleted` posts synchronously via an inline SC.Post (Yield). If yes, pass. If no, fail.

## 7. Probe reproducer

All probes above can be reproduced against any Carbide HEAD:

```bash
# terminal 1 — serve packages/core
cd packages/core
node test/browser/static-server.mjs

# terminal 2 — run a single probe via the runner
cd ../../  # back to carbide repo root
node docs/reports/artifacts/carbide-gh-T21-artifact/scripts/fixture-probe.mjs \
    "t21-matrix-probe.html?probe=PAIR-A-late"
```

Valid `probe=` values live in `packages/core/test/browser/t21-matrix-probe.html` under `window.__T21_PROBES__`. Add a new one, rebuild the TS side (`npm run build:ts`) is **not** needed since probes are plain HTML; but any C# runtime change needs `dotnet publish -c Release` + overlay step as usual.

## 8. Recommended next-step framing

For anyone picking this up further:

1. **Stop claiming a blanket "any await works" or a blanket "no await works".** Neither is accurate. The accurate claim is the §1 rule.
2. **Do not use `interactive-readline.spec.mjs` as evidence that suspended-await works.** It's a synchronous-fast-path test that uses a race to dodge real suspension.
3. **If you want a genuinely-suspends test fixture, write it with `await waitForText(terminal, "ready\n")` before the JS-side deliver.** That's the probe harness pattern — it forces the user-code await to suspend first.
4. **The next meaningful investigative step** is the in-browser debugger break on the `PlatformNotSupportedException` throw site, with Mono-WASM debug symbols loaded, to walk up the C# stack from the `Monitor.Wait` and find exactly which BCL code path (likely inside `AsyncTaskMethodBuilder`, `AsyncStateMachineBox<TStateMachine>`, or `ManualResetEventSlim.Wait`) routes through the trap. That will answer whether Carbide is violating a runtime assumption or whether the runtime itself has no single-threaded-browser path for this particular completion shape.
