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

test("real-world: CsvHelper pipeline + offline replay", { skip: !LIVE }, async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-rw-csv-"));
    t.after(() => {
        try { rmSync(workDir, { recursive: true, force: true }); } catch { }
    });

    writeFileSync(
        path.join(workDir, "Retail.csproj"),
        `<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>RetailReporting</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="CsvHelper" Version="32.0.0" />
  </ItemGroup>
</Project>`,
    );
    writeFileSync(
        path.join(workDir, "Program.cs"),
        `using System.Globalization;
using CsvHelper;

var csv = "Region,Amount\\nWest,120\\nEast,30\\nWest,80\\nNorth,50";
using var reader = new StringReader(csv);
using var csvReader = new CsvReader(reader, CultureInfo.InvariantCulture);

var rows = csvReader.GetRecords<Sale>().ToArray();
var top = rows
    .GroupBy(x => x.Region)
    .Select(g => new { Region = g.Key, Sum = g.Sum(x => x.Amount) })
    .OrderByDescending(x => x.Sum)
    .ThenBy(x => x.Region, StringComparer.Ordinal)
    .First();

Console.Write($"Top={top.Region};Total={top.Sum}");

public sealed record Sale(string Region, int Amount);
`,
    );

    const run1 = runCarbide(["run", "--project", path.join(workDir, "Retail.csproj"), "--format", "json"]);
    assert.equal(run1.status, 0, `run1 failed: stdout=${run1.stdout} stderr=${run1.stderr}`);
    const summary1 = JSON.parse(run1.stdout.trim());
    assert.equal(summary1.success, true);
    assert.equal(summary1.stdOut, "Top=West;Total=200");

    const lockPath = path.join(workDir, "carbide.lock.json");
    assert.ok(existsSync(lockPath), "lock file should exist");
    const lock = JSON.parse(readFileSync(lockPath, "utf8"));
    assert.ok(lock.packages.length >= 1, "lock should include at least CsvHelper");

    const run2 = runCarbide([
        "run",
        "--project", path.join(workDir, "Retail.csproj"),
        "--offline",
        "--format", "json",
    ]);
    assert.equal(run2.status, 0, `run2 failed: stdout=${run2.stdout} stderr=${run2.stderr}`);
    const summary2 = JSON.parse(run2.stdout.trim());
    assert.equal(summary2.success, true);
    assert.equal(summary2.stdOut, summary1.stdOut);
});

test("real-world: YamlDotNet + Scriban configuration rendering", { skip: !LIVE }, async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-rw-config-"));
    t.after(() => {
        try { rmSync(workDir, { recursive: true, force: true }); } catch { }
    });

    writeFileSync(
        path.join(workDir, "Config.csproj"),
        `<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>ConfigRender</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="YamlDotNet" Version="15.0.0" />
    <PackageReference Include="Scriban" Version="5.9.1" />
  </ItemGroup>
</Project>`,
    );
    writeFileSync(
        path.join(workDir, "Program.cs"),
        `using Scriban;
using YamlDotNet.Serialization;

var yaml = "service: Payments\\nregion: us-west\\nretries: 3";
var parser = new DeserializerBuilder().Build();
var data = parser.Deserialize<Dictionary<string, object>>(yaml);

var template = Template.Parse("svc={{ service }};region={{ region }};retries={{ retries }}");
var rendered = template.Render(data);
Console.Write(rendered);
`,
    );

    const run = runCarbide(["run", "--project", path.join(workDir, "Config.csproj"), "--format", "json"]);
    assert.equal(run.status, 0, `run failed: stdout=${run.stdout} stderr=${run.stderr}`);
    const summary = JSON.parse(run.stdout.trim());
    assert.equal(summary.success, true);
    assert.equal(summary.stdOut, "svc=Payments;region=us-west;retries=3");
});
