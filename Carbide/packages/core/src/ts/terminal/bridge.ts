// T1 — installs the JS-side half of the interactive terminal bridge on
// `globalThis.Carbide.Terminal`. The C# side reaches this via
// `[JSImport("globalThis.Carbide.Terminal.write")]` / `.writeErr`. One active session at a
// time; `startInteractiveSession` enforces exclusivity on the JS side so a stray xterm
// instance can't clobber another session's bindings mid-run.

import type { XtermTerminalLike } from "../types.js";

export interface TerminalBridgeSink {
    writeStdOut(text: string): void;
    writeStdErr(text: string): void;
    /** Dispose hook for T2 — clears any bridge-side subscriptions (`onData`, `onResize`). */
    dispose(): void;
}

/**
 * Route Carbide-side writes to the given xterm instance. Installs the `{write, writeErr}`
 * globals the C# bridge imports resolve against; those globals point to the returned sink's
 * functions for the life of the session.
 */
export function installBridge(terminal: XtermTerminalLike): TerminalBridgeSink {
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
        dispose() {
            // T1 holds no subscriptions. The actual uninstall happens in `uninstallBridge`.
        },
    };

    // Unscoped globalThis.Carbide.Terminal is the resolve target for the C# JSImports. We
    // don't try to run two sessions concurrently; the session shell guards that case and
    // this function is only called inside its mutex.
    const carbide = ((globalThis as Record<string, unknown>).Carbide ??= {}) as Record<string, unknown>;
    carbide.Terminal = {
        write: sink.writeStdOut,
        writeErr: sink.writeStdErr,
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
