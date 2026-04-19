// End-to-end round-trip: a .csproj with a <PackageReference> to YamlDotNet and a small
// program that exercises it. Gated on CARBIDE_NUGET_LIVE=1 since it downloads from
// api.nuget.org.
//
// Run:   CARBIDE_NUGET_LIVE=1 node --test test/integration/yaml-round-trip.test.mjs

import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, writeFileSync, existsSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { parseJsonTrailer } from "../_helpers.mjs";

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

test("end-to-end — csproj → YamlDotNet → carbide run", { skip: !LIVE }, async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-yaml-e2e-"));
    t.after(() => {
        try { rmSync(workDir, { recursive: true, force: true }); } catch { /* best-effort */ }
    });

    writeFileSync(
        path.join(workDir, "Foo.csproj"),
        `<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>YamlRoundTrip</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="YamlDotNet" Version="17.0.1"/>
  </ItemGroup>
</Project>`,
    );
    writeFileSync(
        path.join(workDir, "Program.cs"),
        `using YamlDotNet.Serialization;

var yaml = "x: 3\\ny: hello\\n";
var deserializer = new DeserializerBuilder().Build();
var obj = deserializer.Deserialize<Dictionary<string, object>>(yaml);
Console.Write($"x={obj["x"]},y={obj["y"]}");
`,
    );

    // Pass 1: fresh resolve — downloads package, writes lock.
    const run1 = runCarbide([
        "run", "--project", path.join(workDir, "Foo.csproj"),
        "--format", "json",
    ]);
    assert.equal(run1.status, 0, `run1 failed: stdout=${run1.stdout} stderr=${run1.stderr}`);
    const summary1 = parseJsonTrailer(run1.stdout);
    assert.equal(summary1.success, true);
    assert.equal(summary1.stdOut, `x=3,y=hello`);

    // Lock should exist next to the csproj.
    const lockPath = path.join(workDir, "carbide.lock.json");
    assert.ok(existsSync(lockPath), "lock file should exist after fresh resolve");
    const lock = JSON.parse(readFileSync(lockPath, "utf8"));
    assert.equal(lock.schemaVersion, 1);
    assert.equal(lock.packages.length, 1);
    assert.equal(lock.packages[0].id, "YamlDotNet");
    assert.equal(lock.packages[0].version, "17.0.1");
    assert.equal(lock.packages[0].sha256.length, 64);

    // Pass 2: replay mode via --offline (lock exists → no network allowed).
    const run2 = runCarbide([
        "run", "--project", path.join(workDir, "Foo.csproj"),
        "--offline",
        "--format", "json",
    ]);
    assert.equal(run2.status, 0, `run2 (offline replay) failed: stdout=${run2.stdout} stderr=${run2.stderr}`);
    const summary2 = parseJsonTrailer(run2.stdout);
    assert.equal(summary2.success, true);
    assert.equal(summary2.stdOut, summary1.stdOut);
});

