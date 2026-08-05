// M12 — `<Analyzer Include>` items and `<ProjectReference OutputItemType="Analyzer">`.
//
// A boundary worth stating up front, because it shapes what these tests can prove: building a
// Roslyn source generator *with Carbide* needs the Microsoft.CodeAnalysis reference assemblies,
// which Carbide does not supply to compilations. So `<Analyzer Include>` pointing at an
// already-built DLL works end to end, while `OutputItemType="Analyzer"` is exercised for its
// attachment semantics against a producer Carbide can actually compile.

import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, writeFileSync, mkdirSync, rmSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { parseJsonBySentinel } from "./_helpers.mjs";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const CLI = path.resolve(HERE, "..", "dist", "bin", "carbide.js");
const GENERATOR = path.resolve(
    HERE, "..", "..", "core", "test", "fixtures", "generator-dll", "CarbideTestGenerator.dll",
);

const PROGRAM = [
    "Console.Write(new Point { X = 3, Y = 4 });",
    "",
    "[CarbideTest.GenerateToString]",
    "public partial class Point",
    "{",
    "    public int X { get; set; }",
    "    public int Y { get; set; }",
    "}",
].join("\n");

function runCarbide(args, options = {}) {
    const result = spawnSync(process.execPath, [CLI, ...args], {
        encoding: "utf8",
        shell: false,
        ...options,
    });
    return { status: result.status, stdout: result.stdout ?? "", stderr: result.stderr ?? "" };
}

function requireGenerator() {
    if (!existsSync(GENERATOR)) {
        throw new Error(
            `${GENERATOR} not found. Run \`npm run build:test-fixtures\` in packages/core first.`,
        );
    }
}

/**
 * Lay out an App project and a sibling Lib project.
 *
 * Sibling, not nested: default compile items glob `**\/*.cs` under the project directory, so a
 * `Lib/` folder *inside* App's directory gets compiled straight into App. A reference-suppression
 * test written that way passes whether or not suppression works.
 */
function twoProjects(t, appCsproj, appProgram) {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-m12-graph-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));

    mkdirSync(path.join(work, "Lib"), { recursive: true });
    writeFileSync(
        path.join(work, "Lib", "Lib.csproj"),
        "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
            "<AssemblyName>Lib</AssemblyName></PropertyGroup></Project>",
    );
    writeFileSync(
        path.join(work, "Lib", "Thing.cs"),
        "namespace Lib; public static class Thing { public static int Value => 42; }",
    );

    mkdirSync(path.join(work, "App"), { recursive: true });
    writeFileSync(path.join(work, "App", "App.csproj"), appCsproj);
    writeFileSync(path.join(work, "App", "P.cs"), appProgram);
    return path.join(work, "App", "App.csproj");
}

function appCsprojWith(projectReferenceItem) {
    return (
        "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
        "<AssemblyName>App</AssemblyName></PropertyGroup><ItemGroup>" +
        projectReferenceItem +
        "</ItemGroup></Project>"
    );
}

test("M12: <Analyzer Include> in a csproj runs the generator", async (t) => {
    requireGenerator();
    const work = mkdtempSync(path.join(tmpdir(), "carbide-m12-item-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));

    writeFileSync(
        path.join(work, "App.csproj"),
        "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
            "<AssemblyName>App</AssemblyName></PropertyGroup>" +
            `<ItemGroup><Analyzer Include="${GENERATOR.replace(/\\/g, "/")}" /></ItemGroup></Project>`,
    );
    writeFileSync(path.join(work, "P.cs"), PROGRAM);

    const r = runCarbide([
        "run", "--project", path.join(work, "App.csproj"), "--format", "human",
    ]);
    assert.equal(r.status, 0, r.stderr);
    assert.equal(r.stdout, "Point { X = 3, Y = 4 }");
});

test("M12: an <Analyzer Include> that does not exist warns and does not fail the build", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-m12-item-missing-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));

    writeFileSync(
        path.join(work, "App.csproj"),
        "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
            "<AssemblyName>App</AssemblyName></PropertyGroup>" +
            '<ItemGroup><Analyzer Include="nope/Missing.dll" /></ItemGroup></Project>',
    );
    writeFileSync(path.join(work, "P.cs"), "Console.Write(7);");

    // The rest of the project is buildable; a missing analyzer must be said out loud rather
    // than silently skipped or fatal.
    const r = runCarbide(["run", "--project", path.join(work, "App.csproj"), "--format", "json"]);
    assert.equal(r.status, 0, r.stderr);
    const payload = parseJsonBySentinel(r.stdout);
    assert.ok(
        payload.warnings.some((w) => w.code === "MSPROJ013"),
        `expected MSPROJ013 among ${JSON.stringify(payload.warnings)}`,
    );
});

test('M12: ProjectReference OutputItemType="Analyzer" warns when the producer carries no analyzer', async (t) => {
    // The mistake a user makes when they wire the metadata onto the wrong project: Lib builds
    // fine, but it is an ordinary library, not a generator.
    const csproj = twoProjects(
        t,
        appCsprojWith(
            '<ProjectReference Include="../Lib/Lib.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />',
        ),
        "Console.Write(7);",
    );

    const r = runCarbide(["run", "--project", csproj, "--format", "json"]);
    const payload = parseJsonBySentinel(r.stdout);
    assert.ok(
        payload.warnings.some((w) => w.code === "MSPROJ012"),
        `expected MSPROJ012 among ${JSON.stringify(payload.warnings)}`,
    );
});

test('M12: ReferenceOutputAssembly="false" keeps the producer off the consumer\'s API surface', async (t) => {
    // Naming Lib.Thing must fail: the point of ReferenceOutputAssembly="false" is that the
    // producer is built but not referenced.
    const csproj = twoProjects(
        t,
        appCsprojWith('<ProjectReference Include="../Lib/Lib.csproj" ReferenceOutputAssembly="false" />'),
        "Console.Write(Lib.Thing.Value);",
    );

    const r = runCarbide(["run", "--project", csproj, "--format", "json"]);
    assert.equal(r.status, 1, "expected the reference to be suppressed");
    const payload = parseJsonBySentinel(r.stdout);
    assert.ok(
        payload.diagnostics.some((d) => d.message.includes("Lib")),
        `expected an unresolved-name error, got ${JSON.stringify(payload.diagnostics)}`,
    );
});

test("M12: a plain ProjectReference still contributes its reference", async (t) => {
    // The control for the test above: same layout, no metadata, and the type resolves. Without
    // it, a suppression test would pass even if references were never attached at all.
    const csproj = twoProjects(
        t,
        appCsprojWith('<ProjectReference Include="../Lib/Lib.csproj" />'),
        "Console.Write(Lib.Thing.Value);",
    );

    const r = runCarbide(["run", "--project", csproj, "--format", "human"]);
    assert.equal(r.status, 0, r.stderr);
    assert.equal(r.stdout, "42");
});

test('M12: a bare OutputItemType="Analyzer" still contributes its reference', async (t) => {
    // MSBuild treats the two pieces of metadata independently; only ReferenceOutputAssembly
    // suppresses the reference. Lib carries no analyzer, so MSPROJ012 rides along — what this
    // asserts is that the reference survived regardless.
    const csproj = twoProjects(
        t,
        appCsprojWith('<ProjectReference Include="../Lib/Lib.csproj" OutputItemType="Analyzer" />'),
        "Console.Write(Lib.Thing.Value);",
    );

    const r = runCarbide(["run", "--project", csproj, "--format", "human"]);
    assert.equal(r.status, 0, r.stderr);
    assert.equal(r.stdout, "42");
});
