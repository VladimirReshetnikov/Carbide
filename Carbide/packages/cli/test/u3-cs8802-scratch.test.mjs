// U3.3 CS8802 hint + U3.4 --scratch flag hermetic tests.

import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, writeFileSync, mkdirSync, existsSync, rmSync } from "node:fs";
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

// ----- U3.3 — CS8802 hint -----

test("U3.3: CS8802 in --project mode triggers CARBIDE_HINT_CS8802 info diagnostic", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u3-cs8802-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(
        path.join(work, "Foo.csproj"),
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Foo</AssemblyName></PropertyGroup></Project>`,
    );
    writeFileSync(path.join(work, "A.cs"), `Console.Write("a");`);
    writeFileSync(path.join(work, "B.cs"), `Console.Write("b");`);

    const r = runCarbide(["build", "--project", path.join(work, "Foo.csproj"), "--out", path.join(work, "out")]);
    assert.equal(r.status, 1);
    const p = parseJsonBySentinel(r.stdout);
    const hasCs8802 = p.diagnostics.some((d) => d.id === "CS8802");
    const hasHint = p.diagnostics.some((d) => d.id === "CARBIDE_HINT_CS8802");
    assert.ok(hasCs8802, `expected CS8802 in: ${JSON.stringify(p.diagnostics.map((d) => d.id))}`);
    assert.ok(hasHint, `expected CARBIDE_HINT_CS8802 in: ${JSON.stringify(p.diagnostics.map((d) => d.id))}`);
});

test("U3.3: validate --project also attaches the CS8802 hint", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u3-cs8802-validate-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(
        path.join(work, "Foo.csproj"),
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Foo</AssemblyName></PropertyGroup></Project>`,
    );
    writeFileSync(path.join(work, "A.cs"), `Console.Write("a");`);
    writeFileSync(path.join(work, "B.cs"), `Console.Write("b");`);

    const r = runCarbide(["validate", "--project", path.join(work, "Foo.csproj")]);
    assert.equal(r.status, 1);
    const p = parseJsonBySentinel(r.stdout);
    const hasHint = p.diagnostics.some((d) => d.id === "CARBIDE_HINT_CS8802");
    assert.ok(hasHint, `expected CARBIDE_HINT_CS8802 in: ${JSON.stringify(p.diagnostics.map((d) => d.id))}`);
});

test("U3.3: clean build (no CS8802) does not attach the hint", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u3-nohint-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(
        path.join(work, "Foo.csproj"),
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Foo</AssemblyName></PropertyGroup></Project>`,
    );
    writeFileSync(path.join(work, "Program.cs"), `Console.Write("only one");`);

    const r = runCarbide(["build", "--project", path.join(work, "Foo.csproj"), "--out", path.join(work, "out")]);
    assert.equal(r.status, 0, r.stderr);
    const p = parseJsonBySentinel(r.stdout);
    assert.ok(!p.diagnostics.some((d) => d.id === "CARBIDE_HINT_CS8802"));
});

// ----- U3.4 — --scratch -----

test("U3.4: --scratch combines --project with --source (adds to root)", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u3-scratch-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(
        path.join(work, "Foo.csproj"),
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Foo</AssemblyName><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup></Project>`,
    );
    writeFileSync(path.join(work, "Extra.cs"), `Console.Write("scratch");`);

    const r = runCarbide([
        "run",
        "--project", path.join(work, "Foo.csproj"),
        "--source", path.join(work, "Extra.cs"),
        "--scratch",
        "--format", "human",
    ]);
    assert.equal(r.status, 0, r.stderr);
    assert.equal(r.stdout, "scratch");
});

test("U3.4: --project + --source WITHOUT --scratch still exits 3", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u3-noscratch-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(
        path.join(work, "Foo.csproj"),
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Foo</AssemblyName></PropertyGroup></Project>`,
    );
    writeFileSync(path.join(work, "Extra.cs"), `Console.Write("x");`);

    const r = runCarbide([
        "build",
        "--project", path.join(work, "Foo.csproj"),
        "--source", path.join(work, "Extra.cs"),
    ]);
    assert.equal(r.status, 3);
    assert.match(r.stderr, /mutually exclusive|--scratch/);
});

test("U3.4: --scratch without --project exits 3", async () => {
    const r = runCarbide(["build", "--scratch", "--source", "x.cs", "--assembly-name", "Foo"]);
    assert.equal(r.status, 3);
    assert.match(r.stderr, /--scratch.*--project|--project.*--scratch/);
});
