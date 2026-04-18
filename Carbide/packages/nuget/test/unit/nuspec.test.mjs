import { test } from "node:test";
import assert from "node:assert/strict";
import { Buffer } from "node:buffer";
import { buildZip } from "./_zip-helper.mjs";
import {
    readNuspec,
    nuspecDepsToPackageRefs,
} from "../../dist/nuspec.js";

const NUSPEC_WITH_GROUPS = `<?xml version="1.0"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>Sample.Package</id>
    <version>1.2.3</version>
    <authors>Carbide Tests</authors>
    <description>Sample</description>
    <dependencies>
      <group targetFramework="net10.0">
        <dependency id="Newtonsoft.Json" version="[13.0.3, )" />
      </group>
      <group targetFramework="net6.0">
        <dependency id="Legacy.Compat" version="1.0.0" />
      </group>
      <group targetFramework="netstandard2.0">
        <dependency id="System.Text.Json" version="8.0.0" />
      </group>
    </dependencies>
  </metadata>
</package>`;

const NUSPEC_FLAT = `<?xml version="1.0"?>
<package>
  <metadata>
    <id>Flat.Pkg</id>
    <version>2.0.0</version>
    <dependencies>
      <dependency id="A" version="1.0.0" />
      <dependency id="B" version="[2.0.0, 3.0.0)" />
    </dependencies>
  </metadata>
</package>`;

const NUSPEC_NO_DEPS = `<?xml version="1.0"?>
<package>
  <metadata>
    <id>Leaf.Pkg</id>
    <version>0.1.0</version>
  </metadata>
</package>`;

test("readNuspec: parses id / version / grouped dependencies", async () => {
    const zip = buildZip([
        { name: "Sample.Package.nuspec", content: NUSPEC_WITH_GROUPS },
        { name: "lib/net10.0/Sample.Package.dll", content: Buffer.from([0x4d, 0x5a]) },
        { name: "lib/net6.0/Sample.Package.dll", content: Buffer.from([0x4d, 0x5a]) },
    ]);
    const info = await readNuspec(zip);
    assert.equal(info.id, "Sample.Package");
    assert.equal(info.version, "1.2.3");
    assert.equal(info.entries.length, 3);
    // Exactly 3 deps: one per group. No stray null-TFM duplicates.
    assert.equal(info.dependencies.length, 3);
    const dep10 = info.dependencies.find((d) => d.targetFramework === "net10.0");
    assert.ok(dep10);
    assert.equal(dep10.id, "Newtonsoft.Json");
    assert.equal(dep10.versionRange, "[13.0.3, )");
    const depStd = info.dependencies.find((d) => d.targetFramework === "netstandard2.0");
    assert.ok(depStd);
    assert.equal(depStd.id, "System.Text.Json");
    assert.equal(info.dependencies.filter((d) => d.targetFramework === null).length, 0);
});

test("readNuspec: parses flat <dependencies> (no groups)", async () => {
    const zip = buildZip([{ name: "Flat.Pkg.nuspec", content: NUSPEC_FLAT }]);
    const info = await readNuspec(zip);
    assert.equal(info.id, "Flat.Pkg");
    assert.equal(info.version, "2.0.0");
    assert.equal(info.dependencies.length, 2);
    for (const dep of info.dependencies) {
        assert.equal(dep.targetFramework, null);
    }
    assert.deepEqual(info.dependencies.map((d) => d.id).sort(), ["A", "B"]);
    const a = info.dependencies.find((d) => d.id === "A");
    assert.equal(a.versionRange, "1.0.0");
    const b = info.dependencies.find((d) => d.id === "B");
    assert.equal(b.versionRange, "[2.0.0, 3.0.0)");
});

test("readNuspec: no dependencies at all", async () => {
    const zip = buildZip([{ name: "Leaf.Pkg.nuspec", content: NUSPEC_NO_DEPS }]);
    const info = await readNuspec(zip);
    assert.equal(info.id, "Leaf.Pkg");
    assert.equal(info.version, "0.1.0");
    assert.equal(info.dependencies.length, 0);
});

test("readNuspec: throws when no .nuspec is at the zip root", async () => {
    const zip = buildZip([
        { name: "lib/net10.0/Foo.dll", content: Buffer.from([0x4d, 0x5a]) },
    ]);
    await assert.rejects(() => readNuspec(zip), /No \.nuspec file at nupkg root/);
});

test("readNuspec: collects .entries from the zip central directory", async () => {
    const zip = buildZip([
        { name: "Sample.Package.nuspec", content: NUSPEC_WITH_GROUPS },
        { name: "lib/net10.0/Sample.Package.dll", content: Buffer.from([0x4d, 0x5a, 0x90, 0x00]) },
        { name: "lib/net10.0/Sample.Package.xml", content: "<doc/>" },
        { name: "lib/netstandard2.0/Sample.Package.dll", content: Buffer.from([0x4d, 0x5a]) },
    ]);
    const info = await readNuspec(zip);
    const names = info.entries.map((e) => e.name).sort();
    assert.deepEqual(names, [
        "Sample.Package.nuspec",
        "lib/net10.0/Sample.Package.dll",
        "lib/net10.0/Sample.Package.xml",
        "lib/netstandard2.0/Sample.Package.dll",
    ].sort());
});

test("readNuspec: dependencyGroups preserves empty groups (empty-net10.0 before netstandard1.0)", async () => {
    // Real-world shape: Newtonsoft.Json 13.0.3 declares empty net6.0 / netstandard2.0
    // groups alongside netstandard1.0 which pulls Microsoft.CSharp. The resolver must
    // see every declared <group>, even the empty ones, to pick correctly.
    const nuspec = `<?xml version="1.0"?>
<package>
  <metadata>
    <id>Mimic.Newtonsoft</id>
    <version>13.0.3</version>
    <authors>Carbide Tests</authors>
    <description>Mimic</description>
    <dependencies>
      <group targetFramework="net6.0" />
      <group targetFramework="netstandard2.0" />
      <group targetFramework="netstandard1.0">
        <dependency id="Microsoft.CSharp" version="4.3.0" />
      </group>
    </dependencies>
  </metadata>
</package>`;
    const zip = buildZip([{ name: "Mimic.Newtonsoft.nuspec", content: nuspec }]);
    const info = await readNuspec(zip);
    const tfms = info.dependencyGroups.map((g) => g.targetFramework);
    assert.ok(tfms.includes("net6.0"));
    assert.ok(tfms.includes("netstandard2.0"));
    assert.ok(tfms.includes("netstandard1.0"));
    const empty6 = info.dependencyGroups.find((g) => g.targetFramework === "net6.0");
    assert.equal(empty6.dependencies.length, 0);
    const emptyStd2 = info.dependencyGroups.find((g) => g.targetFramework === "netstandard2.0");
    assert.equal(emptyStd2.dependencies.length, 0);
    const std1 = info.dependencyGroups.find((g) => g.targetFramework === "netstandard1.0");
    assert.equal(std1.dependencies.length, 1);
    assert.equal(std1.dependencies[0].id, "Microsoft.CSharp");
});

test("nuspecDepsToPackageRefs: dedupes by id, first occurrence wins", () => {
    const deps = [
        { id: "A", versionRange: "1.0.0", targetFramework: "net10.0" },
        { id: "A", versionRange: "2.0.0", targetFramework: "net6.0" },
        { id: "B", versionRange: "[3.0.0, )", targetFramework: null },
    ];
    const refs = nuspecDepsToPackageRefs(deps);
    assert.equal(refs.length, 2);
    const a = refs.find((r) => r.id === "A");
    assert.ok(a);
    assert.equal(a.versionRange, "1.0.0");
    const b = refs.find((r) => r.id === "B");
    assert.ok(b);
    assert.equal(b.versionRange, "[3.0.0, )");
});
