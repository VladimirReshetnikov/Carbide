// MSPROJ011 — `TargetFramework` selects NuGet assets, never the compile-time reference set.
//
// Carbide always compiles against net10.0 metadata, so a project declaring net8.0 still
// binds APIs introduced after net8.0. That is a silent semantic surprise unless it is named,
// which is what these tests pin.

import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { parseJsonBySentinel } from "./_helpers.mjs";

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

function scaffold(prefix, tfm) {
    const work = mkdtempSync(path.join(tmpdir(), prefix));
    writeFileSync(
        path.join(work, "Foo.csproj"),
        `<Project><PropertyGroup><TargetFramework>${tfm}</TargetFramework>` +
            `<AssemblyName>Foo</AssemblyName></PropertyGroup></Project>`,
    );
    writeFileSync(path.join(work, "P.cs"), `Console.WriteLine("hello from ${tfm}");`);
    return work;
}

test("MSPROJ011 warns that a net8.0 project still compiles against net10.0", async (t) => {
    const work = scaffold("carbide-tfm-net8-", "net8.0");
    t.after(() => rmSync(work, { recursive: true, force: true }));

    const r = runCarbide(["run", "--project", path.join(work, "Foo.csproj")]);
    assert.equal(r.status, 0, r.stderr);

    const payload = parseJsonBySentinel(r.stdout);
    assert.equal(payload.success, true, JSON.stringify(payload));
    const warning = payload.warnings.find((w) => w.code === "MSPROJ011");
    assert.ok(warning, `expected MSPROJ011, got ${JSON.stringify(payload.warnings)}`);
    assert.match(warning.message, /net10\.0 reference set/);
    // The project still builds and runs — the warning describes a semantic gap, not a failure.
    assert.match(payload.stdOut, /hello from net8\.0/);
});

test("a net10.0 project carries no MSPROJ011", async (t) => {
    const work = scaffold("carbide-tfm-net10-", "net10.0");
    t.after(() => rmSync(work, { recursive: true, force: true }));

    const r = runCarbide(["run", "--project", path.join(work, "Foo.csproj")]);
    assert.equal(r.status, 0, r.stderr);
    const payload = parseJsonBySentinel(r.stdout);
    assert.ok(!payload.warnings.some((w) => w.code === "MSPROJ011"));
});

test("a net8.0 project can still bind an API introduced after net8.0", async (t) => {
    // The concrete consequence the warning exists for. `System.Threading.Lock` is .NET 9+;
    // it compiles here because the reference set is net10.0 regardless of the declared TFM.
    const work = scaffold("carbide-tfm-net8-api-", "net8.0");
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(
        path.join(work, "P.cs"),
        [
            "using System.Threading;",
            "var gate = new Lock();",
            "lock (gate) { Console.WriteLine(\"post-net8 API bound\"); }",
        ].join("\n"),
    );

    const r = runCarbide(["run", "--project", path.join(work, "Foo.csproj")]);
    assert.equal(r.status, 0, r.stderr);
    const payload = parseJsonBySentinel(r.stdout);
    assert.match(payload.stdOut, /post-net8 API bound/);
    assert.ok(payload.warnings.some((w) => w.code === "MSPROJ011"));
});
