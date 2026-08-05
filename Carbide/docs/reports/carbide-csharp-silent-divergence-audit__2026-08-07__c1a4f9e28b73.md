# Carbide — C# silent-divergence audit

Documentation in this directory is licensed under the repository's [Apache License 2.0](../../../LICENSE), with copyright held collectively by Carbide Contributors.

- Created (UTC): 2026-08-07
- Status: Informational; findings 1–3 are fixed, 4–9 are open
- Scope: the C# half of `@carbide/core` — `Services/`, `Terminal/`, `CompilationInterop.cs`, and the forked `core-bcl/System.Console`

## Why this audit exists

Nine earlier passes over Carbide's TypeScript half found 22 defects, every one of a single
shape: the code does not throw, the tests stay green, and the failure surfaces far from its
cause. The C# half had been skipped because probing it one surface at a time is expensive.
This pass audited six surfaces in parallel and then had every candidate finding
adversarially refuted by an independent reader.

**27 candidates → 16 refuted → 11 survived**, which deduplicate to **8 distinct defects**
(three were reported twice, from two files each). A ninth defect — finding 3 — was found
while building a fixture for one of the others.

The refutation step earned its keep. Among the 16 killed: a claim that `build` discards
compiler warnings (it matches the documented contract), a claim that `Environment.ExitCode`
is ignored (true, but it is not part of the hosting contract and honouring it would be
wrong here), and a claim about `Console.CursorSize` that misstated what .NET itself does —
the "expected behaviour" was Windows-only, which the verifier established by dumping IL
from the shipped reference assemblies.

## Fixed

### 1. A user-declared synchronous `Main` was displaced by an unrelated async sibling (high)

`Services/ProjectCompiler.cs`, `Services/AssemblyRunner.cs`

T2.1 added a substitution so Roslyn's *synthesised* wrapper for `async Task Main` — which
deadlocks on single-threaded Mono-WASM — is bypassed in favour of the underlying async
method. The sibling search ran for every non-awaitable entry point, including a genuine
`static void Main`. One unrelated helper was enough:

```csharp
public static void Main() { Console.WriteLine("main-ok"); }
public static async Task WarmUpAsync() { ... }   // this ran instead
```

Reproduced end to end through the public API: `stdOut` was `"WARMUP-RAN"`, `Main` never ran,
`success: true`, no diagnostic. The `int Main` + `Task<int>` variant returned the helper's
value as the process exit code and printed nothing. `carbide build` emitted a PE whose entry
point was `Main` while `carbide run` on identical sources executed something else.

Fixed by restricting the substitution to compiler-generated entry points, which is what the
code's own comment already said it was for. Candidate ordering is pinned too —
`Type.GetMethods` order is explicitly unspecified.

### 2. `CarbideConsole` and stock `Console` disagreed about terminal size (high)

`Terminal/TerminalInputState.cs`, `Terminal/CarbideTerminalInterop.cs`

The TS layer pushes initial geometry with a priming `NotifyResize` *before* calling
`RunInteractiveAsync` — before the run's `TerminalInputState` exists, so `TryGet` returned
null and the value was dropped. `Cols`/`Rows` kept their 80×24 placeholders for the whole
run unless the user happened to resize. Meanwhile the T3-forked `Console.WindowWidth` asks
xterm directly and was correct. In a 120×40 terminal, `CarbideConsole.WindowWidth` returned
80 and `Console.WindowWidth` returned 120 — two APIs documented as equivalent, disagreeing
about the same terminal.

Every interactive fixture in the suite builds its mock at exactly 80×24, which is also the
fallback, so the suite structurally could not tell "delivered" from "dropped".
`test/browser/interactive-initial-geometry.{html,spec.mjs}` now uses 120×40 and asserts both
APIs agree. Fixed by seeding `TerminalInputState` from the same live bridge the fork uses,
which removes the ordering dependency rather than trying to sequence the two calls.

### 3. Shift+Tab decoded as Shift+F2 (medium)

`Terminal/XtermTerminfo.cs`

The terminfo shim omitted back-tab (`kcbt`, `CSI Z`), so xterm.js's Shift+Tab fell through
to `KeyParser`'s SCO-style single-letter branch where `Z` maps to F2. Reverse-tab-order
navigation in a user's TUI silently fired whatever F2 was bound to — a wrong key rather than
an unrecognised one.

## Open

These survived refutation but are not yet fixed. All six live in the browser-interactive
path, where verifying a change needs browser-level coverage that does not exist yet for the
specific scenarios below. They are recorded here with reproduction steps so the work is
triageable rather than lost.

### 4. Every interactive run after the first writes into the first run's terminal (high)

`src/ts/terminal/bridge.ts`

Found while building a fixture for finding 8, not by the audit itself; the two-run setup that
finding needed exposed it immediately. **Diagnosed precisely, attempted, and reverted** — the
diagnosis is solid, the fix is not.

Mono-WASM resolves a `[JSImport("globalThis.Carbide.Terminal.write")]` binding once and
caches the *function object*. `installBridge` publishes a fresh function per run, so the C#
side keeps calling the first run's closure forever.

*Demonstrated:* three programs run in sequence on one session, each with its own terminal,
reading every terminal at the end. All three lines land in terminal A; B and C stay empty.
Every `RunResult.stdOut` is correct throughout, because the C# side tees output into a
`StringBuilder` independently of the bridge — which is why no existing test notices. Reading
each terminal right after its own run (the natural way to write such a test) also hides it.

*Impact:* a browser IDE shows a working first tab and permanently silent ones after it.

*Attempted fix, and why it is not committed:* replacing the per-run functions with stable
trampolines installed once at boot, delegating to a swappable sink, made the routing correct —
a three-run fixture went green, with each program's output in its own terminal.

It was reverted for a reason that did not survive scrutiny, and the honest version matters for
whoever picks this up. During that testing the browser suite slowed sharply (the ten-line
`interactive-hello` fixture went from ~7 s to 21–84 s) and `interactive-beep` failed once, so
the change looked like a regression on the per-write hot path. But after reverting, the same
fixtures still took 28 s and 56 s on the same machine — so the slowdown was environmental
(sustained load from a long session), **not** attributable to the fix, and the single beep
failure did not reproduce. The trampoline approach may well be correct.

It is left out of the tree only because a change to the runtime bridge's hot path should be
merged on evidence, and the evidence available here was confounded. Re-attempt on a quiet
machine: verify the three-run routing, then compare `interactive-hello` timing against a
baseline measured in the same session. Worth checking first whether Mono can be made to
re-resolve a JSImport binding per run, or whether the sink swap belongs on the C# side of the
boundary — either would avoid the extra JS indirection entirely.

### 5. `DisposeSession` does not tear down an in-flight interactive run (high)

`Services/SessionSolutions.cs:63`

`DisposeSession` removes the project entries and calls `ProjectCompiler.Dispose()`, which
only disposes the Roslyn workspace. It never touches the run's `TerminalInputState`, so a
program parked in `await Console.In.ReadLineAsync()` is never released.

*Repro:* take `test/browser/interactive-dispose-midrun.html` and replace
`handle.dispose()` with `session.shutdown()`, keeping the `await handle.exitPromise`.

*Impact:* in a browser IDE, Reset/Close while a program sits at a prompt appears to succeed,
but anything keyed off `exitPromise` — a spinner, a "stopped" badge, a queued next run —
hangs forever with no error. Repeating the cycle grows `TerminalInputState.s_registry` and
keeps every abandoned run's state alive.

*Note:* `handle.dispose()` (the documented teardown) works correctly; this is the
`session.shutdown()` path only.

### 6. EOF is discarded in key mode, so `ReadKeyAsync` hot-spins after dispose (high)

`Terminal/BrowserTerminalReader.cs:86`

A program suspended in `CarbideConsole.ReadKeyAsync()` with no cancellation token, when the
host disposes: the EOF signal reaches the reader but the key-mode path discards it instead
of completing the pending read.

*Impact:* worse than a hang. Mono-WASM browser is single-threaded, so a synchronous C# spin
starves the JS event loop entirely — xterm stops rendering, the page stops responding to
clicks, and `dispose()`'s own `await exitPromise` can never resolve. The user sees a frozen
tab, not an error.

### 7. Only `Console.Out` is flushed before an input suspension (medium)

`Terminal/BrowserTerminalReader.cs:185`

Both streams share a time-windowed buffered writer, but the pre-suspension flush covers
`Console.Out` only. A newline-less prompt written to `Console.Error` within the flush window
stays buffered while the program blocks on input.

*Impact:* the terminal shows nothing while the program is in fact waiting for a line. Typing
blind works, and the prompt then appears retroactively on the next stderr write — so output
arrives out of order relative to the input that answered it.

### 8. `Console.CancelKeyPress` handlers are never unregistered between runs (high)

`core-bcl/System.Console/src/Console.cs:440`

The stock event's handler list is process-global in the fork and is never cleared at run
teardown. A second `runInteractive` on the same page inherits the first run's handlers.

*Impact:* a previous run's handler can set `e.Cancel = true`, so Ctrl+C silently stops
working in later runs — the program keeps going and `RunCancellationToken` never trips.
Demonstrated: a two-run fixture where run A registers a vetoing handler and run B is sent
Ctrl+C never terminates, so the page hangs rather than merely misbehaving. The fixture is not
committed precisely because it hangs; fixing this needs an internal reset entry point on the
fork, called from `RunInteractiveAsync`'s finally alongside the existing
`AppContext.SetData("Carbide.InteractiveBridge", false)`.
Output from a program the user already finished with also appears in an unrelated run's
terminal and in its `RunResult.stdOut`.

### 9. Handle-level writes corrupt multi-byte characters split across calls (medium)

`core-bcl/System.Console/src/ConsolePal.Browser.cs:472`

`CarbideStdWriteStream` decodes each `Write` with a stateless `Encoding.UTF8.GetString`, so
a UTF-8 sequence spanning two calls is decoded as two invalid fragments.

*Repro:* `Console.OpenStandardOutput()`, then write the bytes of `"café"` in two calls that
split `é` (`C3 A9`).

*Impact:* non-ASCII characters become replacement glyphs at predictable offsets in long
output. Ordinary `Console.Write` goes through the `StreamWriter` path and is unaffected,
which makes the corruption look like a data problem rather than an encoding one. A stateful
`Decoder` fixes it.

## Method note

The value came from two things, in this order. First, a defect *class* stated concretely
enough to search for — "accepted then never read", "succeeds while contributing nothing",
"confidently wrong where refusing is correct" — rather than "look for bugs". Second, an
adversarial verification pass instructed to default to refuted. Sixteen of twenty-seven
candidates did not survive it, and several were plausible enough that acting on them would
have meant rewriting correct code.
