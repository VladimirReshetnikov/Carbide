// T1+T2+T3 — installs the JS-side half of the interactive terminal bridge on
// `globalThis.Carbide.Terminal`. The C# side reaches this via
// `[JSImport("globalThis.Carbide.Terminal.{write|writeErr|setKeyMode|setTreatControlCAsInput|getCols|getRows}")]`.
// T1 wires the output side (write/writeErr). T2 adds setKeyMode and setTreatControlCAsInput
// so the line editor can be toggled + the Ctrl+C policy can be propagated from C# → JS
// without a round-trip per keystroke. T3 adds getCols/getRows so the forked
// System.Console.dll's synchronous WindowWidth/WindowHeight getters can answer without a
// NotifyResize round-trip.

import type { XtermTerminalLike } from "../types.js";

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
 * Route Carbide-side writes to the given xterm instance. Installs the `{write, writeErr,
 * setKeyMode, setTreatControlCAsInput}` globals the C# JSImports resolve against.
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
            // lifecycle; the bridge just holds the pointer.
        },
    };

    const carbide = ((globalThis as Record<string, unknown>).Carbide ??= {}) as Record<string, unknown>;
    carbide.Terminal = {
        write: sink.writeStdOut,
        writeErr: sink.writeStdErr,
        setKeyMode: sink.setKeyMode,
        setTreatControlCAsInput: sink.setTreatControlCAsInput,
        getCols: sink.getCols,
        getRows: sink.getRows,
        // T2.1 — Callback-based delay. Earlier T2 exposed a Promise-returning `delay`;
        // Mono-WASM's JSImport Promise-to-Task marshaler wraps such results in a TCS
        // with `TaskCreationOptions.RunContinuationsAsynchronously`, which forces every
        // await-continuation through the ThreadPool — which on single-threaded browser-
        // wasm falls back to `Monitor.Wait(INFINITE)` and trips "Cannot wait on monitors".
        // The callback variant hands the completion through a local TCS the C# side
        // constructs with `TaskCreationOptions.None`, so continuations run synchronously
        // inline on the main thread when setTimeout fires.
        delayCallback: (ms: number, callback: () => void) => {
            setTimeout(() => {
                try { callback(); } catch { /* swallow */ }
            }, Math.max(0, ms));
        },
        // T2.1 — CarbideSyncContext's Post path enqueues via `setTimeout(cb, 0)` to actually
        // yield to the JS event loop between continuations. Previously CarbideSyncContext.Post
        // ran inline, which meant `Task.Yield()` + other micro-yield patterns never yielded
        // control and any setTimeout-backed await (DelayAsync, Promise-based JSImports) never
        // got a chance to fire. With macrotask-based Post we pay one microtask queue hop per
        // continuation but browser event loop actually advances.
        scheduleMacrotask: (callback: () => void) => {
            setTimeout(() => {
                try { callback(); } catch { /* swallow */ }
            }, 0);
        },
    };
    return sink;
}

/** Remove the `globalThis.Carbide.Terminal` pointer. Matches `installBridge`. Idempotent. */
export function uninstallBridge(): void {
    const carbide = (globalThis as Record<string, unknown>).Carbide as Record<string, unknown> | undefined;
    if (carbide && "Terminal" in carbide) {
        delete carbide.Terminal;
    }
}

/**
 * Whether a terminal bridge is currently installed. Consulted before activating the
 * emscripten print/printErr overlay (which falls back to `console.log` when no bridge is
 * live).
 */
export function isBridgeInstalled(): boolean {
    const carbide = (globalThis as Record<string, unknown>).Carbide as Record<string, unknown> | undefined;
    return !!carbide && "Terminal" in carbide;
}
