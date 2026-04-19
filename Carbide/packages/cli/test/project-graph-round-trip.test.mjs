// M9 — hermetic integration tests for multi-project builds. Each test writes synthetic
// csproj + .cs files into a tmp dir and runs the full `carbide` CLI binary as a subprocess.
// No network. Matches the acceptance laid out in carbide-M9-detailed-plan §2.

import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, writeFileSync, mkdirSync, existsSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { parseJsonTrailer, parseJsonBySentinel } from "./_helpers.mjs";

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

function setupLibApp(workDir, { libSrc, appSrc, appCsprojExtras = "" } = {}) {
    const appDir = path.join(workDir, "App");
    const libDir = path.join(workDir, "Lib");
    mkdirSync(appDir);
    mkdirSync(libDir);
    writeFileSync(
        path.join(libDir, "Lib.csproj"),
        `<Project>\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <AssemblyName>MyLib</AssemblyName>\n  </PropertyGroup>\n</Project>\n`,
    );
    writeFileSync(
        path.join(libDir, "Greeting.cs"),
        libSrc ??
            `namespace MyLib;\npublic static class Greeting { public static string For(string n) => $"hello {n}"; }\n`,
    );
    writeFileSync(
        path.join(appDir, "App.csproj"),
        `<Project>\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <AssemblyName>App</AssemblyName>\n    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n  <ItemGroup>\n    <ProjectReference Include="..\\Lib\\Lib.csproj"/>\n  </ItemGroup>\n${appCsprojExtras}</Project>\n`,
    );
    writeFileSync(
        path.join(appDir, "Program.cs"),
        appSrc ?? `using MyLib;\nConsole.Write(Greeting.For("world"));\n`,
    );
    return { appDir, libDir, appCsproj: path.join(appDir, "App.csproj") };
}

test("M9.2.1: two-project fixture builds and runs, emits both PEs", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-m9-libapp-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    const { appCsproj } = setupLibApp(work);

    const outDir = path.join(work, "out");
    const build = runCarbide([
        "build", "--project", appCsproj, "--out", outDir, "--format", "json",
    ]);
    assert.equal(build.status, 0, `build failed: ${build.stderr}`);
    const buildPayload = parseJsonTrailer(build.stdout);
    assert.equal(buildPayload.success, true);
    assert.ok(existsSync(path.join(outDir, "App.dll")));
    assert.ok(existsSync(path.join(outDir, "MyLib.dll")));
    assert.ok(existsSync(path.join(outDir, "App.pdb")));
    assert.ok(existsSync(path.join(outDir, "MyLib.pdb")));

    const run = runCarbide(["run", "--project", appCsproj, "--format", "human"]);
    assert.equal(run.status, 0, `run failed: ${run.stderr}`);
    assert.equal(run.stdout, "hello world");
});

test("M9.2.2: transitive A -> Mid -> Lib; all three PEs emitted", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-m9-trans-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));

    const libDir = path.join(work, "Lib");
    const midDir = path.join(work, "Mid");
    const appDir = path.join(work, "App");
    mkdirSync(libDir);
    mkdirSync(midDir);
    mkdirSync(appDir);
    writeFileSync(
        path.join(libDir, "Lib.csproj"),
        `<Project>\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <AssemblyName>MyLib</AssemblyName>\n  </PropertyGroup>\n</Project>\n`,
    );
    writeFileSync(
        path.join(libDir, "Lib.cs"),
        `namespace MyLib; public static class L { public static string Base() => "base"; }\n`,
    );
    writeFileSync(
        path.join(midDir, "Mid.csproj"),
        `<Project>\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <AssemblyName>MyMid</AssemblyName>\n  </PropertyGroup>\n  <ItemGroup>\n    <ProjectReference Include="..\\Lib\\Lib.csproj"/>\n  </ItemGroup>\n</Project>\n`,
    );
    writeFileSync(
        path.join(midDir, "Mid.cs"),
        `namespace MyMid; public static class M { public static string Wrap(string n) => $"mid({MyLib.L.Base()},{n})"; }\n`,
    );
    writeFileSync(
        path.join(appDir, "App.csproj"),
        `<Project>\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <AssemblyName>App</AssemblyName>\n    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n  <ItemGroup>\n    <ProjectReference Include="..\\Mid\\Mid.csproj"/>\n  </ItemGroup>\n</Project>\n`,
    );
    writeFileSync(
        path.join(appDir, "Program.cs"),
        `Console.Write(MyMid.M.Wrap("app"));\n`,
    );

    const appCsproj = path.join(appDir, "App.csproj");
    const outDir = path.join(work, "out");
    const build = runCarbide([
        "build", "--project", appCsproj, "--out", outDir, "--format", "json",
    ]);
    assert.equal(build.status, 0, `build failed: ${build.stderr}`);
    assert.ok(existsSync(path.join(outDir, "App.dll")));
    assert.ok(existsSync(path.join(outDir, "MyMid.dll")));
    assert.ok(existsSync(path.join(outDir, "MyLib.dll")));

    const run = runCarbide(["run", "--project", appCsproj, "--format", "human"]);
    assert.equal(run.status, 0, `run failed: ${run.stderr}`);
    assert.equal(run.stdout, "mid(base,app)");
});

test("M9.2.4: InternalsVisibleTo lets App call internal members of Lib (positive)", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-m9-ivt-pos-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));

    const { appCsproj } = setupLibApp(work, {
        libSrc: `using System.Runtime.CompilerServices;\n[assembly: InternalsVisibleTo("App")]\nnamespace MyLib;\ninternal static class Internals { public static string Secret() => "friend-ok"; }\npublic static class Greeting { public static string For(string n) => $"hello {n}"; }\n`,
        appSrc: `using MyLib;\nConsole.Write(Internals.Secret());\n`,
    });

    const run = runCarbide(["run", "--project", appCsproj, "--format", "human"]);
    assert.equal(run.status, 0, `run failed: ${run.stderr}`);
    assert.equal(run.stdout, "friend-ok");
});

test("M9.2.4: removing InternalsVisibleTo surfaces CS0122 on App", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-m9-ivt-neg-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));

    const { appCsproj } = setupLibApp(work, {
        libSrc: `namespace MyLib;\ninternal static class Internals { public static string Secret() => "friend-ok"; }\npublic static class Greeting { public static string For(string n) => $"hello {n}"; }\n`,
        appSrc: `using MyLib;\nConsole.Write(Internals.Secret());\n`,
    });

    const validate = runCarbide(["validate", "--project", appCsproj, "--format", "json"]);
    assert.equal(validate.status, 1, `validate should fail without IVT: ${validate.stderr}`);
    const payload = parseJsonTrailer(validate.stdout);
    const errs = (payload.diagnostics ?? []).filter((d) => d.severity === "error");
    assert.ok(
        errs.some((d) => d.id === "CS0122" || d.id === "CS0103"),
        `expected CS0122/CS0103 in: ${JSON.stringify(errs.map((d) => d.id))}`,
    );
});

test("M9.2.5: ProjectReference cycle exits 1 with MSPROJ001", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-m9-cycle-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));

    const aDir = path.join(work, "A");
    const bDir = path.join(work, "B");
    mkdirSync(aDir);
    mkdirSync(bDir);
    writeFileSync(
        path.join(aDir, "A.csproj"),
        `<Project>\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <AssemblyName>A</AssemblyName>\n  </PropertyGroup>\n  <ItemGroup>\n    <ProjectReference Include="..\\B\\B.csproj"/>\n  </ItemGroup>\n</Project>\n`,
    );
    writeFileSync(path.join(aDir, "A.cs"), `namespace A; public class X { }\n`);
    writeFileSync(
        path.join(bDir, "B.csproj"),
        `<Project>\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <AssemblyName>B</AssemblyName>\n  </PropertyGroup>\n  <ItemGroup>\n    <ProjectReference Include="..\\A\\A.csproj"/>\n  </ItemGroup>\n</Project>\n`,
    );
    writeFileSync(path.join(bDir, "B.cs"), `namespace B; public class Y { }\n`);

    const build = runCarbide(["build", "--project", path.join(aDir, "A.csproj")]);
    assert.equal(build.status, 1, build.stderr);
    // U1.3: graph errors flow through the structured `error` field in the JSON payload,
    // not stderr (which stays calm under --format json).
    const payload = parseJsonBySentinel(build.stdout);
    assert.equal(payload.error.code, "MSPROJ001");
});

test("M9.2.6: syntax error in Lib.cs attributes diagnostic to Lib.csproj", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-m9-diag-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));

    const { appCsproj, libDir } = setupLibApp(work);
    // Introduce a syntax error: missing semicolon.
    writeFileSync(
        path.join(libDir, "Greeting.cs"),
        `namespace MyLib;\npublic static class Greeting { public static string For(string n) => $"hello {n}" }\n`,
    );

    const validate = runCarbide(["validate", "--project", appCsproj, "--format", "json"]);
    assert.equal(validate.status, 1, validate.stderr);
    const payload = parseJsonTrailer(validate.stdout);
    const errs = (payload.diagnostics ?? []).filter((d) => d.severity === "error");
    assert.ok(errs.length > 0, "expected at least one error diagnostic");
    const libError = errs.find((d) => typeof d.project === "string" && d.project.endsWith("Lib.csproj"));
    assert.ok(
        libError,
        `expected a diagnostic attributed to Lib.csproj; got: ${JSON.stringify(
            errs.map((d) => ({ id: d.id, project: d.project })),
        )}`,
    );
});

test("M9.2.7: output layout is flat under --out/", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-m9-outlayout-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    const { appCsproj } = setupLibApp(work);

    const outDir = path.join(work, "out");
    const build = runCarbide([
        "build", "--project", appCsproj, "--out", outDir, "--format", "json",
    ]);
    assert.equal(build.status, 0, build.stderr);

    // Exactly the four files expected: App.{dll,pdb}, MyLib.{dll,pdb}.
    for (const name of ["App.dll", "App.pdb", "MyLib.dll", "MyLib.pdb"]) {
        assert.ok(existsSync(path.join(outDir, name)), `missing ${name} in ${outDir}`);
    }
});

test("M9: assembly-name collision exits 3 with MSPROJ002", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-m9-collision-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));

    for (const libName of ["Lib1", "Lib2"]) {
        const d = path.join(work, libName);
        mkdirSync(d);
        writeFileSync(
            path.join(d, `${libName}.csproj`),
            `<Project>\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <AssemblyName>SharedName</AssemblyName>\n  </PropertyGroup>\n</Project>\n`,
        );
        writeFileSync(path.join(d, `${libName}.cs`), `namespace ${libName}; public class X {}\n`);
    }
    const appDir = path.join(work, "App");
    mkdirSync(appDir);
    writeFileSync(
        path.join(appDir, "App.csproj"),
        `<Project>\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <AssemblyName>App</AssemblyName>\n  </PropertyGroup>\n  <ItemGroup>\n    <ProjectReference Include="..\\Lib1\\Lib1.csproj"/>\n    <ProjectReference Include="..\\Lib2\\Lib2.csproj"/>\n  </ItemGroup>\n</Project>\n`,
    );
    writeFileSync(path.join(appDir, "Program.cs"), `Console.Write("ok");\n`);

    const build = runCarbide(["build", "--project", path.join(appDir, "App.csproj")]);
    assert.equal(build.status, 3, build.stderr);
    const payload = parseJsonBySentinel(build.stdout);
    assert.equal(payload.error.code, "MSPROJ002");
});

test("M9: --out - rejected for multi-project graphs (MSPROJ003)", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-m9-stdout-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    const { appCsproj } = setupLibApp(work);

    const build = runCarbide(["build", "--project", appCsproj, "--out", "-"]);
    assert.equal(build.status, 3, build.stderr);
    assert.match(build.stderr, /MSPROJ003/);
});
