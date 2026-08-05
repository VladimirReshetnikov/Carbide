// T1+T2+T3 — installs the JS-side half of the interactive terminal bridge on
// `globalThis.Carbide.Terminal`. The C# side reaches this via
// `[JSImport("globalThis.Carbide.Terminal.{write|writeErr|setKeyMode|setTreatControlCAsInput|getCols|getRows|beep}")]`.
// T1 wires the output side (write/writeErr). T2 adds setKeyMode and setTreatControlCAsInput
// so the line editor can be toggled + the Ctrl+C policy can be propagated from C# → JS
// without a round-trip per keystroke. T3 adds getCols/getRows so the forked
// System.Console.dll's synchronous WindowWidth/WindowHeight getters can answer without a
// NotifyResize round-trip. T3.1 adds `beep` for `Console.Beep(freq, duration)`.

import type { XtermTerminalLike } from "../types.js";

/**
 * Lazy, shared AudioContext. Created on first beep so environments without Web Audio
 * (Node, JSDOM, some embedded WebViews) don't fail at bridge install time. Reused across
 * install/uninstall cycles so the one-time user-gesture unlock persists.
 */
let sharedAudioContext: AudioContext | null = null;
let audioContextUnavailable = false;

function getAudioContext(): AudioContext | null {
    if (sharedAudioContext !== null) return sharedAudioContext;
    if (audioContextUnavailable) return null;
    const g = globalThis as Record<string, unknown>;
    const Ctor = (g.AudioContext ?? g.webkitAudioContext) as (new () => AudioContext) | undefined;
    if (!Ctor) {
        audioContextUnavailable = true;
        return null;
    }
    try {
        sharedAudioContext = new Ctor();
        return sharedAudioContext;
    } catch {
        audioContextUnavailable = true;
        return null;
    }
}

/**
 * Audio-clock time at which the next queued beep should start. Tracks the end of the
 * most recently scheduled tone so that back-to-back `Console.Beep` calls play in
 * sequence instead of overlapping — otherwise `Beep(440, 500); Beep(880, 500);` from
 * fire-and-forget C# would emit both tones simultaneously. Resets to "now" when the
 * last-scheduled-end has already passed.
 */
let beepQueueEndTime = 0;

/**
 * Play a single sine tone at `frequency` Hz for `durationMs` milliseconds, then invoke
 * the callback at the end of playback. Multiple concurrent `beep` calls queue back-to-
 * back in audio time so the caller's intended sequence is preserved even though this
 * function returns immediately. Silent but fast-completes when Web Audio is unavailable
 * (Node tests) or the context is still suspended waiting on a user gesture (browser
 * autoplay policy). Uses a short linear attack + release envelope so tone boundaries
 * don't produce audible clicks.
 */
function beep(frequency: number, durationMs: number, callback: () => void): void {
    const done = () => { try { callback(); } catch { /* swallow */ } };
    const ctx = getAudioContext();
    if (!ctx) {
        // No Web Audio in this environment — schedule the callback after the expected
        // duration so user code's `await BeepAsync(...)` still resolves on a realistic
        // timeline (tests that assert ordering aren't surprised).
        setTimeout(done, Math.max(0, durationMs));
        return;
    }

    // Best-effort unlock on suspended contexts. `resume()` is a no-op if the user hasn't
    // gestured yet; the scheduled beep will just be silent in that case (it still fires
    // and its `ended` handler still completes the callback on time).
    if (ctx.state === "suspended") {
        ctx.resume().catch(() => { /* swallow */ });
    }

    const now = ctx.currentTime;
    const durSec = Math.max(0.01, durationMs / 1000);
    const startAt = Math.max(now, beepQueueEndTime);
    const endAt = startAt + durSec;
    beepQueueEndTime = endAt;

    const osc = ctx.createOscillator();
    // Sine is gentler than square; the stock Windows Console.Beep sounds closer to a
    // square wave but that's actively unpleasant at higher frequencies in browser Web
    // Audio, which doesn't band-limit the square. Sine + a slight gain ramp reads as
    // a clean "beep" without a painful alias.
    osc.type = "sine";
    osc.frequency.value = Math.max(20, Math.min(20000, frequency));

    const gain = ctx.createGain();
    const attack = Math.min(0.005, durSec * 0.25);
    const release = Math.min(0.01, durSec * 0.25);
    gain.gain.setValueAtTime(0, startAt);
    gain.gain.linearRampToValueAtTime(0.2, startAt + attack);
    gain.gain.setValueAtTime(0.2, Math.max(startAt + attack, endAt - release));
    gain.gain.linearRampToValueAtTime(0, endAt);

    osc.connect(gain);
    gain.connect(ctx.destination);

    let completed = false;
    const complete = () => {
        if (completed) return;
        completed = true;
        try { osc.disconnect(); gain.disconnect(); } catch { /* nop */ }
        done();
    };
    osc.onended = complete;
    // Belt-and-braces: if `ended` never fires (some headless configs), a setTimeout is
    // a guaranteed floor for callback completion. Account for queue delay so queued
    // beeps' callbacks fire at their scheduled end, not `durationMs` from now.
    const waitMs = (endAt - now) * 1000;
    setTimeout(complete, waitMs + 50);

    try {
        osc.start(startAt);
        osc.stop(endAt);
    } catch {
        complete();
    }
}

export interface TerminalBridgeSink {
    writeStdOut(text: string): void;
    writeStdErr(text: string): void;
    /** Flip the line editor into key mode (raw pass-through) or back to line mode. */
    setKeyMode(enabled: boolean): void;
    /** Propagate `CarbideConsole.TreatControlCAsInput` to the line editor's local flag. */
    setTreatControlCAsInput(value: boolean): void;
    /** T3 — current column count, reads directly off the xterm instance. */
    getCols(): number;
    /** T3 — current row count, reads directly off the xterm instance. */
    getRows(): number;
    /** Dispose hook — clears bridge-side subscriptions and pointers. */
    dispose(): void;
}

/**
 * Controller vended alongside the sink so the session shell can forward key-mode /
 * TreatCtrlC updates to the line editor. Supplied by the session; null when no editor is
 * attached (no line mode, e.g. pure output-only sessions).
 */
export interface LineEditorHandle {
    setKeyMode(enabled: boolean): void;
    setTreatControlCAsInput(value: boolean): void;
}

/**
 * The interactive sink currently attached, or null when no interactive run is live.
 * Swapped by `installBridge` / `uninstallBridge`; read by the trampolines below on every
 * call.
 *
 * This indirection is load-bearing, not stylistic. Mono-WASM resolves a
 * `[JSImport("globalThis.Carbide.Terminal.write")]` binding once and caches the *function
 * object* it found. Publishing a fresh function per run — which is what `installBridge`
 * used to do — left the C# side calling the first run's closure forever, so every
 * interactive run after the first wrote into the first run's terminal. `RunResult.stdOut`
 * stayed correct throughout, because the C# side tees output into a `StringBuilder`
 * independently of the bridge, so nothing failed loudly: a browser IDE showed a working
 * first tab and permanently silent ones after it. Reading each terminal right after its
 * own run — the natural way to write such a test — hides it too; it takes running several
 * programs and then reading every terminal.
 *
 * The fix is to make the function identities permanent and move the per-run part behind
 * them.
 */
let activeSink: TerminalBridgeSink | null = null;

/** Lazily-built host-default sinks, used whenever no interactive sink is attached. */
let defaultWriteStdOut: ((text: string) => void) | null = null;
let defaultWriteStdErr: ((text: string) => void) | null = null;

function writeStdOutTrampoline(text: string): void {
    if (activeSink) { activeSink.writeStdOut(text); return; }
    (defaultWriteStdOut ??= defaultStdoutSink())(text);
}

function writeStdErrTrampoline(text: string): void {
    if (activeSink) { activeSink.writeStdErr(text); return; }
    (defaultWriteStdErr ??= defaultStderrSink())(text);
}

function setKeyModeTrampoline(enabled: boolean): void {
    // No line editor outside an interactive run — nothing to toggle.
    activeSink?.setKeyMode(enabled);
}

function setTreatControlCAsInputTrampoline(value: boolean): void {
    activeSink?.setTreatControlCAsInput(value);
}

function getColsTrampoline(): number {
    return activeSink ? activeSink.getCols() : 80;
}

function getRowsTrampoline(): number {
    return activeSink ? activeSink.getRows() : 24;
}

// T2.1 — Callback-based delay. Earlier T2 exposed a Promise-returning `delay`; Mono-WASM's
// JSImport Promise-to-Task marshaler wraps such results in a TCS with
// `TaskCreationOptions.RunContinuationsAsynchronously`, which forces every
// await-continuation through the ThreadPool — which on single-threaded browser-wasm falls
// back to `Monitor.Wait(INFINITE)` and trips "Cannot wait on monitors". The callback
// variant hands the completion through a local TCS the C# side constructs with
// `TaskCreationOptions.None`, so continuations run synchronously inline on the main thread
// when setTimeout fires.
function delayCallbackTrampoline(ms: number, callback: () => void): void {
    setTimeout(() => { try { callback(); } catch { /* swallow */ } }, Math.max(0, ms));
}

// T2.1 — CarbideSyncContext's Post path enqueues via `setTimeout(cb, 0)` to actually yield
// to the JS event loop between continuations. Previously CarbideSyncContext.Post ran
// inline, which meant `Task.Yield()` + other micro-yield patterns never yielded control and
// any setTimeout-backed await (DelayAsync, Promise-based JSImports) never got a chance to
// fire. With macrotask-based Post we pay one microtask queue hop per continuation but the
// browser event loop actually advances.
function scheduleMacrotaskTrampoline(callback: () => void): void {
    setTimeout(() => { try { callback(); } catch { /* swallow */ } }, 0);
}

/**
 * Install the stable `globalThis.Carbide.Terminal` surface if it isn't there yet, and
 * return it. Every member is a module-level function whose identity never changes for the
 * lifetime of the page, so a JSImport binding resolved at any point stays valid across any
 * number of install/uninstall cycles.
 */
function ensureTerminalSurface(): Record<string, unknown> {
    const carbide = ((globalThis as Record<string, unknown>).Carbide ??= {}) as Record<string, unknown>;
    const existing = carbide.Terminal as Record<string, unknown> | undefined;
    if (existing?.write === writeStdOutTrampoline) {
        return existing;
    }
    const surface: Record<string, unknown> = {
        write: writeStdOutTrampoline,
        writeErr: writeStdErrTrampoline,
        setKeyMode: setKeyModeTrampoline,
        setTreatControlCAsInput: setTreatControlCAsInputTrampoline,
        getCols: getColsTrampoline,
        getRows: getRowsTrampoline,
        delayCallback: delayCallbackTrampoline,
        scheduleMacrotask: scheduleMacrotaskTrampoline,
        // T3.1 — single-tone beep via Web Audio. See the module-level `beep` for the full
        // story. Callback-based to dovetail with the same Mono-WASM marshaler caveat that
        // drove `delayCallback`.
        beep,
    };
    carbide.Terminal = surface;
    return surface;
}

/**
 * Boot-time default. Installs `globalThis.Carbide.Terminal` so code paths that resolve the
 * JSImport at runtime (`CarbideBridge.WriteStdOut` in the forked System.Console's
 * `CarbideStdWriteStream`) always have something to call, whether or not an interactive
 * session is live. With no interactive sink attached, writes route to the host's default
 * output (Node: `process.stdout`/`process.stderr`; browser: `console.log`/`console.error`).
 * `installBridge` below attaches a sink for the duration of an interactive run;
 * `uninstallBridge` detaches it — neither replaces the surface.
 *
 * Without this, calling `Console.OpenStandardOutput().Write(...)` outside
 * `runInteractive` raised `Carbide not found while looking up
 * globalThis.Carbide.Terminal.write` — see R2-followup / `cli/test/advanced-usage`.
 */
export function installDefaultBridge(): void {
    ensureTerminalSurface();
}

function defaultStdoutSink(): (text: string) => void {
    const proc = (globalThis as { process?: { stdout?: { write?: (s: string) => unknown } } }).process;
    if (proc?.stdout?.write) {
        // Node: write raw bytes (no extra newline). Matches how `Console.OpenStandardOutput`
        // + `writer.Write("raw")` + `writer.Flush()` is expected to land in the parent CLI
        // process's stdout, in line with the JSON-trailer-composability contract.
        return (text: string) => { proc.stdout!.write!(text); };
    }
    // Browser fallback: `console.log` always exists and adds a newline, which would
    // double-space multi-line payloads. Strip a single trailing `\n` so a caller that
    // writes "hello\n" reads "hello" in the console without gaining a blank line.
    return (text: string) => { console.log(text.endsWith("\n") ? text.slice(0, -1) : text); };
}

function defaultStderrSink(): (text: string) => void {
    const proc = (globalThis as { process?: { stderr?: { write?: (s: string) => unknown } } }).process;
    if (proc?.stderr?.write) {
        return (text: string) => { proc.stderr!.write!(text); };
    }
    return (text: string) => { console.error(text.endsWith("\n") ? text.slice(0, -1) : text); };
}

/**
 * Route Carbide-side writes to the given xterm instance. Attaches a sink behind the stable
 * `{write, writeErr, setKeyMode, setTreatControlCAsInput, ...}` globals the C# JSImports
 * resolve against — the globals themselves are installed once (see `ensureTerminalSurface`)
 * and never replaced, because Mono-WASM caches the function object it resolved and would
 * otherwise keep calling a finished run's closure.
 */
export function installBridge(
    terminal: XtermTerminalLike,
    editor: LineEditorHandle | null,
): TerminalBridgeSink {
    const sink: TerminalBridgeSink = {
        writeStdOut(text) {
            terminal.write(text);
        },
        writeStdErr(text) {
            // T1 — stderr lands in the same xterm buffer as stdout. The C# side has already
            // wrapped the chunk in the caller's chosen SGR if any. Interleaving with stdout
            // is unavoidable at a single-buffer terminal and matches conhost/cmd behaviour.
            terminal.write(text);
        },
        setKeyMode(enabled) {
            editor?.setKeyMode(enabled);
        },
        setTreatControlCAsInput(value) {
            editor?.setTreatControlCAsInput(value);
        },
        getCols() {
            // xterm always exposes a non-zero cols/rows after `open()`, but the
            // `XtermTerminalLike` structural contract makes them optional to keep mock-based
            // unit tests cheap. Default to VT100's 80×24 if the host's terminal is a reduced
            // mock that elides the property — the forked System.Console consumer can still
            // get a non-zero width.
            return terminal.cols ?? 80;
        },
        getRows() {
            return terminal.rows ?? 24;
        },
        dispose() {
            // T2 editor disposal is handled by the session shell which owns the editor's
            // lifecycle; the bridge just holds the pointer. Detaching the sink is
            // `uninstallBridge`'s job — a sink that outlived its own uninstall would keep
            // writing into a terminal the host has already let go of.
        },
    };

    ensureTerminalSurface();
    activeSink = sink;
    return sink;
}

/**
 * Detach the interactive sink, so writes fall back to the host's default output. Matches
 * `installBridge`. Idempotent.
 *
 * Note: does NOT delete or replace `globalThis.Carbide.Terminal` — the forked
 * System.Console's `CarbideStdWriteStream` (and any other late-binding JSImport consumer)
 * expects the surface to remain resolvable whether or not an interactive session is active.
 * Pre-bridge behaviour (delete-on-teardown) caused non-interactive `project.run()` calls
 * that touched `Console.OpenStandardOutput()` to throw with `Carbide not found while
 * looking up globalThis.Carbide.Terminal.write`. Replacing the surface was no better: a
 * JSImport binding already resolved against the old functions kept using them.
 */
export function uninstallBridge(): void {
    activeSink = null;
}

/**
 * Whether `globalThis.Carbide.Terminal` is resolvable — i.e. whether a JSImport pointed at
 * it will bind. True from boot onward once `installDefaultBridge` has run, and unaffected
 * by install/uninstall cycles, which now swap the sink behind a permanent surface rather
 * than replacing the surface. Use {@link isInteractiveSinkAttached} to ask the different
 * question of whether a live interactive run currently owns the output.
 */
export function isBridgeInstalled(): boolean {
    const carbide = (globalThis as Record<string, unknown>).Carbide as Record<string, unknown> | undefined;
    return !!carbide && "Terminal" in carbide;
}

/**
 * Whether an interactive run currently owns terminal output. False outside
 * `installBridge`/`uninstallBridge`, when writes fall back to the host's default streams.
 */
export function isInteractiveSinkAttached(): boolean {
    return activeSink !== null;
}
