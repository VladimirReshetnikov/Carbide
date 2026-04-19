// M11 — end-to-end CLI test: carbide build with Directory.Build.props inherited
// properties produces byte-identical PE to a csproj that flattens those properties
// directly.

import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, writeFileSync, readFileSync, rmSync, existsSync } from "node:fs";
import { createHash } from "node:crypto";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { parseJsonBySentinel } from "./_helpers.mjs";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const CLI = path.resolve(HERE, "..", "dist", "bin", "carbide.js");

function sha256File(p) {
    const h = createHash("sha256");
    h.update(readFileSync(p));
    return h.digest("hex");
}

function runCarbide(args) {
    const result = spawnSync(process.execPath, [CLI, ...args], {
        encoding: "utf8",
        shell: false,
    });
    return { status: result.status, stdout: result.stdout ?? "", stderr: result.stderr ?? "" };
}

test("M11: build with Directory.Build.props-derived properties succeeds", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-m11-e2e-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));

    writeFileSync(
        path.join(work, "Directory.Build.props"),
        `<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>`,
    );
    writeFileSync(
        path.join(work, "Foo.csproj"),
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>M11Inherited</AssemblyName></PropertyGroup></Project>`,
    );
    // `Console.WriteLine` uses `System.Console` (via ImplicitUsings) — proves the props
    // file's property was honoured at compile time.
    writeFileSync(path.join(work, "Program.cs"), `Console.Write("m11-ok");`);

    const build = runCarbide([
        "build", "--project", path.join(work, "Foo.csproj"),
        "--out", path.join(work, "out"),
    ]);
    assert.equal(build.status, 0, build.stderr);
    const payload = parseJsonBySentinel(build.stdout);
    assert.equal(payload.success, true);
    assert.ok(existsSync(path.join(work, "out", "M11Inherited.dll")));
});

test("M11: Directory.Build.props inheritance produces byte-identical PE to flattened csproj", async (t) => {
    // The byte-identical guarantee depends on M5 D53 determinism: the same input shape must
    // compile to the same PE. This test exercises it across the M11 evaluator layer.
    const inherited = mkdtempSync(path.join(tmpdir(), "carbide-m11-inherited-"));
    const flattened = mkdtempSync(path.join(tmpdir(), "carbide-m11-flattened-"));
    t.after(() => rmSync(inherited, { recursive: true, force: true }));
    t.after(() => rmSync(flattened, { recursive: true, force: true }));

    const SOURCE = `namespace ByteIdentical;\npublic static class P { public static int V() => 42; }\n`;

    // Inherited version: props file carries <Nullable>enable</Nullable>.
    writeFileSync(
        path.join(inherited, "Directory.Build.props"),
        `<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>`,
    );
    writeFileSync(
        path.join(inherited, "Foo.csproj"),
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>ByteIdentical</AssemblyName></PropertyGroup></Project>`,
    );
    writeFileSync(path.join(inherited, "Src.cs"), SOURCE);

    // Flattened version: csproj has <Nullable>enable</Nullable> directly (NO Directory.Build.props).
    writeFileSync(
        path.join(flattened, "Foo.csproj"),
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>ByteIdentical</AssemblyName><Nullable>enable</Nullable></PropertyGroup></Project>`,
    );
    writeFileSync(path.join(flattened, "Src.cs"), SOURCE);

    const r1 = runCarbide(["build", "--project", path.join(inherited, "Foo.csproj"), "--out", path.join(inherited, "out")]);
    assert.equal(r1.status, 0, r1.stderr);
    const r2 = runCarbide(["build", "--project", path.join(flattened, "Foo.csproj"), "--out", path.join(flattened, "out")]);
    assert.equal(r2.status, 0, r2.stderr);

    const hashInherited = sha256File(path.join(inherited, "out", "ByteIdentical.dll"));
    const hashFlattened = sha256File(path.join(flattened, "out", "ByteIdentical.dll"));
    assert.equal(
        hashInherited,
        hashFlattened,
        "PE bytes must match between Directory.Build.props inheritance and the flattened csproj",
    );
});

test("M11: explicit <Import> threads a sibling .props file", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-m11-import-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));

    writeFileSync(
        path.join(work, "common.props"),
        `<Project><PropertyGroup><AssemblyName>FromImport</AssemblyName></PropertyGroup></Project>`,
    );
    writeFileSync(
        path.join(work, "Foo.csproj"),
        `<Project><Import Project="common.props"/><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>`,
    );
    writeFileSync(path.join(work, "Program.cs"), `System.Console.Write("import-ok");`);

    const r = runCarbide([
        "build", "--project", path.join(work, "Foo.csproj"),
        "--out", path.join(work, "out"),
    ]);
    assert.equal(r.status, 0, r.stderr);
    assert.ok(existsSync(path.join(work, "out", "FromImport.dll")));
});
