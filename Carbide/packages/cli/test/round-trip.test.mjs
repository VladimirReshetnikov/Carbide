// M4.8 CLI acceptance: build a library, reference its emitted DLL from a second source,
// run the resulting program. Mirrors the two-stage flow in the M4 plan §2.2.
import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, writeFileSync, readFileSync, existsSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import path from "node:path";

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

test("carbide build emits a DLL that carbide run can reference", async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-roundtrip-"));
    t.after(() => rmSync(workDir, { recursive: true, force: true }));

    // Stage 1: write Thing.cs and build it into MyLib.dll + MyLib.pdb.
    const thingPath = path.join(workDir, "Thing.cs");
    writeFileSync(
        thingPath,
        `namespace MyLib;\npublic static class Thing { public static string Describe(int v) => $"Thing<{v}>"; }\n`,
    );

    const outDir = path.join(workDir, "lib");
    const build = runCarbide([
        "build",
        "--source", thingPath,
        "--assembly-name", "MyLib",
        "--out", outDir,
        "--format", "json",
    ]);
    assert.equal(build.status, 0, `build failed: stdout=${build.stdout} stderr=${build.stderr}`);
    const buildSummary = JSON.parse(build.stdout.trim());
    assert.equal(buildSummary.success, true);
    assert.equal(buildSummary.assemblyName, "MyLib");
    assert.ok(buildSummary.pe && buildSummary.pe.endsWith("MyLib.dll"));
    assert.ok(buildSummary.pdb && buildSummary.pdb.endsWith("MyLib.pdb"));

    // Verify the DLL exists and has the PE magic.
    const dllPath = path.join(outDir, "MyLib.dll");
    assert.ok(existsSync(dllPath), `expected ${dllPath} to exist`);
    const peBytes = readFileSync(dllPath);
    assert.equal(peBytes[0], 0x4d, "dll[0] should be 'M'");
    assert.equal(peBytes[1], 0x5a, "dll[1] should be 'Z'");

    // Stage 2: write Program.cs that uses MyLib, run with --ref MyLib.dll.
    const programPath = path.join(workDir, "Program.cs");
    writeFileSync(
        programPath,
        `using MyLib;\nConsole.Write(Thing.Describe(42));\n`,
    );

    const run = runCarbide([
        "run",
        "--source", programPath,
        "--ref", dllPath,
        "--assembly-name", "MyApp",
        "--format", "human",
    ]);
    assert.equal(run.status, 0, `run failed: stdout=${run.stdout} stderr=${run.stderr}`);
    assert.equal(run.stdout, "Thing<42>");
});

test("carbide validate exits 0 for clean source, non-zero for broken source", async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-validate-"));
    t.after(() => rmSync(workDir, { recursive: true, force: true }));

    const clean = path.join(workDir, "Clean.cs");
    writeFileSync(clean, `Console.WriteLine("ok");\n`);

    const ok = runCarbide(["validate", "--source", clean, "--format", "json"]);
    assert.equal(ok.status, 0, `validate clean: ${ok.stderr}`);
    const payload = JSON.parse(ok.stdout.trim());
    assert.equal(payload.success, true);

    const broken = path.join(workDir, "Broken.cs");
    writeFileSync(broken, `class X {\n`);

    const bad = runCarbide(["validate", "--source", broken, "--format", "json"]);
    assert.equal(bad.status, 1, `validate broken should exit 1`);
    const badPayload = JSON.parse(bad.stdout.trim());
    assert.equal(badPayload.success, false);
    assert.ok(badPayload.diagnostics.some((d) => d.severity === "error"));
});

test("carbide build --out - writes PE bytes to stdout", async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-stdout-"));
    t.after(() => rmSync(workDir, { recursive: true, force: true }));

    const source = path.join(workDir, "Lib.cs");
    writeFileSync(source, `namespace S; public static class C { public static int X() => 1; }\n`);

    // spawnSync gives us stdout as a Buffer when encoding is unset.
    const result = spawnSync(process.execPath, [
        CLI, "build",
        "--source", source,
        "--assembly-name", "S",
        "--out", "-",
    ], { shell: false });
    assert.equal(result.status, 0, `build --out - failed`);
    assert.ok(Buffer.isBuffer(result.stdout));
    assert.ok(result.stdout.length > 0);
    assert.equal(result.stdout[0], 0x4d, "stdout[0] should be 'M' (PE magic)");
    assert.equal(result.stdout[1], 0x5a, "stdout[1] should be 'Z'");
});
