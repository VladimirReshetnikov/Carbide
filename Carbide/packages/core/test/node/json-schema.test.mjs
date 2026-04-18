import { test } from "node:test";
import assert from "node:assert/strict";
import { CarbideSchemaError, SCHEMA_VERSION } from "../../dist/index.js";
import { parseRunResult } from "../../dist/interop/schema.js";

test("SCHEMA_VERSION is exported and pinned to the current wire version", () => {
    assert.equal(typeof SCHEMA_VERSION, "number");
    // M5 bumped the wire schema from 1 to 2 when ProjectOptions gained defineConstants.
    assert.equal(SCHEMA_VERSION, 2);
});

test("parseRunResult rejects a mismatched schemaVersion", () => {
    const bogus = JSON.stringify({
        schemaVersion: 9999,
        success: true,
        stdOut: "",
        stdErr: "",
        durationMs: 0,
        diagnostics: [],
    });
    try {
        parseRunResult(bogus);
        assert.fail("expected CarbideSchemaError");
    } catch (e) {
        assert.ok(e instanceof CarbideSchemaError, `expected CarbideSchemaError, got ${e}`);
        assert.equal(e.expected, SCHEMA_VERSION);
        assert.equal(e.got, 9999);
    }
});

test("parseRunResult accepts a valid payload", () => {
    const good = JSON.stringify({
        schemaVersion: SCHEMA_VERSION,
        success: true,
        stdOut: "hi",
        stdErr: "",
        durationMs: 1,
        diagnostics: [],
    });
    const parsed = parseRunResult(good);
    assert.equal(parsed.stdOut, "hi");
});
