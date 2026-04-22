// @carbide-ui/launcher — UI-M3 unit tests.
//
// Exercises the protocol-timing and error-surfacing behaviour without a real browser.
// Full browser integration lives in a Playwright fixture (deferred; requires Chromium
// install). These tests mock `window` as a Node EventTarget and the iframe as a plain
// object with a .src setter that triggers simulated runner responses, and an autoresponder
// postMessage that emits runnerRunning in reply to load.

import { test } from "node:test";
import assert from "node:assert/strict";
import { EventEmitter } from "node:events";

// Install a minimal DOM surface BEFORE importing the launcher — the module references
// `window` and `setTimeout` at top level and inside launchInIframe.

class FakeMessageEvent extends Event {
    constructor(data, source) {
        super("message");
        this.data = data;
        this.source = source;
    }
}

function installFakeWindow() {
    const window = new EventTarget();
    globalThis.window = window;
    globalThis.MessageEvent = FakeMessageEvent;
    return window;
}

function uninstallFakeWindow() {
    delete globalThis.window;
}

function makeFakeIframe({ onSrc, onPostMessage }) {
    const iframe = {
        _src: "",
        get src() { return this._src; },
        set src(v) {
            this._src = v;
            if (onSrc) onSrc(v);
        },
        remove() { this._removed = true; },
        contentWindow: { postMessage: null },
    };
    iframe.contentWindow.postMessage = (msg, targetOrigin) => {
        if (onPostMessage) onPostMessage(msg, targetOrigin);
    };
    return iframe;
}

// Tiny helper: schedule `fn` on the next tick.
function tick(fn) { setImmediate(fn); }

const validBuild = {
    success: true,
    schemaVersion: 5,
    pe: new Uint8Array([0x4d, 0x5a, 0x00, 0x01, 0x02, 0x03]),
    pdb: undefined,
    diagnostics: [],
    durationMs: 10,
    peSchemaVersion: 1,
    primaryAssemblyName: "MyApp",
};

const failedBuild = {
    success: false,
    schemaVersion: 5,
    pe: undefined,
    pdb: undefined,
    diagnostics: [{ id: "CS0001", severity: "error", message: "broken" }],
    durationMs: 1,
};

const { launchInIframe, SCHEMA_VERSION } = await (async () => {
    installFakeWindow();
    return import("../dist/index.js");
})();

test("launchInIframe rejects non-successful builds", async () => {
    const iframe = makeFakeIframe({});
    await assert.rejects(
        launchInIframe(failedBuild, iframe, { appClass: "App", runnerSrc: "x" }),
        /not a successful build/,
    );
});

test("launchInIframe rejects when appClass is missing", async () => {
    const iframe = makeFakeIframe({});
    await assert.rejects(
        launchInIframe(validBuild, iframe, { appClass: "", runnerSrc: "x" }),
        /appClass is required/,
    );
});

test("launchInIframe happy path: runnerReady then runnerRunning", async () => {
    const window = globalThis.window;
    const loadReceived = new EventEmitter();
    const iframe = makeFakeIframe({
        onSrc() {
            tick(() => {
                window.dispatchEvent(new FakeMessageEvent(
                    { type: "runnerReady", schemaVersion: SCHEMA_VERSION },
                    iframe.contentWindow,
                ));
            });
        },
        onPostMessage(msg) {
            if (msg?.type === "load") {
                loadReceived.emit("load", msg);
                tick(() => {
                    window.dispatchEvent(new FakeMessageEvent(
                        { type: "runnerRunning", schemaVersion: SCHEMA_VERSION },
                        iframe.contentWindow,
                    ));
                });
            }
        },
    });
    const load = new Promise((resolve) => loadReceived.once("load", resolve));
    const handle = await launchInIframe(validBuild, iframe, {
        appClass: "MyApp.App",
        runnerSrc: "http://runner.invalid/index.html",
    });
    const loadMsg = await load;
    assert.equal(loadMsg.schemaVersion, SCHEMA_VERSION);
    assert.equal(loadMsg.appClass, "MyApp.App");
    assert.ok(loadMsg.peBase64.length > 0);
    assert.equal(loadMsg.pdbBase64, null);
    assert.ok(handle);
    handle.dispose();
});

test("launchInIframe rejects when runnerReady never arrives within timeout", async () => {
    const iframe = makeFakeIframe({ onSrc() { /* intentionally silent */ } });
    await assert.rejects(
        launchInIframe(validBuild, iframe, {
            appClass: "App",
            readyTimeoutMs: 60,
            runnerSrc: "http://silent.invalid/",
        }),
        /did not post runnerReady within 60ms/,
    );
});

test("launchInIframe rejects when runner posts runnerError during boot", async () => {
    const window = globalThis.window;
    const iframe = makeFakeIframe({
        onSrc() {
            tick(() => {
                window.dispatchEvent(new FakeMessageEvent(
                    {
                        type: "runnerError",
                        schemaVersion: SCHEMA_VERSION,
                        message: "boot blew up",
                        kind: "load",
                    },
                    iframe.contentWindow,
                ));
            });
        },
    });
    await assert.rejects(
        launchInIframe(validBuild, iframe, {
            appClass: "App",
            runnerSrc: "http://erroring.invalid/",
        }),
        /runner reported error \(load\) during boot: boot blew up/,
    );
});

test("launchInIframe rejects when runner posts runnerError after load", async () => {
    const window = globalThis.window;
    const iframe = makeFakeIframe({
        onSrc() {
            tick(() => {
                window.dispatchEvent(new FakeMessageEvent(
                    { type: "runnerReady", schemaVersion: SCHEMA_VERSION },
                    iframe.contentWindow,
                ));
            });
        },
        onPostMessage(msg) {
            if (msg?.type === "load") {
                tick(() => {
                    window.dispatchEvent(new FakeMessageEvent(
                        {
                            type: "runnerError",
                            schemaVersion: SCHEMA_VERSION,
                            message: "App class not found",
                            kind: "load",
                        },
                        iframe.contentWindow,
                    ));
                });
            }
        },
    });
    await assert.rejects(
        launchInIframe(validBuild, iframe, {
            appClass: "Missing.App",
            runnerSrc: "http://runner.invalid/",
        }),
        /runner reported error \(load\) after load: App class not found/,
    );
});

test("LaunchHandle.dispose is idempotent and optionally removes iframe", async () => {
    const window = globalThis.window;
    const iframe = makeFakeIframe({
        onSrc() {
            tick(() => window.dispatchEvent(new FakeMessageEvent(
                { type: "runnerReady", schemaVersion: SCHEMA_VERSION },
                iframe.contentWindow,
            )));
        },
        onPostMessage(msg) {
            if (msg?.type === "load") {
                tick(() => window.dispatchEvent(new FakeMessageEvent(
                    { type: "runnerRunning", schemaVersion: SCHEMA_VERSION },
                    iframe.contentWindow,
                )));
            }
        },
    });
    const handle = await launchInIframe(validBuild, iframe, {
        appClass: "App",
        runnerSrc: "http://runner.invalid/",
    });
    handle.dispose();
    handle.dispose(); // second call: no throw
    assert.equal(iframe._src, "about:blank");
    assert.notEqual(iframe._removed, true, "dispose() without arg should not remove iframe");

    const iframe2 = makeFakeIframe({
        onSrc() {
            tick(() => window.dispatchEvent(new FakeMessageEvent(
                { type: "runnerReady", schemaVersion: SCHEMA_VERSION },
                iframe2.contentWindow,
            )));
        },
        onPostMessage(msg) {
            if (msg?.type === "load") {
                tick(() => window.dispatchEvent(new FakeMessageEvent(
                    { type: "runnerRunning", schemaVersion: SCHEMA_VERSION },
                    iframe2.contentWindow,
                )));
            }
        },
    });
    const handle2 = await launchInIframe(validBuild, iframe2, {
        appClass: "App",
        runnerSrc: "http://runner.invalid/",
    });
    handle2.dispose(true);
    assert.equal(iframe2._removed, true, "dispose(true) should remove iframe");
});

test("onRuntimeError fires for runtime-kind errors after launch resolves", async () => {
    const window = globalThis.window;
    const iframe = makeFakeIframe({
        onSrc() {
            tick(() => window.dispatchEvent(new FakeMessageEvent(
                { type: "runnerReady", schemaVersion: SCHEMA_VERSION },
                iframe.contentWindow,
            )));
        },
        onPostMessage(msg) {
            if (msg?.type === "load") {
                tick(() => window.dispatchEvent(new FakeMessageEvent(
                    { type: "runnerRunning", schemaVersion: SCHEMA_VERSION },
                    iframe.contentWindow,
                )));
            }
        },
    });
    let caught = null;
    const handle = await launchInIframe(validBuild, iframe, {
        appClass: "App",
        runnerSrc: "http://runner.invalid/",
        onRuntimeError(msg) { caught = msg; },
    });
    // Now simulate a runtime-kind error arriving post-launch.
    window.dispatchEvent(new FakeMessageEvent(
        {
            type: "runnerError",
            schemaVersion: SCHEMA_VERSION,
            message: "unhandled exception in button click",
            kind: "runtime",
        },
        iframe.contentWindow,
    ));
    // Event dispatch is synchronous; the callback should have fired already.
    assert.equal(caught, "unhandled exception in button click");
    handle.dispose();
});

test("reload() re-boots the iframe and posts a new load", async () => {
    const window = globalThis.window;
    const loads = [];
    const iframe = makeFakeIframe({
        onSrc() {
            tick(() => window.dispatchEvent(new FakeMessageEvent(
                { type: "runnerReady", schemaVersion: SCHEMA_VERSION },
                iframe.contentWindow,
            )));
        },
        onPostMessage(msg) {
            if (msg?.type === "load") {
                loads.push(msg);
                tick(() => window.dispatchEvent(new FakeMessageEvent(
                    { type: "runnerRunning", schemaVersion: SCHEMA_VERSION },
                    iframe.contentWindow,
                )));
            }
        },
    });
    const handle = await launchInIframe(validBuild, iframe, {
        appClass: "App.V1",
        runnerSrc: "http://runner.invalid/",
    });
    const secondBuild = { ...validBuild, pe: new Uint8Array([0x4d, 0x5a, 0x04, 0x05]) };
    await handle.reload({ ...secondBuild });
    assert.equal(loads.length, 2);
    assert.equal(loads[0].appClass, "App.V1");
    assert.equal(loads[1].appClass, "App.V1");  // same options on reload
    assert.notEqual(loads[0].peBase64, loads[1].peBase64);
    handle.dispose();
});

test("reload() after dispose rejects", async () => {
    const window = globalThis.window;
    const iframe = makeFakeIframe({
        onSrc() {
            tick(() => window.dispatchEvent(new FakeMessageEvent(
                { type: "runnerReady", schemaVersion: SCHEMA_VERSION },
                iframe.contentWindow,
            )));
        },
        onPostMessage(msg) {
            if (msg?.type === "load") {
                tick(() => window.dispatchEvent(new FakeMessageEvent(
                    { type: "runnerRunning", schemaVersion: SCHEMA_VERSION },
                    iframe.contentWindow,
                )));
            }
        },
    });
    const handle = await launchInIframe(validBuild, iframe, {
        appClass: "App",
        runnerSrc: "http://runner.invalid/",
    });
    handle.dispose();
    await assert.rejects(handle.reload(validBuild), /has been disposed/);
});

test("schema-version mismatch messages are ignored by isInboundMessage", async () => {
    const window = globalThis.window;
    // Post a runnerReady with a bad schema version; launcher should time out
    // because it ignores unknown versions (per UI-I5).
    const iframe = makeFakeIframe({
        onSrc() {
            tick(() => window.dispatchEvent(new FakeMessageEvent(
                { type: "runnerReady", schemaVersion: 999 },
                iframe.contentWindow,
            )));
        },
    });
    await assert.rejects(
        launchInIframe(validBuild, iframe, {
            appClass: "App",
            readyTimeoutMs: 60,
            runnerSrc: "http://runner.invalid/",
        }),
        /did not post runnerReady within 60ms/,
    );
});

// Cleanup: restore globalThis.
test("cleanup", () => {
    uninstallFakeWindow();
});
