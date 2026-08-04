// `carbide.lock.json` exists so an identical graph produces an identical file. It is
// committed to source control, so any nondeterminism turns every re-resolve into a diff and
// buries the real changes.

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, rmSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { buildLock, writeLock, readLock, LOCK_SCHEMA_VERSION } from "../../dist/lock.js";

const pkg = (id, version, extra = {}) => ({
    id,
    version,
    sha256: "0".repeat(64),
    requestedBy: ["<root>"],
    dependencies: [],
    libFolder: "net10.0",
    ...extra,
});

test("the same graph produces byte-identical output", () => {
    const packages = [pkg("Serilog", "3.1.1"), pkg("Newtonsoft.Json", "13.0.3")];
    const first = JSON.stringify(buildLock(packages, []));
    const second = JSON.stringify(buildLock(packages, []));
    assert.equal(first, second);
    // No timestamp: it would differ between the two calls above and on every later resolve.
    assert.equal(buildLock(packages, []).generatedAt, undefined);
});

test("input order does not affect output order", () => {
    const forwards = [pkg("A", "1.0.0"), pkg("B", "1.0.0"), pkg("C", "1.0.0")];
    const backwards = [...forwards].reverse();
    assert.equal(
        JSON.stringify(buildLock(forwards, [])),
        JSON.stringify(buildLock(backwards, [])),
    );
});

test("ordering is ordinal, so it cannot vary with the host locale", () => {
    // `localeCompare` collates punctuation and case, placing these differently — and its
    // answer depends on ICU locale data, so two machines could disagree.
    const ids = ["Serilog", "Serilog.Sinks.Console", "SerilogTimings", "a.b", "ab"];
    const lock = buildLock(ids.map((id) => pkg(id, "1.0.0")), []);
    assert.deepEqual(
        lock.packages.map((p) => p.id),
        [...ids].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0)),
    );
    // Specifically: capitalised ids sort before lowercase ones, ordinally.
    assert.ok(lock.packages.findIndex((p) => p.id === "SerilogTimings") <
        lock.packages.findIndex((p) => p.id === "a.b"));
});

test("versions of one package are ordered too", () => {
    const lock = buildLock([pkg("A", "2.0.0"), pkg("A", "1.0.0")], []);
    assert.deepEqual(lock.packages.map((p) => p.version), ["1.0.0", "2.0.0"]);
});

test("requestedBy and dependencies are sorted within each entry", () => {
    const lock = buildLock(
        [pkg("A", "1.0.0", { requestedBy: ["Z", "B", "<root>"], dependencies: ["Y", "X"] })],
        [],
    );
    assert.deepEqual(lock.packages[0].requestedBy, ["<root>", "B", "Z"]);
    assert.deepEqual(lock.packages[0].dependencies, ["X", "Y"]);
});

test("a lock round-trips through disk unchanged", async (t) => {
    const dir = mkdtempSync(path.join(tmpdir(), "carbide-lock-"));
    t.after(() => rmSync(dir, { recursive: true, force: true }));
    const lockPath = path.join(dir, "carbide.lock.json");

    const lock = buildLock([pkg("B", "1.0.0"), pkg("A", "2.0.0")], [
        { code: "MSNUGET010", message: "example", severity: "warning" },
    ]);
    await writeLock(lockPath, lock);
    const readBack = await readLock(lockPath);
    assert.deepEqual(readBack, lock);

    // Writing what was read back must reproduce the same bytes.
    const firstBytes = readFileSync(lockPath, "utf8");
    await writeLock(lockPath, readBack);
    assert.equal(readFileSync(lockPath, "utf8"), firstBytes);
});

test("a lock carrying a legacy generatedAt still parses", async (t) => {
    const dir = mkdtempSync(path.join(tmpdir(), "carbide-lock-legacy-"));
    t.after(() => rmSync(dir, { recursive: true, force: true }));
    const lockPath = path.join(dir, "carbide.lock.json");
    const legacy = {
        schemaVersion: LOCK_SCHEMA_VERSION,
        generator: "carbide",
        generatedAt: "2026-04-18T00:00:00Z",
        packages: [pkg("A", "1.0.0")],
        warnings: [],
    };
    await writeLock(lockPath, legacy);
    const readBack = await readLock(lockPath);
    assert.equal(readBack.generatedAt, "2026-04-18T00:00:00Z");
    assert.equal(readBack.packages.length, 1);
});
