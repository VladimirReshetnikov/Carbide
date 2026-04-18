// M5.7 byte-identical acceptance + CLI --project wiring.
import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, writeFileSync, readFileSync, existsSync, rmSync } from "node:fs";
import { createHash } from "node:crypto";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import path from "node:path";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const CLI = path.resolve(HERE, "..", "dist", "bin", "carbide.js");

function sha256File(p) {
    const h = createHash("sha256");
    h.update(readFileSync(p));
    return h.digest("hex");
}

function runCarbide(args, options = {}) {
    const result = spawnSync(process.execPath, [CLI, ...args], {
        encoding: "utf8",
        shell: false,
        ...options,
    });
    return { status: result.status, stdout: result.stdout ?? "", stderr: result.stderr ?? "" };
}

test("carbide build --project reads .csproj and emits the expected DLL", async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-m5-"));
    t.after(() => rmSync(workDir, { recursive: true, force: true }));

    writeFileSync(
        path.join(workDir, "Foo.csproj"),
        `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>FooLib</AssemblyName>
  </PropertyGroup>
</Project>`,
    );
    writeFileSync(
        path.join(workDir, "Thing.cs"),
        `namespace FooLib; public static class Thing { public static string Describe(int v) => $"Thing<{v}>"; }`,
    );

    const outDir = path.join(workDir, "out");
    const build = runCarbide([
        "build",
        "--project", path.join(workDir, "Foo.csproj"),
        "--out", outDir,
        "--format", "json",
    ]);
    assert.equal(build.status, 0, `build failed: ${build.stderr}`);
    const summary = JSON.parse(build.stdout.trim());
    assert.equal(summary.success, true);
    assert.equal(summary.assemblyName, "FooLib");
    assert.ok(existsSync(path.join(outDir, "FooLib.dll")));
    assert.ok(existsSync(path.join(outDir, "FooLib.pdb")));
});

test("byte-identical PE: --project and equivalent --source flags produce the same bytes", async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-m5-byte-"));
    t.after(() => rmSync(workDir, { recursive: true, force: true }));

    const src = `namespace IdTest; public static class X { public static int V() => 7; }`;
    writeFileSync(path.join(workDir, "X.cs"), src);
    writeFileSync(
        path.join(workDir, "Foo.csproj"),
        `<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>IdTest</AssemblyName>
  </PropertyGroup>
</Project>`,
    );

    const projectOut = path.join(workDir, "project");
    const flagOut = path.join(workDir, "flag");

    const fromProject = runCarbide([
        "build", "--project", path.join(workDir, "Foo.csproj"),
        "--out", projectOut, "--format", "json",
    ]);
    assert.equal(fromProject.status, 0, fromProject.stderr);

    const fromFlags = runCarbide([
        "build", "--source", path.join(workDir, "X.cs"),
        "--assembly-name", "IdTest",
        "--out", flagOut, "--format", "json",
    ]);
    assert.equal(fromFlags.status, 0, fromFlags.stderr);

    const aHash = sha256File(path.join(projectOut, "IdTest.dll"));
    const bHash = sha256File(path.join(flagOut, "IdTest.dll"));
    assert.equal(aHash, bHash, "PE bytes must be byte-identical between --project and flattened flags");
});

test("two successive --project builds produce identical PE (reproducibility)", async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-m5-repro-"));
    t.after(() => rmSync(workDir, { recursive: true, force: true }));

    writeFileSync(
        path.join(workDir, "Foo.csproj"),
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Repro</AssemblyName></PropertyGroup></Project>`,
    );
    writeFileSync(path.join(workDir, "Thing.cs"), `namespace Repro; public static class T {}`);

    const outA = path.join(workDir, "A");
    const outC = path.join(workDir, "C");
    const a = runCarbide(["build", "--project", path.join(workDir, "Foo.csproj"), "--out", outA]);
    assert.equal(a.status, 0, a.stderr);
    const c = runCarbide(["build", "--project", path.join(workDir, "Foo.csproj"), "--out", outC]);
    assert.equal(c.status, 0, c.stderr);
    assert.equal(sha256File(path.join(outA, "Repro.dll")), sha256File(path.join(outC, "Repro.dll")));
});

test("carbide run --project executes the program", async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-m5-run-"));
    t.after(() => rmSync(workDir, { recursive: true, force: true }));

    writeFileSync(
        path.join(workDir, "Foo.csproj"),
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>RunApp</AssemblyName></PropertyGroup></Project>`,
    );
    writeFileSync(path.join(workDir, "Program.cs"), `Console.Write("project-ran");`);

    const run = runCarbide([
        "run", "--project", path.join(workDir, "Foo.csproj"),
        "--format", "human",
    ]);
    assert.equal(run.status, 0, `run failed: ${run.stderr}`);
    assert.equal(run.stdout, "project-ran");
});

test("carbide validate --project exits 0 for a clean project", async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-m5-val-"));
    t.after(() => rmSync(workDir, { recursive: true, force: true }));

    writeFileSync(
        path.join(workDir, "Foo.csproj"),
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>`,
    );
    writeFileSync(path.join(workDir, "Program.cs"), `Console.WriteLine("ok");`);

    const v = runCarbide(["validate", "--project", path.join(workDir, "Foo.csproj"), "--format", "json"]);
    assert.equal(v.status, 0, v.stderr);
    const payload = JSON.parse(v.stdout.trim());
    assert.equal(payload.success, true);
});

test("carbide build --project --source combined exits 3", () => {
    const r = runCarbide(["build", "--project", "foo.csproj", "--source", "x.cs"]);
    assert.equal(r.status, 3);
    assert.match(r.stderr, /mutually exclusive/);
});

test("PackageReference in .csproj surfaces as MSBLITE013 warning but build succeeds", async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-m5-pkg-"));
    t.after(() => rmSync(workDir, { recursive: true, force: true }));

    writeFileSync(
        path.join(workDir, "Foo.csproj"),
        `<Project>
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>PkgWarn</AssemblyName></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3"/>
  </ItemGroup>
</Project>`,
    );
    writeFileSync(path.join(workDir, "Program.cs"), `Console.Write("ok");`);

    const build = runCarbide([
        "build", "--project", path.join(workDir, "Foo.csproj"),
        "--out", path.join(workDir, "out"),
        "--format", "json",
    ]);
    assert.equal(build.status, 0, build.stderr);
    const payload = JSON.parse(build.stdout.trim());
    assert.equal(payload.success, true);
    assert.ok(
        (payload.warnings ?? []).some((w) => w.code === "MSBLITE013"),
        `expected MSBLITE013 warning, got: ${JSON.stringify(payload.warnings)}`,
    );
});

test("defineConstants from .csproj reaches the user's #if", async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-m5-def-"));
    t.after(() => rmSync(workDir, { recursive: true, force: true }));

    writeFileSync(
        path.join(workDir, "Foo.csproj"),
        `<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>DefDemo</AssemblyName>
    <DefineConstants>DEBUG;MY_FEATURE</DefineConstants>
  </PropertyGroup>
</Project>`,
    );
    writeFileSync(
        path.join(workDir, "Program.cs"),
        `#if MY_FEATURE\nConsole.Write("on");\n#else\nConsole.Write("off");\n#endif`,
    );

    const run = runCarbide([
        "run", "--project", path.join(workDir, "Foo.csproj"),
        "--format", "human",
    ]);
    assert.equal(run.status, 0, run.stderr);
    assert.equal(run.stdout, "on");
});
