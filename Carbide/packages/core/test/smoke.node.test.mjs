// Placeholder export sanity — the real acceptance lives in test/node/hello.test.mjs.
import { test } from "node:test";
import assert from "node:assert/strict";
import { initialize, CARBIDE_VERSION, CarbideSession } from "../dist/index.js";

test("placeholder initialize() stays callable until M2", async () => {
    const result = await initialize();
    assert.equal(result, "Carbide initialised");
});

test("CARBIDE_VERSION is exported and matches package.json", async () => {
    assert.equal(typeof CARBIDE_VERSION, "string");
    // version.ts keeps a literal copy (browser bundles can't import JSON); this pin
    // is what keeps the two from drifting apart.
    const { readFile } = await import("node:fs/promises");
    const pkg = JSON.parse(await readFile(new URL("../package.json", import.meta.url), "utf8"));
    assert.equal(CARBIDE_VERSION, pkg.version, "src/ts/version.ts must match package.json version");
});

test("CarbideSession exports initializeAsync", () => {
    assert.equal(typeof CarbideSession.initializeAsync, "function");
});
