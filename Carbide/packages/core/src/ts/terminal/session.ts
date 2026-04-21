// T1+T2 — browser-side orchestration of an interactive terminal run. Owns the bridge
// lifetime (install before RunInteractiveAsync fires, uninstall after the C# side drains)
// plus the line editor (T2) that turns xterm's onData bytes into committed lines, and the
// resize subscription that pushes terminal geometry into CarbideConsole.WindowWidth.

import type { CarbideInteropExports } from "../runtime/dotnet-types.js";
import {
    parseRunResult,
    SCHEMA_VERSION,
    type RunInteractiveOptionsRequest,
} from "../interop/schema.js";
import type { BrowserHostAdapter } from "../host/browser/browser-adapter.js";
import type { InteractiveRunOptions, TerminalSession } from "../types.js";
import { installBridge, uninstallBridge, type TerminalBridgeSink } from "./bridge.js";
import { attachLineEditor, type LineEditorController } from "./line-editor.js";

/**
 * Start an interactive run on the given project. The bridge and line editor are wired
 * before the C# side receives the RunInteractiveAsync call; teardown runs in a `finally`
 * so an uncaught user exception still detaches cleanly.
 */
export function startInteractiveSession(
    interop: CarbideInteropExports,
    projectId: string,
    adapter: BrowserHostAdapter,
    options: InteractiveRunOptions,
): TerminalSession {
    // T2 — attach the line editor first so the bridge can route setKeyMode /
    // setTreatControlCAsInput through to it. Terminals that don't support `onData`
    // (Carbide's test mocks) produce a null subscription; the editor controller still
    // works, just never fires.
    const editor: LineEditorController | null = options.terminal.onData
        ? attachLineEditor({
              terminal: options.terminal,
              projectId,
              deliverStdIn: (pid, keyMode, data) => interop.DeliverStdIn(pid, keyMode, data),
              deliverSignal: (pid, signalName) => interop.DeliverSignal(pid, signalName),
          })
        : null;

    const bridge: TerminalBridgeSink = installBridge(options.terminal, editor);
    adapter.attachTerminalSink(bridge);

    // T2 — prime C#-side CarbideConsole.WindowWidth/Height with the current xterm
    // geometry, then subscribe to onResize so resize propagates without user action.
    const initialCols = typeof options.terminal.cols === "number" ? options.terminal.cols : 80;
    const initialRows = typeof options.terminal.rows === "number" ? options.terminal.rows : 24;
    interop.NotifyResize(projectId, initialCols, initialRows);
    const resizeSubscription = options.terminal.onResize?.((size) => {
        interop.NotifyResize(projectId, size.cols, size.rows);
    }) ?? null;

    const request: RunInteractiveOptionsRequest = {
        schemaVersion: SCHEMA_VERSION,
        stderrStyle: options.stderrStyle ?? "plain",
    };
    if (options.args && options.args.length > 0) {
        request.args = [...options.args];
    }

    let bridgeDetached = false;

    /**
     * Idempotent bridge/adapter detach. Run from both paths:
     *  - the IIFE's finally (run completed naturally or threw),
     *  - an explicit dispose() call (user-initiated mid-run teardown).
     * Must be called AFTER the run has drained, otherwise late writes from the C# side
     * hit a missing `globalThis.Carbide.Terminal.*` and throw.
     */
    function detachBridge(): void {
        if (bridgeDetached) return;
        bridgeDetached = true;
        resizeSubscription?.dispose();
        editor?.dispose();
        adapter.detachTerminalSink();
        uninstallBridge();
        bridge.dispose();
    }

    // Declare `exitPromise` via a forward reference because `dispose` awaits it and the
    // IIFE below references `detachBridge`.
    let exitPromise: Promise<ReturnType<typeof parseRunResult>>;

    /**
     * Reviews R1 C3 / R2 §2 — honour the mid-run-safe contract. Signal the C# side to
     * unblock any pending read / cancel the run token, wait for the run to actually drain
     * (so stdout writes land before the bridge goes away), then detach the bridge.
     *
     * Repeated calls are safe: the second await on `exitPromise` resolves immediately,
     * and `detachBridge` is idempotent.
     */
    async function dispose(): Promise<void> {
        try {
            interop.DisposeTerminal(projectId);
        } catch {
            // Bridge may have already been torn down by a concurrent completion; harmless.
        }
        try {
            await exitPromise;
        } catch {
            // The caller gets run failures via the public `exitPromise`. Swallow here so
            // dispose() itself always resolves cleanly even when the program threw.
        }
        detachBridge();
    }

    // Kick off the run. The C# side drains its streaming writers in its own finally block,
    // so bytes buffered at the moment the entry point exits still reach the bridge before
    // the promise resolves.
    exitPromise = (async () => {
        try {
            const json = await interop.RunInteractiveAsync(projectId, JSON.stringify(request));
            return parseRunResult(json);
        } finally {
            detachBridge();
        }
    })();

    return {
        exitPromise,
        dispose,
    };
}
