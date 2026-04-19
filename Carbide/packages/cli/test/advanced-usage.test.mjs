import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, writeFileSync, existsSync, rmSync, mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { parseJsonTrailer } from "./_helpers.mjs";

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

test("run --source - reads one source from stdin", () => {
    const run = runCarbide(["run", "--source", "-", "--format", "json"], {
        input: `Console.Write("stdin");\n`,
    });
    assert.equal(run.status, 0, `run failed: ${run.stderr}`);
    const payload = parseJsonTrailer(run.stdout);
    assert.equal(payload.success, true);
    assert.equal(payload.stdOut, "stdin");
});

test("run parses '-- <program args>' but does not forward argv yet", () => {
    const run = runCarbide(["run", "--source", "-", "--format", "json", "--", "one", "two"], {
        input: `Console.Write(args.Length);\n`,
    });
    assert.equal(run.status, 0, `run failed: ${run.stderr}`);
    const payload = parseJsonTrailer(run.stdout);
    assert.equal(payload.success, true);
    assert.equal(payload.stdOut, "0");
});

test("json trailer stays parseable even if the user program writes to stdout via OpenStandardOutput", () => {
    const src = `
using System.Text;

using var stream = Console.OpenStandardOutput();
using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
writer.Write("raw");
writer.Flush();

Console.Write("captured");
`;

    const run = runCarbide(["run", "--source", "-", "--format", "json"], { input: src });
    assert.equal(run.status, 0, `run failed: ${run.stderr}`);
    // The raw bytes may appear in stdout *before* the JSON trailer.
    assert.match(run.stdout, /raw/);
    const payload = parseJsonTrailer(run.stdout);
    assert.equal(payload.success, true);
    assert.equal(payload.stdOut, "captured");
});

test("build --no-debug omits the portable PDB", async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-cli-nodebug-"));
    t.after(() => rmSync(workDir, { recursive: true, force: true }));

    const source = path.join(workDir, "Lib.cs");
    writeFileSync(source, `namespace Demo; public static class Lib { public static int X() => 1; }\n`);

    const outDir = path.join(workDir, "out");
    const build = runCarbide([
        "build",
        "--source", source,
        "--assembly-name", "DemoLib",
        "--out", outDir,
        "--no-debug",
        "--format", "json",
    ]);
    assert.equal(build.status, 0, `build failed: ${build.stderr}`);
    const payload = parseJsonTrailer(build.stdout);
    assert.equal(payload.success, true);
    assert.ok(payload.pe && payload.pe.endsWith("DemoLib.dll"));
    assert.equal(payload.pdb, null);
    assert.ok(existsSync(path.join(outDir, "DemoLib.dll")));
    assert.equal(existsSync(path.join(outDir, "DemoLib.pdb")), false);
});

test("csproj ImplicitUsings=disable matches strict behavior", async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-cli-implicit-"));
    t.after(() => rmSync(workDir, { recursive: true, force: true }));

    const proj = path.join(workDir, "NoImplicit.csproj");
    writeFileSync(
        proj,
        `<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>NoImplicit</AssemblyName>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
</Project>`,
    );

    const program = path.join(workDir, "Program.cs");
    writeFileSync(program, `Console.Write("x");\n`);

    const v = runCarbide(["validate", "--project", proj, "--format", "json"]);
    assert.equal(v.status, 1, `validate should fail: ${v.stderr}`);
    const vPayload = parseJsonTrailer(v.stdout);
    assert.equal(vPayload.success, false);
    assert.ok(vPayload.diagnostics.some((d) => d.severity === "error" && d.id === "CS0103"));

    writeFileSync(program, `using System;\nConsole.Write("x");\n`);
    const run = runCarbide(["run", "--project", proj, "--format", "json"]);
    assert.equal(run.status, 0, `run should succeed: ${run.stderr}`);
    const runPayload = parseJsonTrailer(run.stdout);
    assert.equal(runPayload.success, true);
    assert.equal(runPayload.stdOut, "x");
});

test("csproj Compile Include/Remove globs control the source set", async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-cli-globs-"));
    t.after(() => rmSync(workDir, { recursive: true, force: true }));

    const srcDir = path.join(workDir, "src");
    mkdirSync(srcDir);

    writeFileSync(
        path.join(srcDir, "Program.cs"),
        `using Demo;\nConsole.Write(Helper.Msg());\n`,
    );
    writeFileSync(
        path.join(srcDir, "Extra.cs"),
        `namespace Demo; public static class Helper { public static string Msg() => "ok"; }\n`,
    );
    // If this file is included, we'd get CS8802 (multiple top-level statements).
    writeFileSync(path.join(srcDir, "Ignore.cs"), `Console.Write("ignore");\n`);

    const proj = path.join(workDir, "GlobDemo.csproj");
    writeFileSync(
        proj,
        `<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>GlobDemo</AssemblyName>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.cs" />
    <Compile Remove="src/Ignore.cs" />
  </ItemGroup>
</Project>`,
    );

    const run = runCarbide(["run", "--project", proj, "--format", "json"]);
    assert.equal(run.status, 0, `run failed: ${run.stderr}`);
    const payload = parseJsonTrailer(run.stdout);
    assert.equal(payload.success, true);
    assert.equal(payload.stdOut, "ok");
});

test("M9: ProjectReference is built and linked; App can call into Lib", async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-cli-projref-"));
    t.after(() => rmSync(workDir, { recursive: true, force: true }));

    const libDir = path.join(workDir, "Lib");
    const appDir = path.join(workDir, "App");
    mkdirSync(libDir);
    mkdirSync(appDir);

    writeFileSync(
        path.join(libDir, "Lib.csproj"),
        `<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>Lib</AssemblyName>
  </PropertyGroup>
</Project>`,
    );
    writeFileSync(path.join(libDir, "Thing.cs"), `namespace Lib; public static class Thing { public static int X() => 7; }\n`);

    writeFileSync(
        path.join(appDir, "App.csproj"),
        `<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>App</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\\\\Lib\\\\Lib.csproj" />
  </ItemGroup>
</Project>`,
    );
    writeFileSync(
        path.join(appDir, "Program.cs"),
        `using Lib;\nConsole.Write(Thing.X());\n`,
    );

    const run = runCarbide(["run", "--project", path.join(appDir, "App.csproj"), "--format", "json"]);
    assert.equal(run.status, 0, `expected success, got ${run.status}: ${run.stderr}`);
    const payload = parseJsonTrailer(run.stdout);
    assert.equal(payload.success, true);
    assert.equal(payload.stdOut, "7");
    // M9 consumes the <ProjectReference> — MSBLITE014 must no longer appear.
    assert.ok(
        !(payload.warnings ?? []).some((w) => w.code === "MSBLITE014"),
        `MSBLITE014 should be suppressed after M9, got: ${JSON.stringify(payload.warnings)}`,
    );
});

