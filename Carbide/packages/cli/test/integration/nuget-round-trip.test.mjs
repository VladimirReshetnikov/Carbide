// M6.13 end-to-end round-trip: a .csproj with a <PackageReference> to Newtonsoft.Json,
// a .cs file that actually calls JsonConvert, and `carbide run --project` that produces
// the expected output. Gated on CARBIDE_NUGET_LIVE=1 since it downloads from api.nuget.org.
//
// Run:   CARBIDE_NUGET_LIVE=1 node --test test/integration/nuget-round-trip.test.mjs

import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, writeFileSync, existsSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import path from "node:path";

const LIVE = process.env.CARBIDE_NUGET_LIVE === "1";
const HERE = path.dirname(fileURLToPath(import.meta.url));
const CLI = path.resolve(HERE, "..", "..", "dist", "bin", "carbide.js");

function runCarbide(args, options = {}) {
    const result = spawnSync(process.execPath, [CLI, ...args], {
        encoding: "utf8",
        shell: false,
        ...options,
    });
    return { status: result.status, stdout: result.stdout ?? "", stderr: result.stderr ?? "" };
}

test("M6.13: end-to-end — csproj → Newtonsoft.Json → carbide run", { skip: !LIVE }, async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-m6-e2e-"));
    t.after(() => {
        try { rmSync(workDir, { recursive: true, force: true }); } catch { /* best-effort */ }
    });

    writeFileSync(
        path.join(workDir, "Foo.csproj"),
        `<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>JsonRoundTrip</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3"/>
  </ItemGroup>
</Project>`,
    );
    writeFileSync(
        path.join(workDir, "Program.cs"),
        `using Newtonsoft.Json;

var payload = new { greeting = "hello", count = 3 };
Console.Write(JsonConvert.SerializeObject(payload));
`,
    );

    // Pass 1: fresh resolve — downloads Newtonsoft.Json 13.0.3 nupkg, writes lock.
    const run1 = runCarbide([
        "run", "--project", path.join(workDir, "Foo.csproj"),
        "--format", "json",
    ]);
    assert.equal(run1.status, 0, `run1 failed: stdout=${run1.stdout} stderr=${run1.stderr}`);
    const summary1 = JSON.parse(run1.stdout.trim());
    assert.equal(summary1.success, true);
    assert.equal(summary1.stdOut, `{"greeting":"hello","count":3}`);

    // Lock should exist next to the csproj.
    const lockPath = path.join(workDir, "carbide.lock.json");
    assert.ok(existsSync(lockPath), "lock file should exist after fresh resolve");
    const lock = JSON.parse(readFileSync(lockPath, "utf8"));
    assert.equal(lock.schemaVersion, 1);
    assert.equal(lock.packages.length, 1);
    assert.equal(lock.packages[0].id, "Newtonsoft.Json");
    assert.equal(lock.packages[0].version, "13.0.3");
    assert.equal(lock.packages[0].sha256.length, 64);

    // Pass 2: replay mode via --offline (lock exists → no network allowed).
    const run2 = runCarbide([
        "run", "--project", path.join(workDir, "Foo.csproj"),
        "--offline",
        "--format", "json",
    ]);
    assert.equal(run2.status, 0, `run2 (offline replay) failed: stdout=${run2.stdout} stderr=${run2.stderr}`);
    const summary2 = JSON.parse(run2.stdout.trim());
    assert.equal(summary2.success, true);
    assert.equal(summary2.stdOut, summary1.stdOut);
});

test("M6.13: --allow-list-mode strict refuses an unlisted package", { skip: !LIVE }, async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-m6-reject-"));
    t.after(() => {
        try { rmSync(workDir, { recursive: true, force: true }); } catch { /* best-effort */ }
    });

    writeFileSync(
        path.join(workDir, "Foo.csproj"),
        `<Project>
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>NotAllowed</AssemblyName></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="System.Text.Json" Version="8.0.0"/>
  </ItemGroup>
</Project>`,
    );
    writeFileSync(path.join(workDir, "Program.cs"), `Console.Write("unused");`);

    const result = runCarbide([
        "build", "--project", path.join(workDir, "Foo.csproj"),
        "--out", path.join(workDir, "out"),
        "--format", "json",
    ]);
    // Strict allow-list should reject and produce a non-zero exit.
    assert.notEqual(result.status, 0, "expected non-zero exit for unlisted package in strict mode");
    assert.match(result.stderr, /allow-list/i);
});
