import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import path from "node:path";

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

test("complex multi-file project: support-triage pipeline runs and prints ordered summary", async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-complex-proj-"));
    t.after(() => rmSync(workDir, { recursive: true, force: true }));

    writeFileSync(
        path.join(workDir, "Helpdesk.csproj"),
        `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>Helpdesk</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <DefineConstants>ENTERPRISE</DefineConstants>
  </PropertyGroup>
</Project>`,
    );

    writeFileSync(
        path.join(workDir, "Ticket.cs"),
        `namespace Helpdesk;

public enum Priority { P0, P1, P2, P3 }

public sealed record Ticket(string Id, string Team, Priority Priority, int AgeMinutes, bool CustomerEscalated);
`,
    );

    writeFileSync(
        path.join(workDir, "Policy.cs"),
        `namespace Helpdesk;

public static class Policy
{
    public static bool IsUrgent(Ticket ticket)
    {
#if ENTERPRISE
        return ticket.Priority is Priority.P0 or Priority.P1
            || ticket.CustomerEscalated
            || ticket.AgeMinutes >= 45;
#else
        return ticket.Priority is Priority.P0 || ticket.CustomerEscalated;
#endif
    }
}
`,
    );

    writeFileSync(
        path.join(workDir, "Report.cs"),
        `namespace Helpdesk;

public static class Report
{
    public static string Render(IEnumerable<Ticket> tickets)
    {
        var urgent = tickets.Where(Policy.IsUrgent);
        var sections = urgent
            .GroupBy(static t => t.Team)
            .OrderByDescending(static g => g.Count())
            .ThenBy(static g => g.Key, StringComparer.Ordinal)
            .Select(static g =>
                $"team={g.Key};urgent={g.Count()};oldest={g.Max(static t => t.AgeMinutes)};ids={string.Join(',', g.OrderBy(t => t.Id, StringComparer.Ordinal).Select(static t => t.Id))}");

        return string.Join("\\n", sections);
    }
}
`,
    );

    writeFileSync(
        path.join(workDir, "Program.cs"),
        `using Helpdesk;

var tickets = new[]
{
    new Ticket("INC-100", "Identity", Priority.P1, 20, false),
    new Ticket("INC-101", "Storefront", Priority.P2, 71, false),
    new Ticket("INC-102", "Storefront", Priority.P3, 10, true),
    new Ticket("INC-103", "Identity", Priority.P2, 12, false),
    new Ticket("INC-104", "Payments", Priority.P0, 8, false),
};

Console.Write(Report.Render(tickets));
`,
    );

    const run = runCarbide([
        "run", "--project", path.join(workDir, "Helpdesk.csproj"),
        "--format", "json",
    ]);
    assert.equal(run.status, 0, `run failed: stdout=${run.stdout} stderr=${run.stderr}`);

    const payload = JSON.parse(run.stdout.trim());
    assert.equal(payload.success, true);
    assert.equal(
        payload.stdOut,
        [
            "team=Storefront;urgent=2;oldest=71;ids=INC-101,INC-102",
            "team=Identity;urgent=1;oldest=20;ids=INC-100",
            "team=Payments;urgent=1;oldest=8;ids=INC-104",
        ].join("\n"),
    );
});

test("project with ProjectReference currently reports MSBLITE014 warning (known limitation)", async (t) => {
    const workDir = mkdtempSync(path.join(tmpdir(), "carbide-project-ref-warning-"));
    t.after(() => rmSync(workDir, { recursive: true, force: true }));

    writeFileSync(
        path.join(workDir, "App.csproj"),
        `<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>App</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Shared/Shared.csproj" />
  </ItemGroup>
</Project>`,
    );
    writeFileSync(path.join(workDir, "Program.cs"), `Console.WriteLine("ok");`);

    const validate = runCarbide([
        "validate", "--project", path.join(workDir, "App.csproj"),
        "--format", "json",
    ]);
    assert.equal(validate.status, 0, validate.stderr);

    const payload = JSON.parse(validate.stdout.trim());
    assert.equal(payload.success, true);
    const warnings = payload.warnings ?? [];
    assert.ok(warnings.some((w) => w.code === "MSBLITE014"), `expected MSBLITE014 in warnings: ${JSON.stringify(warnings)}`);
});
