// M12 — `<Analyzer Include>` items and the analyzer-related `<ProjectReference>` metadata.
//
// These three fields are a Carbide extension: `cs_kit.msbuild_lite` does not carry them. They
// are additive, so the parity fixtures — which project onto the fields their expected.json
// declares — are unaffected, and a consumer that ignores them behaves exactly as before.

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, writeFileSync, rmSync, mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { parseCsproj } from "../dist/index.js";

function withProject(t, csproj, extraFiles = {}) {
    const dir = mkdtempSync(path.join(tmpdir(), "carbide-msblite-analyzer-"));
    t.after(() => rmSync(dir, { recursive: true, force: true }));
    writeFileSync(path.join(dir, "Foo.csproj"), csproj);
    writeFileSync(path.join(dir, "Program.cs"), "class C { }");
    for (const [name, content] of Object.entries(extraFiles)) {
        const full = path.join(dir, name);
        mkdirSync(path.dirname(full), { recursive: true });
        writeFileSync(full, content);
    }
    return dir;
}

test("<Analyzer Include> resolves to an absolute path", async (t) => {
    const dir = withProject(
        t,
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
           <ItemGroup><Analyzer Include="tools/Gen.dll" /></ItemGroup></Project>`,
    );
    const model = await parseCsproj(path.join(dir, "Foo.csproj"));
    assert.deepEqual(model.analyzerReferences, [path.resolve(dir, "tools/Gen.dll")]);
});

test("<Analyzer Include> accepts a semicolon-separated list", async (t) => {
    const dir = withProject(
        t,
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
           <ItemGroup><Analyzer Include="a.dll;b.dll" /></ItemGroup></Project>`,
    );
    const model = await parseCsproj(path.join(dir, "Foo.csproj"));
    assert.deepEqual(model.analyzerReferences, [
        path.resolve(dir, "a.dll"),
        path.resolve(dir, "b.dll"),
    ]);
});

test("<Analyzer Include> substitutes properties, like every other item attribute", async (t) => {
    const dir = withProject(
        t,
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><GenDir>tools</GenDir></PropertyGroup>
           <ItemGroup><Analyzer Include="$(GenDir)/Gen.dll" /></ItemGroup></Project>`,
    );
    const model = await parseCsproj(path.join(dir, "Foo.csproj"));
    assert.deepEqual(model.analyzerReferences, [path.resolve(dir, "tools/Gen.dll")]);
});

test('ProjectReference OutputItemType="Analyzer" is recorded as a subset', async (t) => {
    const dir = withProject(
        t,
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
           <ItemGroup>
             <ProjectReference Include="../Gen/Gen.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
             <ProjectReference Include="../Lib/Lib.csproj" />
           </ItemGroup></Project>`,
    );
    const model = await parseCsproj(path.join(dir, "Foo.csproj"));
    const gen = path.resolve(dir, "../Gen/Gen.csproj");
    const lib = path.resolve(dir, "../Lib/Lib.csproj");

    // Still a project reference: the graph has to build it either way. Only the attachment
    // changes.
    assert.ok(model.projectReferences.includes(gen));
    assert.ok(model.projectReferences.includes(lib));

    assert.deepEqual(model.analyzerProjectReferences, [gen]);
    assert.deepEqual(model.noReferenceProjectReferences, [gen]);
});

test("the two ProjectReference flags are independent", async (t) => {
    // A bare OutputItemType="Analyzer" still contributes a metadata reference, and a bare
    // ReferenceOutputAssembly="false" suppresses one without making the project an analyzer.
    const dir = withProject(
        t,
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
           <ItemGroup>
             <ProjectReference Include="../A/A.csproj" OutputItemType="Analyzer" />
             <ProjectReference Include="../B/B.csproj" ReferenceOutputAssembly="false" />
           </ItemGroup></Project>`,
    );
    const model = await parseCsproj(path.join(dir, "Foo.csproj"));
    assert.deepEqual(model.analyzerProjectReferences, [path.resolve(dir, "../A/A.csproj")]);
    assert.deepEqual(model.noReferenceProjectReferences, [path.resolve(dir, "../B/B.csproj")]);
});

test("analyzer metadata is matched case-insensitively, as MSBuild does", async (t) => {
    const dir = withProject(
        t,
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
           <ItemGroup>
             <ProjectReference Include="../Gen/Gen.csproj" OutputItemType="analyzer" ReferenceOutputAssembly="FALSE" />
           </ItemGroup></Project>`,
    );
    const model = await parseCsproj(path.join(dir, "Foo.csproj"));
    const gen = path.resolve(dir, "../Gen/Gen.csproj");
    assert.deepEqual(model.analyzerProjectReferences, [gen]);
    assert.deepEqual(model.noReferenceProjectReferences, [gen]);
});

test("analyzer metadata written as child elements is honoured too", async (t) => {
    // MSBuild allows either form; a project written the long way must not silently lose it.
    const dir = withProject(
        t,
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
           <ItemGroup>
             <ProjectReference Include="../Gen/Gen.csproj">
               <OutputItemType>Analyzer</OutputItemType>
               <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
             </ProjectReference>
           </ItemGroup></Project>`,
    );
    const model = await parseCsproj(path.join(dir, "Foo.csproj"));
    const gen = path.resolve(dir, "../Gen/Gen.csproj");
    assert.deepEqual(model.analyzerProjectReferences, [gen]);
    assert.deepEqual(model.noReferenceProjectReferences, [gen]);
});

test("a project with no analyzer metadata reports empty arrays, not undefined", async (t) => {
    const dir = withProject(
        t,
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>`,
    );
    const model = await parseCsproj(path.join(dir, "Foo.csproj"));
    assert.deepEqual(model.analyzerReferences, []);
    assert.deepEqual(model.analyzerProjectReferences, []);
    assert.deepEqual(model.noReferenceProjectReferences, []);
});
