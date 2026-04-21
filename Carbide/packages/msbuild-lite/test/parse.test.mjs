import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, writeFileSync, rmSync, mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { parseCsproj, parseCsprojString } from "../dist/index.js";

function makeFixture(files) {
    const dir = mkdtempSync(path.join(tmpdir(), "msbuild-lite-"));
    for (const [name, content] of Object.entries(files)) {
        const abs = path.join(dir, name);
        mkdirSync(path.dirname(abs), { recursive: true });
        writeFileSync(abs, content);
    }
    return dir;
}

test("minimal csproj: reads TargetFramework, default-include picks up .cs files", async (t) => {
    const dir = makeFixture({
        "Foo.csproj": `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>`,
        "Program.cs": `Console.WriteLine("hi");`,
        "Helper.cs": `public static class H {}`,
    });
    t.after(() => rmSync(dir, { recursive: true, force: true }));

    const model = await parseCsproj(path.join(dir, "Foo.csproj"));
    assert.deepEqual(model.targetFrameworks, ["net10.0"]);
    assert.equal(model.evaluationTrace.targetFramework.selected, "net10.0");
    assert.equal(model.sourceFiles.length, 2);
    assert.ok(model.sourceFiles.some((f) => f.endsWith("Program.cs")));
    assert.ok(model.sourceFiles.some((f) => f.endsWith("Helper.cs")));
});

test("properties: Nullable, LangVersion, ImplicitUsings, DefineConstants, AssemblyName, RootNamespace", async (t) => {
    const dir = makeFixture({
        "Foo.csproj": `<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <DefineConstants>DEBUG;MY_FEATURE</DefineConstants>
    <AssemblyName>MyAsm</AssemblyName>
    <RootNamespace>MyApp</RootNamespace>
  </PropertyGroup>
</Project>`,
        "Program.cs": ``,
    });
    t.after(() => rmSync(dir, { recursive: true, force: true }));

    const model = await parseCsproj(path.join(dir, "Foo.csproj"));
    assert.equal(model.properties.nullable, "enable");
    assert.equal(model.properties.langVersion, "latest");
    assert.equal(model.properties.implicitUsings, "enable");
    assert.deepEqual(model.properties.defineConstants, ["DEBUG", "MY_FEATURE"]);
    assert.equal(model.properties.assemblyName, "MyAsm");
    assert.equal(model.properties.rootNamespace, "MyApp");
});

test("TargetFrameworks semicolon list; first-listed wins", async (t) => {
    const dir = makeFixture({
        "Foo.csproj": `<Project>
  <PropertyGroup>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
  </PropertyGroup>
</Project>`,
    });
    t.after(() => rmSync(dir, { recursive: true, force: true }));

    const model = await parseCsproj(path.join(dir, "Foo.csproj"));
    // Both listed in declaration order — first-listed is the load-bearing selection
    // under the documented evaluationTrace.targetFramework.selectionPolicy. An earlier
    // (broken) revision sorted the list; swapping <TargetFrameworks>net8.0;net10.0 to
    // have net10.0 selected silently changed downstream ref-pack / NuGet resolution.
    assert.deepEqual(model.targetFrameworks, ["net8.0", "net10.0"]);
    assert.equal(model.evaluationTrace.targetFramework.selected, "net8.0");
});

// Regression for review R2 §9 — item attribute values used to be stored raw, so
// `<PackageReference Version="$(Pkg)"/>` downstream consumers saw the literal
// placeholder `$(Pkg)` rather than the evaluated property. Must now substitute.
test("item attributes substitute $(Property) references", async (t) => {
    const dir = makeFixture({
        "Foo.csproj": `<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <NewtonsoftVersion>13.0.3</NewtonsoftVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="$(NewtonsoftVersion)"/>
    <ProjectReference Include="$(MSBuildThisFileDirectory)../Sibling/Sibling.csproj"/>
  </ItemGroup>
</Project>`,
    });
    t.after(() => rmSync(dir, { recursive: true, force: true }));

    const model = await parseCsproj(path.join(dir, "Foo.csproj"));
    assert.equal(model.packageReferences.length, 1);
    assert.equal(model.packageReferences[0].version, "13.0.3",
        "PackageReference Version should be substituted from $(NewtonsoftVersion)");
    assert.equal(model.projectReferences.length, 1);
    // $(MSBuildThisFileDirectory) resolves to the project directory; the substituted
    // include should therefore resolve to the sibling project's absolute path.
    assert.ok(!model.projectReferences[0].includes("$("),
        "ProjectReference Include should not contain a literal $(...) placeholder after substitution");
});

test("PackageReference and ProjectReference emit warnings, not errors", async (t) => {
    const dir = makeFixture({
        "Foo.csproj": `<Project>
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3"/>
    <ProjectReference Include="../Sibling/Sibling.csproj"/>
  </ItemGroup>
</Project>`,
    });
    t.after(() => rmSync(dir, { recursive: true, force: true }));

    const model = await parseCsproj(path.join(dir, "Foo.csproj"));
    assert.equal(model.packageReferences.length, 1);
    assert.equal(model.packageReferences[0].id, "Newtonsoft.Json");
    assert.equal(model.packageReferences[0].version, "13.0.3");
    assert.equal(model.projectReferences.length, 1);
    assert.ok(model.warnings.some((w) => w.code === "MSBLITE013"));
    assert.ok(model.warnings.some((w) => w.code === "MSBLITE014"));
});

test("Compile Remove drops files from the default include", async (t) => {
    const dir = makeFixture({
        "Foo.csproj": `<Project>
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup>
    <Compile Remove="Excluded.cs"/>
  </ItemGroup>
</Project>`,
        "Program.cs": ``,
        "Excluded.cs": ``,
    });
    t.after(() => rmSync(dir, { recursive: true, force: true }));

    const model = await parseCsproj(path.join(dir, "Foo.csproj"));
    assert.equal(model.sourceFiles.length, 1);
    assert.ok(model.sourceFiles[0].endsWith("Program.cs"));
});

test("EnableDefaultCompileItems=false means explicit Include is required", async (t) => {
    const dir = makeFixture({
        "Foo.csproj": `<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.cs"/>
  </ItemGroup>
</Project>`,
        "Program.cs": ``,
        "Unused.cs": ``,
    });
    t.after(() => rmSync(dir, { recursive: true, force: true }));

    const model = await parseCsproj(path.join(dir, "Foo.csproj"));
    assert.equal(model.sourceFiles.length, 1);
    assert.ok(model.sourceFiles[0].endsWith("Program.cs"));
});

test("parseCsprojString parses in-memory XML without touching disk for the csproj", async (t) => {
    const dir = makeFixture({
        "Program.cs": ``,
    });
    t.after(() => rmSync(dir, { recursive: true, force: true }));

    const xml = `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>`;
    const model = await parseCsprojString(xml, path.join(dir, "Foo.csproj"));
    assert.deepEqual(model.targetFrameworks, ["net10.0"]);
    assert.equal(model.sourceFiles.length, 1);
});

test("malformed XML yields MSBLITE000 error-severity warning", async (t) => {
    const dir = makeFixture({ "Foo.csproj": `<Project><PropertyGroup></Project>` });
    t.after(() => rmSync(dir, { recursive: true, force: true }));

    const model = await parseCsproj(path.join(dir, "Foo.csproj"));
    assert.equal(model.targetFrameworks.length, 0);
    const err = model.warnings.find((w) => w.code === "MSBLITE000");
    assert.ok(err, `expected MSBLITE000 warning, got ${JSON.stringify(model.warnings)}`);
    assert.equal(err.severity, "error");
});

test("condition on PropertyGroup: skip when $(Configuration) != 'Release'", async (t) => {
    const dir = makeFixture({
        "Foo.csproj": `<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <PropertyGroup Condition=" '$(Configuration)' == 'Release' ">
    <DefineConstants>RELEASE</DefineConstants>
  </PropertyGroup>
</Project>`,
    });
    t.after(() => rmSync(dir, { recursive: true, force: true }));

    const debug = await parseCsproj(path.join(dir, "Foo.csproj"), { configuration: "Debug" });
    assert.equal(debug.properties.defineConstants, undefined);

    const release = await parseCsproj(path.join(dir, "Foo.csproj"), { configuration: "Release" });
    assert.deepEqual(release.properties.defineConstants, ["RELEASE"]);
});

test("Compile Include with glob matches nested files", async (t) => {
    const dir = makeFixture({
        "Foo.csproj": `<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.cs"/>
  </ItemGroup>
</Project>`,
        "src/A.cs": ``,
        "src/nested/B.cs": ``,
        "docs/Ignored.cs": ``,
    });
    t.after(() => rmSync(dir, { recursive: true, force: true }));

    const model = await parseCsproj(path.join(dir, "Foo.csproj"));
    assert.equal(model.sourceFiles.length, 2);
    assert.ok(model.sourceFiles.some((f) => f.endsWith("A.cs")));
    assert.ok(model.sourceFiles.some((f) => f.endsWith("B.cs")));
    assert.ok(!model.sourceFiles.some((f) => f.includes("Ignored")));
});

test("bin/obj/.git directories are excluded from default discovery", async (t) => {
    const dir = makeFixture({
        "Foo.csproj": `<Project>
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>`,
        "Program.cs": ``,
        "bin/Release/Ignored.cs": ``,
        "obj/Debug/Ignored2.cs": ``,
    });
    t.after(() => rmSync(dir, { recursive: true, force: true }));

    const model = await parseCsproj(path.join(dir, "Foo.csproj"));
    assert.equal(model.sourceFiles.length, 1);
    assert.ok(model.sourceFiles[0].endsWith("Program.cs"));
});
