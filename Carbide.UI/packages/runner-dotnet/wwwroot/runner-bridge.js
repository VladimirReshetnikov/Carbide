// @carbide-ui/avalonia-runner — postMessage bridge between the parent frame and the
// C# RunnerProgram exports. UI-M3 / plan §7.5 / proposal §7.3 / §8.
//
// Loaded by C# via `JSHost.ImportAsync("runner-bridge", "./runner-bridge.js")`. The
// [JSImport]-bound C# methods PostReady / PostRunning / PostError resolve to the named
// exports below. The message-event listener forwards inbound `load` envelopes to the
// [JSExport]-exposed OnLoadMessage through globalThis.__carbideRunnerInterop (installed
// by main.js before runMain). Schema v1 per proposal §8.

const SCHEMA_VERSION = 1;

export function postReady() {
    if (globalThis.parent && globalThis.parent !== globalThis) {
        globalThis.parent.postMessage({ type: "runnerReady", schemaVersion: SCHEMA_VERSION }, "*");
    }
}

export function postRunning() {
    if (globalThis.parent && globalThis.parent !== globalThis) {
        globalThis.parent.postMessage({ type: "runnerRunning", schemaVersion: SCHEMA_VERSION }, "*");
    }
}

export function postError(message) {
    const kind = classifyError(message);
    if (globalThis.parent && globalThis.parent !== globalThis) {
        globalThis.parent.postMessage(
            { type: "runnerError", schemaVersion: SCHEMA_VERSION, message: String(message), kind },
            "*",
        );
    }
}

function classifyError(message) {
    const text = String(message);
    if (text.includes("runner boot failed")) return "load";
    if (text.includes("runner has already loaded")) return "teardown";
    // Exceptions thrown from OnLoadMessage reach here as C# ToString() text; they are
    // either load-time (e.g. GetType throws, Activator fails) or post-boot runtime
    // errors caught elsewhere. At the bridge level we can't always tell them apart,
    // so default to "load" for safety — the launcher surfaces the full message verbatim.
    return "load";
}

globalThis.addEventListener("message", (ev) => {
    const data = ev && ev.data;
    if (!data || typeof data !== "object") return;
    if (data.type !== "load") return;
    if (data.schemaVersion !== SCHEMA_VERSION) {
        if (globalThis.parent && globalThis.parent !== globalThis) {
            globalThis.parent.postMessage(
                {
                    type: "runnerError",
                    schemaVersion: SCHEMA_VERSION,
                    message: `unsupported schemaVersion ${data.schemaVersion}; runner speaks ${SCHEMA_VERSION}.`,
                    kind: "load",
                },
                "*",
            );
        }
        return;
    }
    const runner = globalThis.__carbideRunnerInterop;
    if (!runner || typeof runner.OnLoadMessage !== "function") {
        postError("runner-bridge: __carbideRunnerInterop not wired; main.js did not expose RunnerProgram.");
        return;
    }
    try {
        runner.OnLoadMessage(data.peBase64, data.pdbBase64 ?? null, data.appClass);
    } catch (err) {
        postError(`runner-bridge: OnLoadMessage dispatch threw: ${err && err.stack ? err.stack : err}`);
    }
});
