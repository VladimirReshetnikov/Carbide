// End-to-end transitive-graph smoke: Serilog.Sinks.Console pulls Serilog transitively.
// Gated on CARBIDE_NUGET_LIVE=1 since it downloads from api.nuget.org.
//
// This test is intentionally lax about the exact console output, because some libraries
// bypass Carbide's current Console.SetOut capture by writing directly to stdout.

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

test("end-to-end — csproj → Serilog.Sinks.Console (transitive) → carbide run", { skip: !LIVE }, async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-serilog-e2e-"));
    t.after(() => {
        try { rmSync(workDir, { recursive: true, force: true }); } catch { /* best-effort */ }
    });

    writeFileSync(
        path.join(workDir, "Foo.csproj"),
        `<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>SerilogRoundTrip</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Serilog.Sinks.Console" Version="6.1.1"/>
  </ItemGroup>
</Project>`,
    );
    writeFileSync(
        path.join(workDir, "Program.cs"),
        `using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: "{Message:lj}{NewLine}")
    .CreateLogger();

Log.Information("hello {Name}", "carbide");
Console.Write("ok");
`,
    );

    const run1 = runCarbide([
        "run", "--project", path.join(workDir, "Foo.csproj"),
        "--format", "json",
    ]);
    assert.equal(run1.status, 0, `run1 failed: stdout=${run1.stdout} stderr=${run1.stderr}`);
    const summary1 = parseJsonTrailer(run1.stdout);
    assert.equal(summary1.success, true);
    assert.match(summary1.stdOut, /ok/);

    const lockPath = path.join(workDir, "carbide.lock.json");
    assert.ok(existsSync(lockPath), "lock file should exist after fresh resolve");
    const lock = JSON.parse(readFileSync(lockPath, "utf8"));
    const ids = lock.packages.map((p) => p.id);
    assert.ok(ids.includes("Serilog.Sinks.Console"), `expected Serilog.Sinks.Console in lock, got ${JSON.stringify(ids)}`);
    assert.ok(ids.includes("Serilog"), `expected Serilog in lock, got ${JSON.stringify(ids)}`);

    const run2 = runCarbide([
        "run", "--project", path.join(workDir, "Foo.csproj"),
        "--offline",
        "--format", "json",
    ]);
    assert.equal(run2.status, 0, `run2 failed: stdout=${run2.stdout} stderr=${run2.stderr}`);
    const summary2 = parseJsonTrailer(run2.stdout);
    assert.equal(summary2.success, true);
    assert.match(summary2.stdOut, /ok/);
});

