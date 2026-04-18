// Placeholder export sanity — the real acceptance lives in test/node/hello.test.mjs.
import { test } from "node:test";
import assert from "node:assert/strict";
import { initialize, CARBIDE_VERSION, CarbideSession } from "../dist/index.js";

test("placeholder initialize() stays callable until M2", async () => {
    const result = await initialize();
    assert.equal(result, "Carbide initialised");
});

test("CARBIDE_VERSION is exported", () => {
    assert.equal(typeof CARBIDE_VERSION, "string");
});

test("CarbideSession exports initializeAsync", () => {
    assert.equal(typeof CarbideSession.initializeAsync, "function");
});
