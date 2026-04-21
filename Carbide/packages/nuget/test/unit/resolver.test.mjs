import { test } from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { tmpdir } from "node:os";
import { mkdtempSync, rmSync } from "node:fs";
import { join } from "node:path";
import { Buffer } from "node:buffer";
import { buildZip } from "./_zip-helper.mjs";
import {
    resolve,
    AllowListRefusedError,
    SafetyRefusalError,
} from "../../dist/resolver.js";
import { MSNUGET_CODES } from "../../dist/warnings.js";
import { LOCK_SCHEMA_VERSION } from "../../dist/lock.js";

// One tmp root for the whole file; resolver never writes into it because we inject
// FlatContainer directly (the cache is opened but unused on the mock path).
const TMP_BASE = mkdtempSync(join(tmpdir(), "carbide-nuget-resolver-"));
process.on("exit", () => {
    try { rmSync(TMP_BASE, { recursive: true, force: true }); } catch { /* best-effort */ }
});

let callCounter = 0;
function nextCacheDir() {
    return join(TMP_BASE, `call-${++callCounter}`);
}

function sha256Hex(bytes) {
    return createHash("sha256").update(bytes).digest("hex");
}

function makeNuspec(id, version, groupedDeps = null) {
    let depsXml = "";
    if (groupedDeps) {
        const groups = Object.entries(groupedDeps)
            .map(([tfm, deps]) => {
                const inner = deps
                    .map((d) => `<dependency id="${d.id}" version="${d.versionRange}" />`)
                    .join("");
                return `<group targetFramework="${tfm}">${inner}</group>`;
            })
            .join("");
        depsXml = `<dependencies>${groups}</dependencies>`;
    }
    return `<?xml version="1.0"?>
<package>
  <metadata>
    <id>${id}</id>
    <version>${version}</version>
    <authors>Carbide Tests</authors>
    <description>Synthetic ${id}</description>
    ${depsXml}
  </metadata>
</package>`;
}

function buildNupkg(id, version, { groupedDeps = null, libTfm = "net10.0", extraEntries = [] } = {}) {
    const nuspec = makeNuspec(id, version, groupedDeps);
    const dllBytes = Buffer.from(`fake-${id}-${version}-dll`, "utf-8");
    const files = [
        { name: `${id}.nuspec`, content: nuspec },
        { name: `lib/${libTfm}/${id}.dll`, content: dllBytes },
        ...extraEntries,
    ];
    return { bytes: buildZip(files), dllBytes };
}

class MockFlatContainer {
    sourceUrl = "mock://flat-container/";
    constructor() {
        // id-lower -> Map<version, { bytes, sha256, fromCache }>
        this.packages = new Map();
    }
    add(id, version, spec = {}) {
        const { bytes } = buildNupkg(id, version, spec);
        const hash = sha256Hex(bytes);
        const key = id.toLowerCase();
        const versions = this.packages.get(key) ?? new Map();
        versions.set(version, { bytes, sha256: hash, fromCache: false });
        this.packages.set(key, versions);
        return { bytes, sha256: hash };
    }
    addRawBytes(id, version, bytes) {
        const hash = sha256Hex(bytes);
        const key = id.toLowerCase();
        const versions = this.packages.get(key) ?? new Map();
        versions.set(version, { bytes, sha256: hash, fromCache: false });
        this.packages.set(key, versions);
        return { bytes, sha256: hash };
    }
    async listVersions(id) {
        const versions = this.packages.get(id.toLowerCase());
        return versions ? [...versions.keys()] : [];
    }
    async downloadNupkg(id, version) {
        const entry = this.packages.get(id.toLowerCase())?.get(version);
        if (!entry) throw new Error(`MockFlatContainer: no entry for '${id}@${version}'`);
        return entry;
    }
}

// =========================================================================

test("resolve: single top-level package with no deps", async () => {
    const fc = new MockFlatContainer();
    fc.add("A", "1.0.0");
    const graph = await resolve([{ id: "A", versionRange: "1.0.0" }], {
        allowListMode: "off",
        flatContainer: fc,
        cacheDir: nextCacheDir(),
    });
    assert.equal(graph.packages.length, 1);
    const [a] = graph.packages;
    assert.equal(a.id, "A");
    assert.equal(a.version, "1.0.0");
    assert.deepEqual(a.requestedBy, ["<root>"]);
    assert.deepEqual(a.dependencies, []);
    assert.equal(a.libFolder, "net10.0");
    assert.equal(typeof a.sha256, "string");
    assert.equal(a.sha256.length, 64);
    assert.equal(graph.references.length, 1);
    assert.equal(graph.references[0].name, "A");
    assert.equal(graph.references[0].packageId, "A");
    assert.equal(graph.references[0].packageVersion, "1.0.0");
    assert.ok(graph.references[0].bytes instanceof Uint8Array);
    assert.equal(graph.warnings.length, 0);
    assert.equal(graph.lock.schemaVersion, LOCK_SCHEMA_VERSION);
    assert.equal(graph.lock.packages.length, 1);
    assert.equal(graph.lock.generator, "carbide");
});

test("resolve: linear transitive chain A → B → C (all resolved)", async () => {
    const fc = new MockFlatContainer();
    fc.add("A", "1.0.0", { groupedDeps: { "net10.0": [{ id: "B", versionRange: "1.0.0" }] } });
    fc.add("B", "1.0.0", { groupedDeps: { "net10.0": [{ id: "C", versionRange: "1.0.0" }] } });
    fc.add("C", "1.0.0");

    const graph = await resolve([{ id: "A", versionRange: "1.0.0" }], {
        allowListMode: "off",
        flatContainer: fc,
        cacheDir: nextCacheDir(),
    });
    const byId = new Map(graph.packages.map((p) => [p.id, p]));
    assert.equal(byId.size, 3);
    assert.deepEqual([...byId.get("A").requestedBy], ["<root>"]);
    assert.deepEqual([...byId.get("B").requestedBy], ["A"]);
    assert.deepEqual([...byId.get("C").requestedBy], ["B"]);
    assert.deepEqual([...byId.get("A").dependencies], ["B"]);
    assert.deepEqual([...byId.get("B").dependencies], ["C"]);
    assert.deepEqual([...byId.get("C").dependencies], []);
    assert.equal(graph.references.length, 3);
    assert.deepEqual(graph.references.map((r) => r.name).sort(), ["A", "B", "C"]);
});

test("resolve: nearest-wins — root-declared B@2.0.0 beats transitive B@1.0.0", async () => {
    const fc = new MockFlatContainer();
    fc.add("A", "1.0.0", { groupedDeps: { "net10.0": [{ id: "B", versionRange: "1.0.0" }] } });
    fc.add("B", "1.0.0");
    fc.add("B", "2.0.0");

    const graph = await resolve(
        [
            { id: "A", versionRange: "1.0.0" },
            { id: "B", versionRange: "2.0.0" },
        ],
        { allowListMode: "off", flatContainer: fc, cacheDir: nextCacheDir() },
    );
    const byId = new Map(graph.packages.map((p) => [p.id, p]));
    assert.equal(byId.size, 2);
    assert.equal(byId.get("B").version, "2.0.0");
    // A at depth 0 is processed first; it queues B@1 at depth 1 with requestedBy=A.
    // Root's B@2 (depth 0) wins. The transitive B@1 at depth 1 then appends "A" to B@2's provenance.
    assert.ok(byId.get("B").requestedBy.includes("<root>"));
    assert.ok(byId.get("B").requestedBy.includes("A"));
});

test("resolve: same-depth version tie triggers MSNUGET010 warning", async () => {
    const fc = new MockFlatContainer();
    fc.add("A", "1.0.0", { groupedDeps: { "net10.0": [{ id: "B", versionRange: "1.0.0" }] } });
    fc.add("C", "1.0.0", { groupedDeps: { "net10.0": [{ id: "B", versionRange: "2.0.0" }] } });
    fc.add("B", "1.0.0");
    fc.add("B", "2.0.0");

    const graph = await resolve(
        [
            { id: "A", versionRange: "1.0.0" },
            { id: "C", versionRange: "1.0.0" },
        ],
        { allowListMode: "off", flatContainer: fc, cacheDir: nextCacheDir() },
    );
    const byId = new Map(graph.packages.map((p) => [p.id, p]));
    assert.equal(byId.get("B").version, "2.0.0");
    const tieWarning = graph.warnings.find(
        (w) => w.code === MSNUGET_CODES.NEAREST_WINS_TIE && w.severity === "warning",
    );
    assert.ok(tieWarning, "Expected MSNUGET010 warning-severity tie note");
    assert.match(tieWarning.message, /Same-depth tie/);
});

// Regression for review R1 M2 / R2 §7 — the same-depth tie-break used to call
// `localeCompare` on raw version strings, which orders `"2.10.0"` BEFORE `"2.9.0"`
// lexicographically. Semantic comparison must pick the higher-minor version.
test("resolve: same-depth tie uses semantic version compare (2.10.0 beats 2.9.0)", async () => {
    const fc = new MockFlatContainer();
    fc.add("A", "1.0.0", { groupedDeps: { "net10.0": [{ id: "B", versionRange: "2.9.0" }] } });
    fc.add("C", "1.0.0", { groupedDeps: { "net10.0": [{ id: "B", versionRange: "2.10.0" }] } });
    fc.add("B", "2.9.0");
    fc.add("B", "2.10.0");

    const graph = await resolve(
        [
            { id: "A", versionRange: "1.0.0" },
            { id: "C", versionRange: "1.0.0" },
        ],
        { allowListMode: "off", flatContainer: fc, cacheDir: nextCacheDir() },
    );
    const byId = new Map(graph.packages.map((p) => [p.id, p]));
    assert.equal(byId.get("B").version, "2.10.0",
        "semver picks 2.10.0 over 2.9.0; lexicographic compare would incorrectly pick 2.9.0");
});

test("resolve: allow-list strict mode refuses unknown package", async () => {
    const fc = new MockFlatContainer();
    fc.add("NotAllowed.Pkg", "1.0.0");
    await assert.rejects(
        () =>
            resolve([{ id: "NotAllowed.Pkg", versionRange: "1.0.0" }], {
                allowListMode: "strict",
                flatContainer: fc,
                cacheDir: nextCacheDir(),
            }),
        (err) => err instanceof AllowListRefusedError && err.packageId === "NotAllowed.Pkg",
    );
});

test("resolve: allow-list advisory mode emits MSNUGET020 but continues", async () => {
    const fc = new MockFlatContainer();
    fc.add("NotAllowed.Pkg", "1.0.0");
    const graph = await resolve(
        [{ id: "NotAllowed.Pkg", versionRange: "1.0.0" }],
        { allowListMode: "advisory", flatContainer: fc, cacheDir: nextCacheDir() },
    );
    assert.equal(graph.packages.length, 1);
    const advisory = graph.warnings.find((w) => w.code === MSNUGET_CODES.ALLOWLIST_ADVISORY);
    assert.ok(advisory, "Expected MSNUGET020 advisory warning");
    assert.equal(advisory.severity, "warning");
});

test("resolve: allow-list off mode skips the allow-list gate entirely", async () => {
    const fc = new MockFlatContainer();
    fc.add("WhateverPkg", "1.0.0");
    const graph = await resolve(
        [{ id: "WhateverPkg", versionRange: "1.0.0" }],
        { allowListMode: "off", flatContainer: fc, cacheDir: nextCacheDir() },
    );
    assert.equal(graph.packages.length, 1);
    const allowWarns = graph.warnings.filter(
        (w) =>
            w.code === MSNUGET_CODES.ALLOWLIST_ADVISORY ||
            w.code === MSNUGET_CODES.ALLOWLIST_REFUSED,
    );
    assert.equal(allowWarns.length, 0);
});

test("resolve: safety refusal on native binaries", async () => {
    const fc = new MockFlatContainer();
    fc.add("Bad.Pkg", "1.0.0", {
        extraEntries: [
            { name: "runtimes/win-x64/native/bad.dll", content: Buffer.from([0x00, 0x01, 0x02]) },
        ],
    });
    await assert.rejects(
        () =>
            resolve([{ id: "Bad.Pkg", versionRange: "1.0.0" }], {
                allowListMode: "off",
                flatContainer: fc,
                cacheDir: nextCacheDir(),
            }),
        (err) =>
            err instanceof SafetyRefusalError && err.code === MSNUGET_CODES.SAFETY_NATIVE,
    );
});

test("resolve: picks the best lib folder across net10.0 / net6.0 / netstandard2.0", async () => {
    const fc = new MockFlatContainer();
    const nuspec = makeNuspec("MultiLib.Pkg", "1.0.0");
    const bytes = buildZip([
        { name: "MultiLib.Pkg.nuspec", content: nuspec },
        { name: "lib/net6.0/MultiLib.Pkg.dll", content: Buffer.from("net6", "utf-8") },
        { name: "lib/net10.0/MultiLib.Pkg.dll", content: Buffer.from("net10", "utf-8") },
        { name: "lib/netstandard2.0/MultiLib.Pkg.dll", content: Buffer.from("ns20", "utf-8") },
    ]);
    fc.addRawBytes("MultiLib.Pkg", "1.0.0", bytes);

    const graph = await resolve(
        [{ id: "MultiLib.Pkg", versionRange: "1.0.0" }],
        { allowListMode: "off", flatContainer: fc, cacheDir: nextCacheDir() },
    );
    assert.equal(graph.packages[0].libFolder, "net10.0");
    assert.equal(graph.references.length, 1);
    assert.equal(new TextDecoder().decode(graph.references[0].bytes), "net10");
});

test("resolve: empty group at a closer TFM beats a non-empty group at a farther TFM", async () => {
    // Models Newtonsoft.Json 13.0.3: empty net6.0 group vs non-empty netstandard1.0 group.
    // With target=net10.0 the resolver must pick the empty net6.0 group — otherwise it
    // would drag in netstandard1.0's Microsoft.CSharp transitively.
    const fc = new MockFlatContainer();
    const parentNuspec = `<?xml version="1.0"?>
<package>
  <metadata>
    <id>Mimic.Parent</id>
    <version>1.0.0</version>
    <authors>Carbide Tests</authors>
    <description>Mimic</description>
    <dependencies>
      <group targetFramework="net6.0" />
      <group targetFramework="netstandard2.0" />
      <group targetFramework="netstandard1.0">
        <dependency id="ShouldNotBeResolved" version="1.0.0" />
      </group>
    </dependencies>
  </metadata>
</package>`;
    const bytes = buildZip([
        { name: "Mimic.Parent.nuspec", content: parentNuspec },
        { name: "lib/netstandard2.0/Mimic.Parent.dll", content: Buffer.from("ns20", "utf-8") },
    ]);
    fc.addRawBytes("Mimic.Parent", "1.0.0", bytes);
    // Deliberately DO NOT register "ShouldNotBeResolved" — if the resolver tried to
    // pull it, the mock would throw.
    const graph = await resolve(
        [{ id: "Mimic.Parent", versionRange: "1.0.0" }],
        { allowListMode: "off", flatContainer: fc, cacheDir: nextCacheDir() },
    );
    assert.equal(graph.packages.length, 1);
    assert.equal(graph.packages[0].id, "Mimic.Parent");
});

test("resolve: dependencies scoped by TFM — picks the net10.0 group", async () => {
    const fc = new MockFlatContainer();
    fc.add("Parent", "1.0.0", {
        groupedDeps: {
            "net10.0": [{ id: "ChildNew", versionRange: "1.0.0" }],
            "net6.0": [{ id: "ChildOld", versionRange: "1.0.0" }],
        },
    });
    fc.add("ChildNew", "1.0.0");
    fc.add("ChildOld", "1.0.0");

    const graph = await resolve(
        [{ id: "Parent", versionRange: "1.0.0" }],
        { allowListMode: "off", flatContainer: fc, cacheDir: nextCacheDir() },
    );
    const ids = graph.packages.map((p) => p.id).sort();
    assert.deepEqual(ids, ["ChildNew", "Parent"]);
});

test("resolve: lock replay short-circuits the graph walk", async () => {
    const fc = new MockFlatContainer();
    const { sha256: aHash } = fc.add("A", "1.0.0");
    const lock = {
        schemaVersion: LOCK_SCHEMA_VERSION,
        generator: "carbide",
        generatedAt: "2026-04-18T00:00:00Z",
        packages: [
            {
                id: "A",
                version: "1.0.0",
                sha256: aHash,
                requestedBy: ["<root>"],
                dependencies: [],
                libFolder: "net10.0",
            },
        ],
        warnings: [],
    };
    const graph = await resolve(
        [{ id: "A", versionRange: "1.0.0" }],
        { allowListMode: "off", flatContainer: fc, cacheDir: nextCacheDir(), lock },
    );
    assert.equal(graph.packages.length, 1);
    assert.equal(graph.packages[0].sha256, aHash);
    assert.equal(graph.references.length, 1);
    // replayLock returns the input lock object verbatim.
    assert.equal(graph.lock, lock);
});

test("resolve: lock replay throws on sha256 mismatch (MSNUGET040)", async () => {
    const fc = new MockFlatContainer();
    fc.add("A", "1.0.0");
    const lock = {
        schemaVersion: LOCK_SCHEMA_VERSION,
        generator: "carbide",
        generatedAt: "2026-04-18T00:00:00Z",
        packages: [
            {
                id: "A",
                version: "1.0.0",
                sha256: "0".repeat(64), // wrong
                requestedBy: ["<root>"],
                dependencies: [],
                libFolder: "net10.0",
            },
        ],
        warnings: [],
    };
    await assert.rejects(
        () =>
            resolve([{ id: "A", versionRange: "1.0.0" }], {
                allowListMode: "off",
                flatContainer: fc,
                cacheDir: nextCacheDir(),
                lock,
            }),
        /Integrity mismatch.+MSNUGET040/,
    );
});

// Regression for review R1 M1 / R2 §8 — earlier, lock replay short-circuited the allow-list
// and safety gates. A lock generated under one policy could carry a disallowed package into
// a stricter session and still land untouched (only the sha256 was verified). Under strict
// mode the replay must reject a disallowed package ID.
test("resolve: lock replay enforces allow-list under strict mode", async () => {
    const fc = new MockFlatContainer();
    const { sha256: notAllowedHash } = fc.add("NotAllowed.Pkg", "1.0.0");
    const lock = {
        schemaVersion: LOCK_SCHEMA_VERSION,
        generator: "carbide",
        generatedAt: "2026-04-18T00:00:00Z",
        packages: [
            {
                id: "NotAllowed.Pkg",
                version: "1.0.0",
                sha256: notAllowedHash,
                requestedBy: ["<root>"],
                dependencies: [],
                libFolder: "net10.0",
            },
        ],
        warnings: [],
    };
    await assert.rejects(
        () =>
            resolve([{ id: "NotAllowed.Pkg", versionRange: "1.0.0" }], {
                allowListMode: "strict",
                flatContainer: fc,
                cacheDir: nextCacheDir(),
                lock,
            }),
        (err) => err instanceof AllowListRefusedError && err.packageId === "NotAllowed.Pkg",
    );
});
