// T1 — browser-side orchestration of an interactive terminal run. Owns the bridge
// lifetime (install before RunInteractiveAsync fires, uninstall after the C# side drains)
// and the host-adapter terminal-sink attach/detach so emscripten's print/printErr overlays
// know where to route bytes during the run.

import type { CarbideInteropExports } from "../runtime/dotnet-types.js";
import {
    parseRunResult,
    SCHEMA_VERSION,
    type RunInteractiveOptionsRequest,
} from "../interop/schema.js";
import type { BrowserHostAdapter } from "../host/browser/browser-adapter.js";
import type { InteractiveRunOptions, TerminalSession } from "../types.js";
import { installBridge, uninstallBridge, type TerminalBridgeSink } from "./bridge.js";

/**
 * Start an interactive run on the given project. The bridge is wired before the C# side
 * receives the RunInteractiveAsync call; teardown runs in a `finally` so an uncaught user
 * exception still detaches cleanly.
 *
 * Returns a {@link TerminalSession} whose `exitPromise` resolves with the run's
 * {@link import("../types.js").RunResult}. Callers that need to tear down early can invoke
 * `dispose()`; it awaits the in-flight C# work before resolving.
 */
export function startInteractiveSession(
    interop: CarbideInteropExports,
    projectId: string,
    adapter: BrowserHostAdapter,
    options: InteractiveRunOptions,
): TerminalSession {
    const bridge = installBridge(options.terminal);
    adapter.attachTerminalSink(bridge);

    const request: RunInteractiveOptionsRequest = {
        schemaVersion: SCHEMA_VERSION,
        stderrStyle: options.stderrStyle ?? "plain",
    };
    if (options.args && options.args.length > 0) {
        request.args = [...options.args];
    }

    let disposed = false;

    async function teardown(activeBridge: TerminalBridgeSink): Promise<void> {
        if (disposed) return;
        disposed = true;
        try {
            interop.DisposeTerminal(projectId);
        } finally {
            adapter.detachTerminalSink();
            uninstallBridge();
            activeBridge.dispose();
        }
    }

    // Kick off the run. The C# side drains its streaming writers in its own finally block,
    // so bytes buffered at the moment the entry point exits still reach the bridge before
    // the promise resolves.
    const exitPromise = (async () => {
        try {
            const json = await interop.RunInteractiveAsync(projectId, JSON.stringify(request));
            return parseRunResult(json);
        } finally {
            await teardown(bridge);
        }
    })();

    return {
        exitPromise,
        dispose: () => teardown(bridge),
    };
}
