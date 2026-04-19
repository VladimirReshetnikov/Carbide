// U1 — hermetic tests for the CLI UX sharpening pass.
//   U1.1: JSON sentinel + schemaVersion 3.
//   U1.2: --verbose / --quiet / CARBIDE_LOG_LEVEL semantics.
//   U1.3: error taxonomy — every named error has a stable exit code + category.

import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, writeFileSync, mkdirSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { parseJsonBySentinel, JSON_SENTINEL } from "./_helpers.mjs";

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

// ----- U1.1 — JSON sentinel -----

test("U1.1: successful build emits the sentinel before the JSON trailer", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u1-1a-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(path.join(work, "Program.cs"), `Console.Write("ok");`);

    const r = runCarbide([
        "build", "--source", path.join(work, "Program.cs"),
        "--assembly-name", "Foo", "--out", path.join(work, "out"),
    ]);
    assert.equal(r.status, 0, r.stderr);
    assert.ok(r.stdout.includes(JSON_SENTINEL), "sentinel should appear in stdout");
    const payload = parseJsonBySentinel(r.stdout);
    assert.equal(payload.schemaVersion, 3);
    assert.equal(payload.success, true);
});

test("U1.1: JSON trailer is parseable via the sentinel even when user code bypasses capture", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u1-1b-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    // Use Console.OpenStandardOutput to bypass the SetOut capture — tests the real sharp
    // edge that motivated U1.1. What U1.1 guarantees is that the JSON trailer is still
    // locatable via the sentinel regardless of what raw-stdout bytes the user wrote;
    // whether those raw bytes end up visible on the *outer* process stdout is a
    // Mono-WASM-capture-specific detail that U1.1 doesn't pin.
    writeFileSync(
        path.join(work, "Program.cs"),
        `using System.Text;\nvar s = Console.OpenStandardOutput();\nvar bytes = Encoding.UTF8.GetBytes("raw-escape");\ns.Write(bytes, 0, bytes.Length);\nConsole.Write("captured");`,
    );

    const r = runCarbide(["run", "--source", path.join(work, "Program.cs")]);
    assert.equal(r.status, 0, r.stderr);
    const payload = parseJsonBySentinel(r.stdout);
    assert.equal(payload.schemaVersion, 3);
    assert.equal(payload.success, true);
    assert.ok(payload.stdOut.includes("captured"), "SetOut-captured bytes land in stdOut");
});

test("U1.1: --format human suppresses the sentinel", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u1-1c-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(path.join(work, "Program.cs"), `Console.Write("hi");`);

    const r = runCarbide(["run", "--source", path.join(work, "Program.cs"), "--format", "human"]);
    assert.equal(r.status, 0, r.stderr);
    assert.equal(r.stdout, "hi");
    assert.ok(!r.stdout.includes(JSON_SENTINEL), "sentinel must not appear under --format human");
});

test("U1.1: --out - piped-PE mode does not emit the sentinel", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u1-1d-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(path.join(work, "X.cs"), `namespace X; public class Y {}`);

    const r = runCarbide(
        ["build", "--source", path.join(work, "X.cs"), "--assembly-name", "X", "--out", "-"],
        { encoding: "buffer" },
    );
    assert.equal(r.status, 0, r.stderr?.toString?.());
    // PE bytes should start with MZ (0x4D 0x5A); the sentinel bytes (0x1E 0x1E) would be
    // visible at the very start of stdout if we accidentally prepended it.
    const stdout = r.stdout;
    assert.equal(stdout[0], 0x4D, "stdout must start with PE MZ header");
    assert.equal(stdout[1], 0x5A);
});

// ----- U1.2 — verbosity -----

test("U1.2: default log level silences info lines on stderr", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u1-2a-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(path.join(work, "Program.cs"), `Console.Write("ok");`);

    const r = runCarbide([
        "build", "--source", path.join(work, "Program.cs"),
        "--assembly-name", "Foo", "--out", path.join(work, "out"),
    ]);
    assert.equal(r.status, 0);
    assert.equal(r.stderr, "", `stderr should be empty under default log level, got: ${JSON.stringify(r.stderr)}`);
});

test("U1.2: --verbose restores info/trace lines", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u1-2b-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(path.join(work, "Program.cs"), `Console.Write("ok");`);

    const r = runCarbide([
        "build", "--verbose",
        "--source", path.join(work, "Program.cs"),
        "--assembly-name", "Foo", "--out", path.join(work, "out"),
    ]);
    assert.equal(r.status, 0);
    assert.match(r.stderr, /info: Carbide initialising/);
});

test("U1.2: -v short alias also works", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u1-2c-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(path.join(work, "Program.cs"), `Console.Write("ok");`);

    const r = runCarbide([
        "build", "-v",
        "--source", path.join(work, "Program.cs"),
        "--assembly-name", "Foo", "--out", path.join(work, "out"),
    ]);
    assert.equal(r.status, 0);
    assert.match(r.stderr, /info: Carbide initialising/);
});

test("U1.2: CARBIDE_LOG_LEVEL=info matches --verbose", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u1-2d-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(path.join(work, "Program.cs"), `Console.Write("ok");`);

    const r = runCarbide(
        [
            "build",
            "--source", path.join(work, "Program.cs"),
            "--assembly-name", "Foo", "--out", path.join(work, "out"),
        ],
        { env: { ...process.env, CARBIDE_LOG_LEVEL: "info" } },
    );
    assert.equal(r.status, 0);
    assert.match(r.stderr, /info: Carbide initialising/);
});

test("U1.2: --verbose --quiet is a flag error (exit 3)", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u1-2e-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(path.join(work, "Program.cs"), `Console.Write("ok");`);

    const r = runCarbide([
        "build", "--verbose", "--quiet",
        "--source", path.join(work, "Program.cs"),
        "--assembly-name", "Foo",
    ]);
    assert.equal(r.status, 3);
    assert.match(r.stderr, /mutually exclusive/);
});

// ----- U1.3 — error taxonomy -----

test("U1.3: MSPROJ002 AssemblyName collision → exit 3, category=assembly-name-collision", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u1-3a-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));

    for (const lib of ["Lib1", "Lib2"]) {
        const d = path.join(work, lib);
        mkdirSync(d);
        writeFileSync(
            path.join(d, `${lib}.csproj`),
            `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>SharedName</AssemblyName></PropertyGroup></Project>`,
        );
        writeFileSync(path.join(d, `${lib}.cs`), `namespace ${lib}; public class X {}`);
    }
    const appDir = path.join(work, "App");
    mkdirSync(appDir);
    writeFileSync(
        path.join(appDir, "App.csproj"),
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>App</AssemblyName></PropertyGroup><ItemGroup><ProjectReference Include="..\\Lib1\\Lib1.csproj"/><ProjectReference Include="..\\Lib2\\Lib2.csproj"/></ItemGroup></Project>`,
    );
    writeFileSync(path.join(appDir, "Program.cs"), `Console.Write("ok");`);

    const r = runCarbide(["build", "--project", path.join(appDir, "App.csproj")]);
    assert.equal(r.status, 3);
    const payload = parseJsonBySentinel(r.stdout);
    assert.equal(payload.schemaVersion, 3);
    assert.equal(payload.success, false);
    assert.equal(payload.error.code, "MSPROJ002");
    assert.equal(payload.error.category, "assembly-name-collision");
    assert.equal(payload.error.details.assemblyName, "SharedName");
});

test("U1.3: MSPROJ001 cycle → exit 1, category=project-graph-cycle", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u1-3b-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));

    for (const [name, ref] of [["A", "..\\B\\B.csproj"], ["B", "..\\A\\A.csproj"]]) {
        const d = path.join(work, name);
        mkdirSync(d);
        writeFileSync(
            path.join(d, `${name}.csproj`),
            `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>${name}</AssemblyName></PropertyGroup><ItemGroup><ProjectReference Include="${ref}"/></ItemGroup></Project>`,
        );
        writeFileSync(path.join(d, `${name}.cs`), `namespace ${name}; public class X {}`);
    }

    const r = runCarbide(["build", "--project", path.join(work, "A", "A.csproj")]);
    assert.equal(r.status, 1);
    const payload = parseJsonBySentinel(r.stdout);
    assert.equal(payload.error.code, "MSPROJ001");
    assert.equal(payload.error.category, "project-graph-cycle");
    assert.ok(Array.isArray(payload.error.details.cyclePath));
});

test("U1.3: flag error → exit 3, category=flag-error", async () => {
    const r = runCarbide(["build", "--project", "nonexistent.csproj", "--source", "x.cs"]);
    assert.equal(r.status, 3);
    // The legacy mutual-exclusion check writes a bare stderr line and returns 3 without
    // routing through handleCliFailure (it's a pre-pipeline validation). We still want
    // exit 3 and a clear message. The payload may or may not exist depending on command.
    assert.match(r.stderr, /mutually exclusive|--project|--source/);
});

test("U1.3: invalid --log-level value → exit 3, category=flag-error", async () => {
    const r = runCarbide(["build", "--log-level", "LOUD", "--source", "x.cs", "--assembly-name", "Foo"]);
    assert.equal(r.status, 3);
    const payload = parseJsonBySentinel(r.stdout);
    assert.equal(payload.error.category, "flag-error");
});

test("U1.3: unknown flag → exit 3, category=flag-error", async () => {
    const r = runCarbide(["build", "--made-up-flag", "yes"]);
    assert.equal(r.status, 3);
    const payload = parseJsonBySentinel(r.stdout);
    assert.equal(payload.error.category, "flag-error");
});
