// M4 in-process PE emission. project.build() returns PE + PDB bytes without running the
// user's code; on compile failure pe/pdb are absent and diagnostics carry the reason.
import { test } from "node:test";
import assert from "node:assert/strict";
import { CarbideSession } from "../../dist/index.js";

test("project.build() returns PE + portable-PDB bytes on success", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject({ assemblyName: "BuildEmit" });
    project.addSource(
        "Thing.cs",
        `namespace MyLib; public static class Thing { public static string Describe(int v) => $"Thing<{v}>"; }`,
    );

    const build = await project.build();
    assert.equal(build.success, true, JSON.stringify(build.diagnostics));
    assert.ok(build.pe instanceof Uint8Array, "pe should be a Uint8Array");
    assert.ok(build.pe.length > 0, "pe should be non-empty");
    assert.ok(build.pdb instanceof Uint8Array, "pdb should be a Uint8Array");
    assert.ok(build.pdb.length > 0, "pdb should be non-empty");

    // A PE file begins with the DOS 'MZ' magic.
    assert.equal(build.pe[0], 0x4d, "pe[0] should be 'M'");
    assert.equal(build.pe[1], 0x5a, "pe[1] should be 'Z'");

    // Portable PDB uses the 'BSJB' metadata magic at offset 0.
    assert.equal(build.pdb[0], 0x42, "pdb[0] should be 'B'");
    assert.equal(build.pdb[1], 0x53, "pdb[1] should be 'S'");
    assert.equal(build.pdb[2], 0x4a, "pdb[2] should be 'J'");
    assert.equal(build.pdb[3], 0x42, "pdb[3] should be 'B'");

    assert.deepEqual(build.diagnostics, []);
    assert.ok(build.durationMs >= 0);
});

test("project.build() on a syntax error returns no PE and carries diagnostics", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject();
    project.addSource("Broken.cs", `class X {`);

    const build = await project.build();
    assert.equal(build.success, false);
    assert.equal(build.pe, undefined);
    assert.equal(build.pdb, undefined);
    const errors = build.diagnostics.filter((d) => d.severity === "error");
    assert.ok(errors.length >= 1, `expected diagnostics, got: ${JSON.stringify(build)}`);
});
