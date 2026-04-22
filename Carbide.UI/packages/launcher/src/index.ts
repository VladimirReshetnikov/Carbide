// @carbide-ui/launcher — UI-M3 implementation.
//
// Bridges a Carbide BuildResult into @carbide-ui/avalonia-runner via postMessage.
// See plan §7.6 and proposal §7.4 / §8 for the contract; §7.7 for protocol shape.
//
// Runtime dependency: none. `BuildResult` is a structural type (UI-I9: no import
// of @carbide/core). The launcher resolves the runner iframe's src URL relative to
// its own package location by default; callers can override via LaunchOptions.runnerSrc.

import type { BuildResult } from "./build-result-types.js";
import { encodeBase64 } from "./base64.js";
import {
    SCHEMA_VERSION,
    isInboundMessage,
    type InboundMessage,
    type LoadMessage,
    type RunnerErrorMessage,
} from "./protocol.js";

export type { BuildResult };
export { SCHEMA_VERSION } from "./protocol.js";

export interface LaunchOptions {
    /** Fully-qualified name of the Avalonia Application-derived type to instantiate
     *  inside the runner (e.g. "MyApp.App"). Required in v1; inference from
     *  BuildResult.primaryAssemblyName is a v1.1 follow-up (proposal §12 Q.2). */
    readonly appClass: string;
    /** How long to wait for the runner to post `runnerReady` and `runnerRunning`.
     *  Default: 30000 ms. */
    readonly readyTimeoutMs?: number;
    /** Called when the runner reports a `runnerError` with kind `"runtime"` — i.e.
     *  an uncaught exception inside the user program, emitted after the initial
     *  launch promise has already resolved. */
    onRuntimeError?(message: string): void;
    /** Override for the runner iframe's src. Defaults to the runner package's
     *  `index.html` resolved via `import.meta.url`. Useful in monorepo/dev scenarios
     *  or when the runner lives at a custom URL (CDN, static host). */
    readonly runnerSrc?: string;
}

export interface LaunchHandle {
    /** Opaque stable identifier for this handle. Useful for logging / diagnostics when
     *  multiple previews run concurrently in the same session. Monotonically assigned
     *  within the launcher module lifetime; not a UUID, not stable across reloads. */
    readonly id: string;
    /** Replace the running UI with a new build. v1 implementation performs an iframe
     *  re-boot (the runner only handles one load per iframe lifetime); rejects if
     *  the handle has been disposed. */
    reload(build: BuildResult): Promise<void>;
    /** Tear down the runner and optionally remove the iframe from the DOM. Idempotent. */
    dispose(removeIframe?: boolean): void;
}

const DEFAULT_READY_TIMEOUT_MS = 30_000;
let handleCounter = 0;

export async function launchInIframe(
    build: BuildResult,
    iframe: HTMLIFrameElement,
    options: LaunchOptions,
): Promise<LaunchHandle> {
    assertSuccessfulBuild(build);
    if (!options.appClass) {
        throw new TypeError("launchInIframe: options.appClass is required.");
    }
    const timeoutMs = options.readyTimeoutMs ?? DEFAULT_READY_TIMEOUT_MS;
    const runnerSrc = options.runnerSrc ?? resolveDefaultRunnerSrc();

    await bootIframe(iframe, runnerSrc, timeoutMs);
    await postLoadAndWait(iframe, build, options, timeoutMs);

    const runtimeErrorListener = (ev: MessageEvent) => {
        if (ev.source !== iframe.contentWindow) return;
        if (!isInboundMessage(ev.data)) return;
        const data = ev.data as InboundMessage;
        if (data.type === "runnerError" && data.kind === "runtime") {
            options.onRuntimeError?.(data.message);
        }
    };
    window.addEventListener("message", runtimeErrorListener);

    let disposed = false;
    const id = `launch-${++handleCounter}`;
    const handle: LaunchHandle = {
        id,
        async reload(newBuild: BuildResult): Promise<void> {
            if (disposed) throw new Error("@carbide-ui/launcher: LaunchHandle has been disposed.");
            assertSuccessfulBuild(newBuild);
            await bootIframe(iframe, runnerSrc, timeoutMs);
            await postLoadAndWait(iframe, newBuild, options, timeoutMs);
        },
        dispose(removeIframe?: boolean): void {
            if (disposed) return;
            disposed = true;
            window.removeEventListener("message", runtimeErrorListener);
            try {
                iframe.src = "about:blank";
            } catch {
                // defensive: iframe may already be detached
            }
            if (removeIframe) {
                iframe.remove();
            }
        },
    };
    return handle;
}

function assertSuccessfulBuild(build: BuildResult): void {
    if (!build.success || !build.pe) {
        throw new Error(
            "@carbide-ui/launcher: BuildResult is not a successful build — feed a build where success=true and pe is defined.",
        );
    }
}

function resolveDefaultRunnerSrc(): string {
    // Resolve relative to this module's URL. Works both in browsers (bundler rewrites
    // the URL at build time) and in Node (import.meta.url is a file:// URL).
    // Layout assumption: @carbide-ui/launcher/dist/index.js is sibling to
    // @carbide-ui/avalonia-runtime-bundle/index.html in node_modules. The bundle was
    // flattened at UI-M3 so its root IS the runner's iframe src (no separate
    // avalonia-runner package required).
    return new URL("../../avalonia-runtime-bundle/index.html", import.meta.url).href;
}

async function bootIframe(
    iframe: HTMLIFrameElement,
    runnerSrc: string,
    timeoutMs: number,
): Promise<void> {
    return new Promise<void>((resolve, reject) => {
        const onMessage = (ev: MessageEvent) => {
            if (ev.source !== iframe.contentWindow) return;
            if (!isInboundMessage(ev.data)) return;
            const data = ev.data as InboundMessage;
            if (data.type === "runnerError") {
                cleanup();
                reject(describeRunnerError(data, "during boot"));
                return;
            }
            if (data.type !== "runnerReady") return;
            cleanup();
            resolve();
        };
        const timer = setTimeout(() => {
            cleanup();
            reject(new Error(
                `@carbide-ui/launcher: runner did not post runnerReady within ${timeoutMs}ms ` +
                `(iframe.src=${JSON.stringify(runnerSrc)}).`,
            ));
        }, timeoutMs);
        const cleanup = () => {
            window.removeEventListener("message", onMessage);
            clearTimeout(timer);
        };
        window.addEventListener("message", onMessage);
        iframe.src = runnerSrc;
    });
}

async function postLoadAndWait(
    iframe: HTMLIFrameElement,
    build: BuildResult,
    options: LaunchOptions,
    timeoutMs: number,
): Promise<void> {
    if (!build.pe) throw new Error("@carbide-ui/launcher: BuildResult.pe missing.");
    const load: LoadMessage = {
        type: "load",
        schemaVersion: SCHEMA_VERSION,
        peBase64: encodeBase64(build.pe),
        pdbBase64: build.pdb ? encodeBase64(build.pdb) : null,
        appClass: options.appClass,
        runArgs: null,
    };
    return new Promise<void>((resolve, reject) => {
        const onMessage = (ev: MessageEvent) => {
            if (ev.source !== iframe.contentWindow) return;
            if (!isInboundMessage(ev.data)) return;
            const data = ev.data as InboundMessage;
            if (data.type === "runnerRunning") {
                cleanup();
                resolve();
            } else if (data.type === "runnerError") {
                cleanup();
                reject(describeRunnerError(data, "after load"));
            }
        };
        const timer = setTimeout(() => {
            cleanup();
            reject(new Error(
                `@carbide-ui/launcher: runner did not post runnerRunning within ${timeoutMs}ms after load.`,
            ));
        }, timeoutMs);
        const cleanup = () => {
            window.removeEventListener("message", onMessage);
            clearTimeout(timer);
        };
        window.addEventListener("message", onMessage);
        iframe.contentWindow?.postMessage(load, "*");
    });
}

function describeRunnerError(message: RunnerErrorMessage, context: string): Error {
    const err = new Error(
        `@carbide-ui/launcher: runner reported error (${message.kind}) ${context}: ${message.message}`,
    );
    (err as Error & { kind?: string }).kind = message.kind;
    return err;
}
