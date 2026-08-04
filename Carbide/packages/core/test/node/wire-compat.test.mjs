// M7 — compatibility freeze for the JSExport wire contract.
//
// The golden payloads under ../fixtures/wire/ record what a released Carbide puts on the
// boundary. This suite asserts the shipped parsers still accept every payload inside the
// documented acceptance window, still reject everything outside it, and still decode the
// fields consumers read. It runs without booting the Mono-WASM runtime: the parsers are
// pure TypeScript, so a wire regression is caught in milliseconds rather than in a
// full-stack test.

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { CarbideSchemaError, SCHEMA_VERSION } from "../../dist/index.js";
import { parseBuildResult, parseDiagnostics, parseRunResult } from "../../dist/interop/schema.js";

const fixtureDir = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "fixtures", "wire");
const readFixture = (name) => readFileSync(path.join(fixtureDir, name), "utf8");

/** Versions the TS parsers promise to accept: the current one and exactly one back-version. */
const acceptedVersions = [SCHEMA_VERSION - 1, SCHEMA_VERSION];

test("every frozen fixture is well-formed JSON at a documented schema version", () => {
    const fixtures = readdirSync(fixtureDir).filter((name) => name.endsWith(".json"));
    assert.ok(fixtures.length > 0, "no wire fixtures found");
    for (const name of fixtures) {
        const match = /\.v(\d+)\.json$/.exec(name);
        assert.ok(match, `${name}: fixture name must end with .v<schemaVersion>.json`);
        const declared = Number(match[1]);
        const payload = JSON.parse(readFixture(name));
        const carried = Array.isArray(payload) ? declared : payload.schemaVersion;
        assert.equal(
            carried,
            declared,
            `${name}: filename says v${declared} but the payload carries ${carried}`,
        );
        assert.ok(declared <= SCHEMA_VERSION, `${name}: fixture is newer than SCHEMA_VERSION`);
    }
});

test("parseRunResult decodes the frozen success payload", () => {
    const result = parseRunResult(readFixture("run-result-success.v5.json"));
    assert.equal(result.schemaVersion, 5);
    assert.equal(result.success, true);
    assert.equal(result.exitCode, 0);
    assert.equal(result.stdOut, "hello\n");
    assert.equal(result.stdErr, "");
    assert.equal(result.durationMs, 12.5);
    assert.deepEqual(result.diagnostics, []);
});

test("parseRunResult decodes the frozen uncaught-exception payload", () => {
    const result = parseRunResult(readFixture("run-result-uncaught.v5.json"));
    assert.equal(result.success, false);
    assert.equal(result.exitCode, null);
    assert.match(result.uncaughtException, /InvalidOperationException: boom/);
    assert.equal(result.stdOut, "before\n");
});

test("parseRunResult decodes the frozen compile-failure payload", () => {
    const result = parseRunResult(readFixture("run-result-compile-failure.v5.json"));
    assert.equal(result.success, false);
    assert.equal(result.diagnostics.length, 1);
    const [diagnostic] = result.diagnostics;
    assert.equal(diagnostic.id, "CS1002");
    assert.equal(diagnostic.severity, "error");
    assert.equal(diagnostic.path, "Program.cs");
    assert.equal(diagnostic.spanStart, 20);
    assert.equal(diagnostic.columnEnd, 20);
});

test("parseBuildResult decodes the frozen success payload, base64 included", () => {
    const result = parseBuildResult(readFixture("build-result-success.v5.json"));
    assert.equal(result.success, true);
    assert.deepEqual([...result.pe], [0x4d, 0x5a, 0x90, 0x00]);
    assert.deepEqual([...result.pdb], [0x42, 0x53, 0x4a, 0x42]);
    assert.equal(result.durationMs, 42);
    assert.equal(result.peSchemaVersion, 1);
    assert.equal(result.primaryAssemblyName, "Program");
});

test("parseBuildResult leaves PE/PDB undefined on the frozen failure payload", () => {
    const result = parseBuildResult(readFixture("build-result-failure.v5.json"));
    assert.equal(result.success, false);
    assert.equal(result.pe, undefined);
    assert.equal(result.pdb, undefined);
    // core-P3 fields are omitted (not null) on failure — consumers branch on `undefined`.
    assert.equal(result.peSchemaVersion, undefined);
    assert.equal(result.primaryAssemblyName, undefined);
    assert.equal(result.diagnostics[0].id, "CS0103");
});

test("parseDiagnostics decodes the frozen diagnostics array", () => {
    const diagnostics = parseDiagnostics(readFixture("diagnostics.v5.json"));
    assert.equal(diagnostics.length, 2);
    assert.deepEqual(
        diagnostics.map((d) => [d.id, d.severity]),
        [
            ["CS0219", "warning"],
            ["CS1002", "error"],
        ],
    );
});

test("the one-step back-version stays inside the acceptance window", () => {
    assert.deepEqual(acceptedVersions, [4, 5], "acceptance window drifted; update the frozen fixtures");
    const run = parseRunResult(readFixture("run-result-success.v4.json"));
    assert.equal(run.schemaVersion, 4);
    assert.equal(run.stdOut, "hello\n");
    const build = parseBuildResult(readFixture("build-result-success.v4.json"));
    assert.equal(build.schemaVersion, 4);
    assert.deepEqual([...build.pe], [0x4d, 0x5a, 0x90, 0x00]);
});

test("payloads outside the acceptance window are rejected on both sides", () => {
    const base = JSON.parse(readFixture("run-result-success.v5.json"));
    for (const version of [SCHEMA_VERSION - 2, SCHEMA_VERSION + 1, null, "5", undefined]) {
        const payload = { ...base, schemaVersion: version };
        assert.throws(
            () => parseRunResult(JSON.stringify(payload)),
            CarbideSchemaError,
            `schemaVersion ${JSON.stringify(version)} must not be accepted`,
        );
    }
});

test("parseDiagnostics rejects a non-array payload", () => {
    assert.throws(() => parseDiagnostics('{"id":"CS1002"}'), TypeError);
});

// Request payloads travel TS → C#. Nothing on the TypeScript side parses them, so the
// freeze asserts the field set instead: a field added to (or dropped from) a request
// interface without a SCHEMA_VERSION bump would silently change what C# receives.
const requestShapes = [
    ["project-options-request.v5.json", [
        "schemaVersion",
        "targetFramework",
        "languageVersion",
        "nullable",
        "implicitUsings",
        "assemblyName",
        "rootNamespace",
        "defineConstants",
    ]],
    ["run-options-request.v5.json", ["schemaVersion", "args", "stdin"]],
    ["run-interactive-options-request.v5.json", ["schemaVersion", "args", "stderrStyle"]],
    ["run-assembly-options-request.v5.json", [
        "schemaVersion",
        "peBase64",
        "referencesBase64",
        "args",
        "stdin",
    ]],
];

for (const [fixture, expectedFields] of requestShapes) {
    test(`${fixture} carries exactly its frozen field set`, () => {
        const payload = JSON.parse(readFixture(fixture));
        assert.deepEqual(Object.keys(payload).sort(), [...expectedFields].sort());
        assert.equal(payload.schemaVersion, SCHEMA_VERSION);
    });
}
