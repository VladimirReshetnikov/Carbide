// M12 — `--analyzer <path>` registers a Roslyn source generator and attaches it to the
// project being compiled, in both input modes.

import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, writeFileSync, rmSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { parseJsonBySentinel } from "./_helpers.mjs";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const CLI = path.resolve(HERE, "..", "dist", "bin", "carbide.js");
const GENERATOR = path.resolve(
    HERE,
    "..",
    "..",
    "core",
    "test",
    "fixtures",
    "generator-dll",
    "CarbideTestGenerator.dll",
);
const HELPER = path.resolve(
    HERE,
    "..",
    "..",
    "core",
    "test",
    "fixtures",
    "helper-dll",
    "MyHelper.dll",
);

// Top-level statements must precede type declarations, hence the ordering.
const PROGRAM = [
    "Console.Write(new Point { X = 3, Y = 4 });",
    "",
    "[CarbideTest.GenerateToString]",
    "public partial class Point",
    "{",
    "    public int X { get; set; }",
    "    public int Y { get; set; }",
    "}",
].join("\n");

function runCarbide(args, options = {}) {
    const result = spawnSync(process.execPath, [CLI, ...args], {
        encoding: "utf8",
        shell: false,
        ...options,
    });
    return { status: result.status, stdout: result.stdout ?? "", stderr: result.stderr ?? "" };
}

function requireFixtures() {
    if (!existsSync(GENERATOR)) {
        throw new Error(
            `${GENERATOR} not found. Run \`npm run build:test-fixtures\` in packages/core first.`,
        );
    }
}

test("M12: --analyzer runs a source generator in --source mode", async (t) => {
    requireFixtures();
    const work = mkdtempSync(path.join(tmpdir(), "carbide-m12-src-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(path.join(work, "P.cs"), PROGRAM);

    const r = runCarbide([
        "run",
        "--source", path.join(work, "P.cs"),
        "--analyzer", GENERATOR,
        "--format", "human",
    ]);
    assert.equal(r.status, 0, r.stderr);
    assert.equal(r.stdout, "Point { X = 3, Y = 4 }");
});

test("M12: the same sources fail without --analyzer", async (t) => {
    requireFixtures();
    const work = mkdtempSync(path.join(tmpdir(), "carbide-m12-noanalyzer-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(path.join(work, "P.cs"), PROGRAM);

    const r = runCarbide(["run", "--source", path.join(work, "P.cs"), "--format", "json"]);
    assert.equal(r.status, 1);
    const payload = parseJsonBySentinel(r.stdout);
    assert.equal(payload.success, false);
    assert.ok(
        payload.diagnostics.some((d) => d.message.includes("CarbideTest")),
        `expected a diagnostic naming the ungenerated attribute, got ${JSON.stringify(payload.diagnostics)}`,
    );
});

test("M12: --analyzer runs the generator in --project mode", async (t) => {
    requireFixtures();
    const work = mkdtempSync(path.join(tmpdir(), "carbide-m12-proj-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(
        path.join(work, "Gen.csproj"),
        "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
            "<AssemblyName>Gen</AssemblyName></PropertyGroup></Project>",
    );
    writeFileSync(path.join(work, "P.cs"), PROGRAM);

    const r = runCarbide([
        "run",
        "--project", path.join(work, "Gen.csproj"),
        "--analyzer", GENERATOR,
        "--format", "human",
    ]);
    assert.equal(r.status, 0, r.stderr);
    assert.equal(r.stdout, "Point { X = 3, Y = 4 }");
});

test("M12: carbide build emits an assembly carrying the generated code", async (t) => {
    requireFixtures();
    const work = mkdtempSync(path.join(tmpdir(), "carbide-m12-build-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(path.join(work, "P.cs"), PROGRAM);
    const outDir = path.join(work, "out");

    const built = runCarbide([
        "build",
        "--source", path.join(work, "P.cs"),
        "--analyzer", GENERATOR,
        "--assembly-name", "GenApp",
        "--out", outDir,
        "--format", "json",
    ]);
    assert.equal(built.status, 0, built.stderr);
    assert.ok(existsSync(path.join(outDir, "GenApp.dll")), "expected the emitted assembly");
});

test("M12: carbide validate accepts generator-dependent sources", async (t) => {
    requireFixtures();
    const work = mkdtempSync(path.join(tmpdir(), "carbide-m12-validate-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(path.join(work, "P.cs"), PROGRAM);

    const r = runCarbide([
        "validate",
        "--source", path.join(work, "P.cs"),
        "--analyzer", GENERATOR,
        "--format", "json",
    ]);
    assert.equal(r.status, 0, r.stderr);
});

test("M12: --analyzer naming a DLL with no generator fails loudly", async (t) => {
    requireFixtures();
    const work = mkdtempSync(path.join(tmpdir(), "carbide-m12-bad-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(path.join(work, "P.cs"), "Console.Write(1);");

    // Pointing --analyzer at an ordinary library must not quietly compile to a program that
    // simply lacks the generated code — the whole point of a generator flag is that its
    // output is load-bearing.
    const r = runCarbide([
        "run",
        "--source", path.join(work, "P.cs"),
        "--analyzer", HELPER,
        "--format", "human",
    ]);
    assert.notEqual(r.status, 0);
    assert.match(r.stderr, /no usable source generator/i);
});
