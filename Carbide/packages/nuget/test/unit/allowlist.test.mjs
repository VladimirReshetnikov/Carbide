import { test } from "node:test";
import assert from "node:assert/strict";
import { ALLOW_LIST, isAllowed, getEntry } from "../../dist/allowlist.js";

test("ALLOW_LIST has exactly the 10 seeded packages", () => {
    // M6 plan §5 D75: allow-list starts with 10 entries.
    assert.equal(ALLOW_LIST.length, 10);
    const ids = ALLOW_LIST.map((e) => e.id).sort();
    assert.deepEqual(ids, [
        "CsvHelper",
        "FluentAssertions",
        "Handlebars.Net",
        "Humanizer.Core",
        "NodaTime",
        "Newtonsoft.Json",
        "Scriban",
        "Serilog",
        "Serilog.Sinks.Console",
        "YamlDotNet",
    ].sort());
});

test("ALLOW_LIST is frozen (immutable)", () => {
    assert.ok(Object.isFrozen(ALLOW_LIST));
});

test("every allow-list entry carries the expected shape", () => {
    for (const entry of ALLOW_LIST) {
        assert.equal(typeof entry.id, "string");
        assert.ok(entry.id.length > 0);
        assert.equal(typeof entry.lastVerified, "string");
        assert.match(entry.lastVerified, /^\d+\.\d+\.\d+/);
        assert.equal(typeof entry.description, "string");
        assert.ok(entry.description.length > 0);
        assert.match(entry.source, /^https:\/\/www\.nuget\.org\/packages\//);
    }
});

test("isAllowed: exact match", () => {
    assert.equal(isAllowed("Newtonsoft.Json"), true);
    assert.equal(isAllowed("Serilog"), true);
});

test("isAllowed: case-insensitive", () => {
    assert.equal(isAllowed("newtonsoft.json"), true);
    assert.equal(isAllowed("NEWTONSOFT.JSON"), true);
    assert.equal(isAllowed("NeWtOnSoFt.JsOn"), true);
    assert.equal(isAllowed("serilog.sinks.CONSOLE"), true);
});

test("isAllowed: unknown package returns false", () => {
    assert.equal(isAllowed("System.Text.Json"), false);
    assert.equal(isAllowed("NotInTheAllowList"), false);
    assert.equal(isAllowed(""), false);
});

test("getEntry: returns the frozen entry for an allowed id", () => {
    const entry = getEntry("newtonsoft.json");
    assert.ok(entry);
    assert.equal(entry.id, "Newtonsoft.Json");
    assert.equal(entry.lastVerified, "13.0.3");
    assert.equal(entry.source, "https://www.nuget.org/packages/Newtonsoft.Json");
});

test("getEntry: returns undefined for unknown id", () => {
    assert.equal(getEntry("System.Text.Json"), undefined);
});
