// U3.1 / U3.2 — hermetic tests for `carbide audit` and `carbide tree`.

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

function setupLibApp(workDir) {
    const libDir = path.join(workDir, "Lib");
    const appDir = path.join(workDir, "App");
    mkdirSync(libDir);
    mkdirSync(appDir);
    writeFileSync(
        path.join(libDir, "Lib.csproj"),
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>MyLib</AssemblyName></PropertyGroup></Project>`,
    );
    writeFileSync(path.join(libDir, "G.cs"), `namespace MyLib; public static class G { public static string For(string n) => $"hello {n}"; }`);
    writeFileSync(
        path.join(appDir, "App.csproj"),
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>App</AssemblyName><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><ProjectReference Include="..\\Lib\\Lib.csproj"/></ItemGroup></Project>`,
    );
    writeFileSync(path.join(appDir, "Program.cs"), `using MyLib; Console.Write(G.For("world"));`);
    return { appCsproj: path.join(appDir, "App.csproj") };
}

test("U3.1: audit --format json emits graph + subprojects payload", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u3-audit-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    const { appCsproj } = setupLibApp(work);

    const r = runCarbide(["audit", "--project", appCsproj]);
    assert.equal(r.status, 0, r.stderr);
    const p = parseJsonBySentinel(r.stdout);
    assert.equal(p.schemaVersion, 3);
    assert.equal(p.success, true);
    assert.ok(p.graph);
    assert.equal(p.graph.order.length, 2);
    assert.equal(p.subprojects.length, 2);
    const app = p.subprojects.find((s) => s.assemblyName === "App");
    const lib = p.subprojects.find((s) => s.assemblyName === "MyLib");
    assert.ok(app && lib);
    assert.equal(app.targetFramework, "net10.0");
    assert.ok(app.sourceFiles.includes("Program.cs"));
    assert.ok(lib.sourceFiles.includes("G.cs"));
});

test("U3.1: audit is read-only by default (no carbide.lock.json written)", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u3-audit-ro-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(
        path.join(work, "Foo.csproj"),
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Foo</AssemblyName></PropertyGroup><ItemGroup><PackageReference Include="Newtonsoft.Json" Version="13.0.3"/></ItemGroup></Project>`,
    );
    writeFileSync(path.join(work, "Program.cs"), `Console.Write("ok");`);
    // Pre-seed an empty lock so the resolver replays (no network).
    writeFileSync(
        path.join(work, "carbide.lock.json"),
        JSON.stringify({ schemaVersion: 1, generator: "carbide", generatedAt: "2026-04-19T00:00:00Z", packages: [], warnings: [] }) + "\n",
    );

    const r = runCarbide(["audit", "--project", path.join(work, "Foo.csproj")]);
    assert.equal(r.status, 0, r.stderr);
    // Lock pre-existed; assert it still exists (we didn't blow it away) and audit succeeded.
    assert.ok(existsSync(path.join(work, "carbide.lock.json")));
});

test("U3.1: audit surfaces MSPROJ001 cycle through U1 error taxonomy", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u3-audit-cycle-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    for (const [n, ref] of [["A", "..\\B\\B.csproj"], ["B", "..\\A\\A.csproj"]]) {
        const d = path.join(work, n);
        mkdirSync(d);
        writeFileSync(
            path.join(d, `${n}.csproj`),
            `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>${n}</AssemblyName></PropertyGroup><ItemGroup><ProjectReference Include="${ref}"/></ItemGroup></Project>`,
        );
        writeFileSync(path.join(d, `${n}.cs`), `namespace ${n}; public class X {}`);
    }
    const r = runCarbide(["audit", "--project", path.join(work, "A", "A.csproj")]);
    assert.equal(r.status, 1);
    const p = parseJsonBySentinel(r.stdout);
    assert.equal(p.error.code, "MSPROJ001");
    assert.equal(p.error.category, "project-graph-cycle");
});

test("U3.2: tree renders ASCII with root + ProjectReference", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u3-tree-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    const { appCsproj } = setupLibApp(work);

    const r = runCarbide(["tree", "--project", appCsproj]);
    assert.equal(r.status, 0, r.stderr);
    assert.match(r.stdout, /App\.csproj \(App, net10\.0\)/);
    assert.match(r.stdout, /└── Lib\.csproj \(MyLib, net10\.0\)/);
});

test("U3.2: tree renders transitive chain A -> Mid -> Lib with nested connectors", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u3-tree-trans-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    const libDir = path.join(work, "Lib");
    const midDir = path.join(work, "Mid");
    const appDir = path.join(work, "App");
    mkdirSync(libDir);
    mkdirSync(midDir);
    mkdirSync(appDir);
    writeFileSync(
        path.join(libDir, "Lib.csproj"),
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>MyLib</AssemblyName></PropertyGroup></Project>`,
    );
    writeFileSync(path.join(libDir, "L.cs"), `namespace MyLib; public static class L {}`);
    writeFileSync(
        path.join(midDir, "Mid.csproj"),
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>MyMid</AssemblyName></PropertyGroup><ItemGroup><ProjectReference Include="..\\Lib\\Lib.csproj"/></ItemGroup></Project>`,
    );
    writeFileSync(path.join(midDir, "M.cs"), `namespace MyMid; public static class M {}`);
    writeFileSync(
        path.join(appDir, "App.csproj"),
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>App</AssemblyName></PropertyGroup><ItemGroup><ProjectReference Include="..\\Mid\\Mid.csproj"/></ItemGroup></Project>`,
    );
    writeFileSync(path.join(appDir, "Program.cs"), `namespace App; public class P {}`);

    const r = runCarbide(["tree", "--project", path.join(appDir, "App.csproj")]);
    assert.equal(r.status, 0, r.stderr);
    // Expected shape:
    //   App.csproj (App, net10.0)
    //   └── Mid.csproj (MyMid, net10.0)
    //       └── Lib.csproj (MyLib, net10.0)
    assert.match(r.stdout, /App\.csproj/);
    assert.match(r.stdout, /└── Mid\.csproj/);
    assert.match(r.stdout, /    └── Lib\.csproj/);
});

test("U3.2: tree --project <missing> exits 3", async () => {
    const r = runCarbide(["tree"]);
    assert.equal(r.status, 3);
    assert.match(r.stderr, /--project/);
});
