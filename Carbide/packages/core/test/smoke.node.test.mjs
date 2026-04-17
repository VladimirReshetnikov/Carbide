import { test } from "node:test";
import assert from "node:assert/strict";
import { initialize, CARBIDE_VERSION } from "../dist/index.js";

test("initialize() returns the expected greeting", async () => {
    const result = await initialize();
    assert.equal(result, "Carbide initialised");
    console.log(result);
});

test("CARBIDE_VERSION is exported", () => {
    assert.equal(typeof CARBIDE_VERSION, "string");
});
