# Carbide — C# silent-divergence audit

Documentation in this directory is licensed under the repository's [Apache License 2.0](../../../LICENSE), with copyright held collectively by Carbide Contributors.

- Created (UTC): 2026-08-07
- Status: Informational; all nine findings are fixed and pinned by tests
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

## Fixed in the follow-up pass

The six below all live in the browser-interactive path, which is why they were deferred:
verifying any of them needs browser-level coverage that did not exist for these specific
scenarios. Each now has a fixture under `test/browser/`, and each fixture was checked in both
directions — confirmed failing against the unfixed code, then passing against the fix. That
step earned its keep twice. It caught a fixture that could not have distinguished the two
states, and it caught a first attempt at finding 9 that was wrong in a way the "fixed" output
still looked plausible under.

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

*Fixed* with the trampoline approach the first attempt had reached and then reverted: the
`Carbide.Terminal` members are module-level functions installed once, whose identities never
change, and `installBridge`/`uninstallBridge` swap a module-level `activeSink` behind them.

The earlier revert was recorded as provisional, on the grounds that the suite had slowed
sharply during that testing (the ten-line `interactive-hello` fixture went from ~7 s to
21–84 s) while `interactive-beep` failed once — but that the same fixtures stayed slow *after*
reverting, so the evidence was confounded rather than damning. Re-measured on a quiet machine,
that reading holds: the full 24-test browser suite runs in 1.7 minutes with the trampolines in,
`interactive-hello` at 8.2 s, and `interactive-beep` passes. The slowdown was environmental.

`test/browser/interactive-multi-run-routing.{html,spec.mjs}` runs three programs in sequence,
each with its own terminal, and reads every terminal only at the end. Against the unfixed
bridge it reports all three labels in terminal A and empty strings for B and C.

### 5. `DisposeSession` does not tear down an in-flight interactive run (high)

`Services/SessionSolutions.cs:63`

`DisposeSession` removes the project entries and calls `ProjectCompiler.Dispose()`, which
only disposes the Roslyn workspace. It never touches the run's `TerminalInputState`, so a
program parked in `await Console.In.ReadLineAsync()` is never released.

*Impact:* in a browser IDE, Reset/Close while a program sits at a prompt appears to succeed,
but anything keyed off `exitPromise` — a spinner, a "stopped" badge, a queued next run —
hangs forever with no error. Repeating the cycle grows `TerminalInputState.s_registry` and
keeps every abandoned run's state alive.

*Note:* `handle.dispose()` (the documented teardown) worked correctly; this was the
`session.shutdown()` path only.

*Fixed* by routing each of the session's projects through `DisposeInteractive` before
dropping it — the same call `handle.dispose()` already made. Shutting a session down has to do
at least as much as disposing one handle.
`test/browser/interactive-shutdown-midrun.{html,spec.mjs}` pins it; unfixed, the fixture never
reaches a terminal state at all.

### 6. EOF is discarded in key mode, so `ReadKeyAsync` hot-spins after dispose (high)

`Terminal/BrowserTerminalReader.cs:86`

A program suspended in `CarbideConsole.ReadKeyAsync()` with no cancellation token, when the
host disposes: the EOF signal reaches the reader but the key-mode path discards it instead
of completing the pending read.

*Impact:* worse than a hang. Mono-WASM browser is single-threaded, so a synchronous C# spin
starves the JS event loop entirely — xterm stops rendering, the page stops responding to
clicks, and `dispose()`'s own `await exitPromise` can never resolve. The user sees a frozen
tab, not an error.

*Fixed* by giving the reader an `IsClosed` surface and having `ReadKeyAsync`'s loop end the
read when the buffer is drained and no further byte can arrive. `ReadLineAsync` signals the
same condition by returning null; `ConsoleKeyInfo` has no such value, and a default key would
hand user code a keystroke nobody pressed — so it throws instead, reporting
`OperationCanceledException` when the run token has tripped (every teardown path cancels
before completing the reader) and `InvalidOperationException` when input merely ran out, which
is what stock `Console.ReadKey` reports.
`test/browser/interactive-readkey-dispose.{html,spec.mjs}` disposes a run parked in
`ReadKeyAsync` and then runs a second program on the same page — reaching that second run at
all is the liveness proof, since a spinning loop would have pinned the only thread.

### 7. Only `Console.Out` is flushed before an input suspension (medium)

`Terminal/BrowserTerminalReader.cs:185`

Both streams share a time-windowed buffered writer, but the pre-suspension flush covers
`Console.Out` only. A newline-less prompt written to `Console.Error` within the flush window
stays buffered while the program blocks on input.

*Impact:* the terminal shows nothing while the program is in fact waiting for a line. Typing
blind works, and the prompt then appears retroactively on the next stderr write — so output
arrives out of order relative to the input that answered it.

*Fixed* by flushing both streams from one helper, used by both suspension points.
`test/browser/interactive-stderr-prompt-flush.{html,spec.mjs}` writes
`Console.Error.WriteLine` immediately before the prompt so the prompt is definitely inside the
writer's 32 ms window — without that the window has usually already elapsed and the write
flushes by luck, which would have made the fixture pass either way.

### 8. `Console.CancelKeyPress` handlers are never unregistered between runs (high)

`core-bcl/System.Console/src/Console.cs:440`

The stock event's handler list is process-global in the fork and is never cleared at run
teardown. A second `runInteractive` on the same page inherits the first run's handlers.

*Impact:* a previous run's handler can set `e.Cancel = true`, so Ctrl+C silently stops
working in later runs — the program keeps going and `RunCancellationToken` never trips.

*Fixed* with an internal `Console.ResetCancelKeyPress()` on the fork, reached by reflection
from `TerminalInputState` (which already owns the reflection into the fork for
`HandleCancelKeyPress`) and called from the finally of all three run paths — the
non-interactive ones register handlers into the same static chain, so resetting only the
interactive one would have left the leak reachable.
`test/browser/interactive-cancelkeypress-reset.{html,spec.mjs}` pins it. The earlier attempt
at this fixture was left out because it hung; this one gives run B a bounded
`DelayAsync(5000)`, so the unfixed behaviour surfaces as `cancelled=False` after the delay
rather than as a hung page.

### 9. Handle-level writes corrupt multi-byte characters split across calls (medium)

`core-bcl/System.Console/src/ConsolePal.Browser.cs:472`

`CarbideStdWriteStream` decodes each `Write` with a stateless `Encoding.UTF8.GetString`, so
a UTF-8 sequence spanning two calls is decoded as two invalid fragments.

*Repro:* `Console.OpenStandardOutput()`, then write the bytes of `"café"` in two calls that
split `é` (`C3 A9`).

*Impact:* non-ASCII characters become replacement glyphs at predictable offsets in long
output. Ordinary `Console.Write` goes through the `StreamWriter` path and is unaffected,
which makes the corruption look like a data problem rather than an encoding one.

*Fixed* with a stateful `Decoder` held on the stream, plus a `flush: true` pass at `Dispose`
so a dangling partial sequence surfaces as U+FFFD instead of vanishing. `Flush()` deliberately
does *not* flush the decoder: a caller may flush after every write, and forcing it there would
corrupt a legitimately split sequence.

The first attempt at this fix was subtly wrong in a way worth recording, because its output
looked like a plausible bug rather than like a broken fix. It returned early when
`GetCharCount` reported zero characters — but `GetCharCount` only *reports*; `GetChars` is
what consumes the bytes and updates the carry-over state. Skipping `GetChars` discarded the
lead bytes, so the byte completing each sequence arrived alone and decoded as U+FFFD: exactly
`n-1` replacement characters per `n`-byte character, which reads as "the decoder isn't
stateful" rather than "the fix drops bytes". A probe fixture that printed the actual decoded
bytes, not just a pass/fail, is what separated the two.

`test/browser/interactive-split-utf8.{html,spec.mjs}` writes a payload mixing 2-, 3- and
4-byte sequences one byte per call, so every sequence is split at every boundary.

## Method note

The value came from two things, in this order. First, a defect *class* stated concretely
enough to search for — "accepted then never read", "succeeds while contributing nothing",
"confidently wrong where refusing is correct" — rather than "look for bugs". Second, an
adversarial verification pass instructed to default to refuted. Sixteen of twenty-seven
candidates did not survive it, and several were plausible enough that acting on them would
have meant rewriting correct code.
