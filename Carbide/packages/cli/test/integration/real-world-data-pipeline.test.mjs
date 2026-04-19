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

test("live: mixed JSON+YAML data pipeline project resolves multiple packages and runs offline", { skip: !LIVE }, async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-live-pipeline-"));
    t.after(() => {
        try { rmSync(workDir, { recursive: true, force: true }); } catch { /* best-effort */ }
    });

    writeFileSync(
        path.join(workDir, "Pipeline.csproj"),
        `<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>Pipeline</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
    <PackageReference Include="YamlDotNet" Version="15.1.5" />
  </ItemGroup>
</Project>`,
    );

    writeFileSync(
        path.join(workDir, "Program.cs"),
        `using Newtonsoft.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

var yaml = """
orders:
  - customer: alice
    total: 12.4
  - customer: bob
    total: 8.1
""";
var parser = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
var parsed = parser.Deserialize<Dictionary<string, List<Order>>>(yaml);
var top = parsed["orders"]
    .OrderByDescending(o => o.Total)
    .Select(o => new { customer = o.Customer, total = o.Total })
    .ToArray();

Console.Write(JsonConvert.SerializeObject(top));

public sealed class Order
{
    [JsonProperty("customer")]
    public string Customer { get; set; } = string.Empty;

    [JsonProperty("total")]
    public decimal Total { get; set; }
}
`,
    );

    const run1 = runCarbide([
        "run", "--project", path.join(workDir, "Pipeline.csproj"), "--format", "json",
    ]);
    assert.equal(run1.status, 0, `first run failed: stdout=${run1.stdout} stderr=${run1.stderr}`);
    const summary1 = parseJsonTrailer(run1.stdout);
    assert.equal(summary1.success, true);
    assert.equal(summary1.stdOut, '[{"customer":"alice","total":12.4},{"customer":"bob","total":8.1}]');

    const lockPath = path.join(workDir, "carbide.lock.json");
    assert.ok(existsSync(lockPath), "expected lock file to be written");
    const lock = JSON.parse(readFileSync(lockPath, "utf8"));
    assert.equal(lock.schemaVersion, 1);
    const ids = lock.packages.map((p) => p.id).sort();
    assert.deepEqual(ids, ["Newtonsoft.Json", "YamlDotNet"]);

    const run2 = runCarbide([
        "run", "--project", path.join(workDir, "Pipeline.csproj"), "--offline", "--format", "json",
    ]);
    assert.equal(run2.status, 0, `offline run failed: stdout=${run2.stdout} stderr=${run2.stderr}`);
    const summary2 = parseJsonTrailer(run2.stdout);
    assert.equal(summary2.success, true);
    assert.equal(summary2.stdOut, summary1.stdOut);
});
