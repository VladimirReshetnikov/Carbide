// Dependency-group selection. NuGet picks the single nearest-compatible `<group>` and uses
// only its dependencies. Picking the wrong group, or merging several, installs a transitive
// set the package never asked for — and nothing reports it.

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { Buffer } from "node:buffer";
import { resolve } from "../../dist/resolver.js";
import { sha256Hex } from "../../dist/cache.js";
import { buildZip } from "./_zip-helper.mjs";

const cacheDirs = [];
const nextCacheDir = () => {
    const dir = mkdtempSync(path.join(tmpdir(), "carbide-depgroups-"));
    cacheDirs.push(dir);
    return dir;
};
process.on("exit", () => {
    for (const dir of cacheDirs) {
        try {
            rmSync(dir, { recursive: true, force: true });
        } catch {
            // best-effort cleanup
        }
    }
});

/** `groups` is an array of [targetFrameworkAttrOrNull, deps[]]. */
function makeNuspec(id, version, groups) {
    const body = groups
        .map(([tfm, deps]) => {
            const inner = deps
                .map((d) => `<dependency id="${d.id}" version="${d.versionRange}" />`)
                .join("");
            return tfm === null ? inner : `<group targetFramework="${tfm}">${inner}</group>`;
        })
        .join("");
    return `<?xml version="1.0"?><package><metadata><id>${id}</id><version>${version}</version>` +
        `<dependencies>${body}</dependencies></metadata></package>`;
}

class MockFlatContainer {
    sourceUrl = "mock://flat-container/";
    packages = new Map();
    add(id, version, groups = [], libTfm = "net10.0") {
        const bytes = buildZip([
            { name: `${id}.nuspec`, content: makeNuspec(id, version, groups) },
            { name: `lib/${libTfm}/${id}.dll`, content: Buffer.from(`fake-${id}`, "utf-8") },
        ]);
        const versions = this.packages.get(id.toLowerCase()) ?? new Map();
        versions.set(version, { bytes, sha256: sha256Hex(bytes), fromCache: false });
        this.packages.set(id.toLowerCase(), versions);
    }
    async listVersions(id) {
        return [...(this.packages.get(id.toLowerCase())?.keys() ?? [])];
    }
    async downloadNupkg(id, version) {
        const entry = this.packages.get(id.toLowerCase())?.get(version);
        if (!entry) throw new Error(`no entry for ${id}@${version}`);
        return entry;
    }
}

const run = (fc, refs) =>
    resolve(refs, { allowListMode: "off", flatContainer: fc, cacheDir: nextCacheDir() });

const ids = (graph) => graph.packages.map((p) => p.id).sort();

test("the nearest compatible group wins", async () => {
    const fc = new MockFlatContainer();
    fc.add("A", "1.0.0", [
        ["netstandard2.0", [{ id: "OldDep", versionRange: "1.0.0" }]],
        ["net10.0", [{ id: "NewDep", versionRange: "1.0.0" }]],
    ]);
    fc.add("OldDep", "1.0.0");
    fc.add("NewDep", "1.0.0");

    const graph = await run(fc, [{ id: "A", versionRange: "1.0.0" }]);
    assert.deepEqual(ids(graph), ["A", "NewDep"], "only the net10.0 group's deps");
});

test("long-form target framework names are understood", async () => {
    // Plenty of published nuspecs write `.NETStandard2.0` rather than `netstandard2.0`.
    // Unrecognised labels used to score as incompatible, which dropped the package into a
    // fallback that merged every group.
    const fc = new MockFlatContainer();
    fc.add("A", "1.0.0", [
        [".NETFramework4.7.2", [{ id: "FrameworkOnly", versionRange: "1.0.0" }]],
        [".NETStandard2.0", [{ id: "PortableDep", versionRange: "1.0.0" }]],
    ]);
    fc.add("FrameworkOnly", "1.0.0");
    fc.add("PortableDep", "1.0.0");

    const graph = await run(fc, [{ id: "A", versionRange: "1.0.0" }]);
    assert.deepEqual(
        ids(graph),
        ["A", "PortableDep"],
        "the .NET Framework group must not contribute dependencies to a net10.0 target",
    );
});

test("a netcoreapp group is compatible with a net10.0 target", async () => {
    // Matches lib-folder selection, which also accepts netcoreapp assets.
    const fc = new MockFlatContainer();
    fc.add("A", "1.0.0", [
        ["netstandard2.0", [{ id: "OldDep", versionRange: "1.0.0" }]],
        ["netcoreapp3.1", [{ id: "CoreDep", versionRange: "1.0.0" }]],
    ]);
    fc.add("OldDep", "1.0.0");
    fc.add("CoreDep", "1.0.0");

    const graph = await run(fc, [{ id: "A", versionRange: "1.0.0" }]);
    assert.deepEqual(ids(graph), ["A", "CoreDep"], "netcoreapp3.1 is nearer than netstandard2.0");
});

test("no compatible group means no dependencies, not every dependency", async () => {
    // The old fallback merged all groups "so we at least try something", which pulled
    // .NET Framework packages into a net10.0 build.
    const fc = new MockFlatContainer();
    fc.add("A", "1.0.0", [
        ["net472", [{ id: "FrameworkOnly", versionRange: "1.0.0" }]],
        ["net48", [{ id: "AlsoFramework", versionRange: "1.0.0" }]],
    ]);
    fc.add("FrameworkOnly", "1.0.0");
    fc.add("AlsoFramework", "1.0.0");

    const graph = await run(fc, [{ id: "A", versionRange: "1.0.0" }]);
    assert.deepEqual(ids(graph), ["A"], "no incompatible dependency is pulled in");
    assert.ok(
        graph.warnings.some((w) => w.code === "MSNUGET012"),
        `expected an MSNUGET012 warning, got ${JSON.stringify(graph.warnings.map((w) => w.code))}`,
    );
});

test("an untyped (flat) dependency list applies to any target", async () => {
    const fc = new MockFlatContainer();
    fc.add("A", "1.0.0", [[null, [{ id: "AnyDep", versionRange: "1.0.0" }]]]);
    fc.add("AnyDep", "1.0.0");

    const graph = await run(fc, [{ id: "A", versionRange: "1.0.0" }]);
    assert.deepEqual(ids(graph), ["A", "AnyDep"]);
});

test("an empty compatible group means zero dependencies", async () => {
    // An empty <group targetFramework="net10.0"/> says "supported here, nothing needed".
    // Falling through to a non-empty older group would drag in packages the author excluded.
    const fc = new MockFlatContainer();
    fc.add("A", "1.0.0", [
        ["netstandard2.0", [{ id: "OldDep", versionRange: "1.0.0" }]],
        ["net10.0", []],
    ]);
    fc.add("OldDep", "1.0.0");

    const graph = await run(fc, [{ id: "A", versionRange: "1.0.0" }]);
    assert.deepEqual(ids(graph), ["A"]);
});
