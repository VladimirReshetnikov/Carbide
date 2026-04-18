// M5 determinism gate: two sequential builds on the same inputs produce byte-identical PE.
// This is the foundation for the --project vs --source byte-identity acceptance in M5.7.
import { test } from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { CarbideSession } from "../../dist/index.js";

function sha256(bytes) {
    const h = createHash("sha256");
    h.update(bytes);
    return h.digest("hex");
}

test("two sequential project.build() calls yield byte-identical PE", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const source = `namespace Det; public static class Thing { public static string Describe(int v) => $"Thing<{v}>"; }`;

    const p1 = session.createProject({ assemblyName: "DetAsm" });
    p1.addSource("Thing.cs", source);
    const build1 = await p1.build();
    assert.equal(build1.success, true, JSON.stringify(build1.diagnostics));

    const p2 = session.createProject({ assemblyName: "DetAsm" });
    p2.addSource("Thing.cs", source);
    const build2 = await p2.build();
    assert.equal(build2.success, true);

    assert.equal(sha256(build1.pe), sha256(build2.pe), "PE bytes must be deterministic across builds");
});

test("different assembly names produce different PE bytes", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const source = `namespace X; public static class Y {}`;

    const p1 = session.createProject({ assemblyName: "AsmA" });
    p1.addSource("Y.cs", source);
    const b1 = await p1.build();

    const p2 = session.createProject({ assemblyName: "AsmB" });
    p2.addSource("Y.cs", source);
    const b2 = await p2.build();

    assert.notEqual(sha256(b1.pe), sha256(b2.pe), "different assembly names must change PE identity");
});
