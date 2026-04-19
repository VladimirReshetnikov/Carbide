// M9 — unit tests for the project-graph module. Every test writes synthetic csproj files
// into a tmp dir and exercises `buildProjectGraph` in isolation (no runtime, no session).

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, writeFileSync, mkdirSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

import {
    buildProjectGraph,
    canonicalKey,
    ProjectGraphCycleError,
    ProjectGraphNameCollisionError,
    ProjectReferenceNotFoundError,
} from "../dist/project-graph.js";

function tmp(prefix) {
    return mkdtempSync(path.join(tmpdir(), prefix));
}

function writeCsproj(dir, name, options = {}) {
    mkdirSync(dir, { recursive: true });
    const { assemblyName, projectReferences = [], targetFramework = "net10.0" } = options;
    const props = [`<TargetFramework>${targetFramework}</TargetFramework>`];
    if (assemblyName) props.push(`<AssemblyName>${assemblyName}</AssemblyName>`);
    const refItems = projectReferences
        .map((p) => `<ProjectReference Include="${p.replace(/\\/g, "/")}"/>`)
        .join("\n    ");
    const itemGroup = refItems ? `\n  <ItemGroup>\n    ${refItems}\n  </ItemGroup>` : "";
    const xml =
        `<Project>\n  <PropertyGroup>\n    ${props.join("\n    ")}\n  </PropertyGroup>${itemGroup}\n</Project>\n`;
    const absPath = path.join(dir, name);
    writeFileSync(absPath, xml);
    return absPath;
}

test("buildProjectGraph: single root with no references yields one node", async (t) => {
    const work = tmp("carbide-m9-solo-");
    t.after(() => rmSync(work, { recursive: true, force: true }));
    const root = writeCsproj(work, "Solo.csproj", { assemblyName: "Solo" });

    const graph = await buildProjectGraph(root);
    assert.equal(graph.nodes.size, 1);
    assert.equal(graph.order.length, 1);
    assert.equal(graph.order[0].assemblyName, "Solo");
    assert.equal(graph.order[0].isRoot, true);
    assert.equal(graph.order[0].projectReferences.length, 0);
    assert.equal(graph.order[0].transitiveClosure.size, 0);
});

test("buildProjectGraph: linear chain A->B->C->D topo-sorts leaves first", async (t) => {
    const work = tmp("carbide-m9-chain-");
    t.after(() => rmSync(work, { recursive: true, force: true }));

    const dDir = path.join(work, "D");
    const cDir = path.join(work, "C");
    const bDir = path.join(work, "B");
    const aDir = path.join(work, "A");
    writeCsproj(dDir, "D.csproj", { assemblyName: "D" });
    writeCsproj(cDir, "C.csproj", { assemblyName: "C", projectReferences: ["../D/D.csproj"] });
    writeCsproj(bDir, "B.csproj", { assemblyName: "B", projectReferences: ["../C/C.csproj"] });
    const aPath = writeCsproj(aDir, "A.csproj", {
        assemblyName: "A",
        projectReferences: ["../B/B.csproj"],
    });

    const graph = await buildProjectGraph(aPath);
    assert.equal(graph.nodes.size, 4);
    assert.deepEqual(
        graph.order.map((n) => n.assemblyName),
        ["D", "C", "B", "A"],
    );
    const aNode = graph.order[graph.order.length - 1];
    assert.equal(aNode.isRoot, true);
    assert.equal(aNode.transitiveClosure.size, 3);
});

test("buildProjectGraph: diamond A->{B,C}, B->D, C->D yields 4 nodes with valid order", async (t) => {
    const work = tmp("carbide-m9-diamond-");
    t.after(() => rmSync(work, { recursive: true, force: true }));

    writeCsproj(path.join(work, "D"), "D.csproj", { assemblyName: "D" });
    writeCsproj(path.join(work, "B"), "B.csproj", {
        assemblyName: "B",
        projectReferences: ["../D/D.csproj"],
    });
    writeCsproj(path.join(work, "C"), "C.csproj", {
        assemblyName: "C",
        projectReferences: ["../D/D.csproj"],
    });
    const aPath = writeCsproj(path.join(work, "A"), "A.csproj", {
        assemblyName: "A",
        projectReferences: ["../B/B.csproj", "../C/C.csproj"],
    });

    const graph = await buildProjectGraph(aPath);
    assert.equal(graph.nodes.size, 4, "diamond must deduplicate D into a single node");

    const indexOf = (name) => graph.order.findIndex((n) => n.assemblyName === name);
    const d = indexOf("D");
    const b = indexOf("B");
    const c = indexOf("C");
    const a = indexOf("A");
    assert.ok(d < b, "D must come before B");
    assert.ok(d < c, "D must come before C");
    assert.ok(b < a, "B must come before A");
    assert.ok(c < a, "C must come before A");
    assert.equal(a, graph.order.length - 1, "A must be last");
});

test("buildProjectGraph: cycle A->B->A throws ProjectGraphCycleError", async (t) => {
    const work = tmp("carbide-m9-cycle-");
    t.after(() => rmSync(work, { recursive: true, force: true }));

    const aPath = path.join(work, "A", "A.csproj");
    const bPath = path.join(work, "B", "B.csproj");

    writeCsproj(path.join(work, "A"), "A.csproj", {
        assemblyName: "A",
        projectReferences: ["../B/B.csproj"],
    });
    writeCsproj(path.join(work, "B"), "B.csproj", {
        assemblyName: "B",
        projectReferences: ["../A/A.csproj"],
    });

    await assert.rejects(
        () => buildProjectGraph(aPath),
        (err) => {
            assert.ok(err instanceof ProjectGraphCycleError, "should throw ProjectGraphCycleError");
            assert.match(err.message, /MSPROJ001/);
            assert.ok(err.cyclePath.length >= 3);
            // First and last element of the cycle path must be the same canonical csproj.
            assert.equal(canonicalKey(err.cyclePath[0]), canonicalKey(err.cyclePath[err.cyclePath.length - 1]));
            return true;
        },
    );
    void bPath; // referenced via projectReference only
});

test("buildProjectGraph: AssemblyName collision throws ProjectGraphNameCollisionError", async (t) => {
    const work = tmp("carbide-m9-collision-");
    t.after(() => rmSync(work, { recursive: true, force: true }));

    writeCsproj(path.join(work, "Lib1"), "Lib1.csproj", { assemblyName: "Shared" });
    writeCsproj(path.join(work, "Lib2"), "Lib2.csproj", { assemblyName: "Shared" });
    const aPath = writeCsproj(path.join(work, "App"), "App.csproj", {
        assemblyName: "App",
        projectReferences: ["../Lib1/Lib1.csproj", "../Lib2/Lib2.csproj"],
    });

    await assert.rejects(
        () => buildProjectGraph(aPath),
        (err) => {
            assert.ok(err instanceof ProjectGraphNameCollisionError);
            assert.match(err.message, /MSPROJ002/);
            assert.equal(err.assemblyName, "Shared");
            assert.equal(err.csprojPaths.length, 2);
            return true;
        },
    );
});

test("buildProjectGraph: missing ProjectReference target throws MSPROJ004", async (t) => {
    const work = tmp("carbide-m9-missing-");
    t.after(() => rmSync(work, { recursive: true, force: true }));

    const aPath = writeCsproj(path.join(work, "App"), "App.csproj", {
        assemblyName: "App",
        projectReferences: ["../Missing/Missing.csproj"],
    });

    await assert.rejects(
        () => buildProjectGraph(aPath),
        (err) => {
            assert.ok(err instanceof ProjectReferenceNotFoundError);
            assert.match(err.message, /MSPROJ004/);
            return true;
        },
    );
});

test("buildProjectGraph: transitive closure correctly populated for A->B->C", async (t) => {
    const work = tmp("carbide-m9-closure-");
    t.after(() => rmSync(work, { recursive: true, force: true }));

    writeCsproj(path.join(work, "C"), "C.csproj", { assemblyName: "C" });
    writeCsproj(path.join(work, "B"), "B.csproj", {
        assemblyName: "B",
        projectReferences: ["../C/C.csproj"],
    });
    const aPath = writeCsproj(path.join(work, "A"), "A.csproj", {
        assemblyName: "A",
        projectReferences: ["../B/B.csproj"],
    });

    const graph = await buildProjectGraph(aPath);
    const a = graph.order.find((n) => n.assemblyName === "A");
    const b = graph.order.find((n) => n.assemblyName === "B");
    const c = graph.order.find((n) => n.assemblyName === "C");
    assert.ok(a && b && c);
    assert.equal(a.transitiveClosure.size, 2);
    assert.ok(a.transitiveClosure.has(canonicalKey(b.csprojPath)));
    assert.ok(a.transitiveClosure.has(canonicalKey(c.csprojPath)));
    assert.equal(b.transitiveClosure.size, 1);
    assert.ok(b.transitiveClosure.has(canonicalKey(c.csprojPath)));
    assert.equal(c.transitiveClosure.size, 0);
});

test("buildProjectGraph: falls back to csproj-filename AssemblyName when <AssemblyName> is absent", async (t) => {
    const work = tmp("carbide-m9-asmfallback-");
    t.after(() => rmSync(work, { recursive: true, force: true }));

    const root = writeCsproj(work, "FallbackName.csproj"); // no assemblyName prop.
    const graph = await buildProjectGraph(root);
    assert.equal(graph.order[0].assemblyName, "FallbackName");
});

test("buildProjectGraph: 3-cycle A->B->C->A detects and reports full cycle path", async (t) => {
    const work = tmp("carbide-m9-cycle3-");
    t.after(() => rmSync(work, { recursive: true, force: true }));

    writeCsproj(path.join(work, "A"), "A.csproj", {
        assemblyName: "A",
        projectReferences: ["../B/B.csproj"],
    });
    writeCsproj(path.join(work, "B"), "B.csproj", {
        assemblyName: "B",
        projectReferences: ["../C/C.csproj"],
    });
    writeCsproj(path.join(work, "C"), "C.csproj", {
        assemblyName: "C",
        projectReferences: ["../A/A.csproj"],
    });

    const aPath = path.join(work, "A", "A.csproj");
    await assert.rejects(
        () => buildProjectGraph(aPath),
        (err) => {
            assert.ok(err instanceof ProjectGraphCycleError);
            // Path should contain A, B, C, and wrap back to A.
            const basenames = err.cyclePath.map((p) => path.basename(p));
            assert.deepEqual(new Set(basenames), new Set(["A.csproj", "B.csproj", "C.csproj"]));
            return true;
        },
    );
});
