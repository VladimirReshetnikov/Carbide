// P0.1 — how `carbide run` surfaces the core's MSCAP00* capture-bypass advisories.
//
// The advisories ride along in `RunResult.diagnostics` even on runs that compiled and
// executed, so the CLI has to route them into `warnings` rather than treating any
// non-empty diagnostics array as a compile failure.

import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { parseJsonBySentinel } from "./_helpers.mjs";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const CLI = path.resolve(HERE, "..", "dist", "bin", "carbide.js");

function runCarbide(args, options = {}) {
    const result = spawnSync(process.execPath, [CLI, ...args], {
        encoding: "utf8",
        shell: false,
        ...options,
    });
    return { status: result.status, stdout: result.stdout ?? "", stderr: result.stderr ?? "" };
}

const BYPASS_PROGRAM = [
    "using System.Text;",
    'Console.WriteLine("captured");',
    "var raw = Console.OpenStandardOutput();",
    'var bytes = Encoding.UTF8.GetBytes("escaped" + Environment.NewLine);',
    "raw.Write(bytes, 0, bytes.Length);",
    "raw.Flush();",
].join("\n");

test("MSCAP001 surfaces as a warning on a successful JSON run", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-mscap-json-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(path.join(work, "P.cs"), BYPASS_PROGRAM);

    const r = runCarbide(["run", "--source", path.join(work, "P.cs")]);
    assert.equal(r.status, 0, r.stderr);

    const payload = parseJsonBySentinel(r.stdout);
    assert.equal(payload.success, true, JSON.stringify(payload));
    const advisory = payload.warnings.find((w) => w.code === "MSCAP001");
    assert.ok(advisory, `expected an MSCAP001 warning, got ${JSON.stringify(payload.warnings)}`);
    assert.equal(advisory.severity, "warning");
    assert.match(advisory.message, /OpenStandardOutput/);
    // A successful run must not be reshaped into the compile-failure payload.
    assert.equal(payload.diagnostics, undefined);
});

test("MSCAP001 surfaces on stderr in human format, leaving stdout to the program", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-mscap-human-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(path.join(work, "P.cs"), BYPASS_PROGRAM);

    const r = runCarbide(["run", "--source", path.join(work, "P.cs"), "--format", "human"]);
    assert.equal(r.status, 0, r.stderr);
    assert.match(r.stderr, /MSCAP001/);
    assert.match(r.stdout, /captured/);
});

test("a throwing program that also bypasses capture still reports as a runtime failure", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-mscap-throw-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(
        path.join(work, "P.cs"),
        [
            "var raw = Console.OpenStandardOutput();",
            "System.GC.KeepAlive(raw);",
            'throw new InvalidOperationException("boom");',
        ].join("\n"),
    );

    const r = runCarbide(["run", "--source", path.join(work, "P.cs")]);
    const payload = parseJsonBySentinel(r.stdout);
    assert.equal(payload.success, false);
    // The regression this guards: keying the failure branch on `diagnostics.length` would
    // have produced the compile-failure payload (exit 1, no uncaughtException) instead.
    assert.ok(payload.uncaughtException, `expected uncaughtException, got ${JSON.stringify(payload)}`);
    assert.match(payload.uncaughtException, /InvalidOperationException/);
    assert.ok(payload.warnings.some((w) => w.code === "MSCAP001"));
});

test("a clean program produces no capture warnings", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-mscap-clean-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(path.join(work, "P.cs"), `Console.WriteLine("hello");`);

    const r = runCarbide(["run", "--source", path.join(work, "P.cs")]);
    assert.equal(r.status, 0, r.stderr);
    const payload = parseJsonBySentinel(r.stdout);
    assert.deepEqual(payload.warnings, []);
});
