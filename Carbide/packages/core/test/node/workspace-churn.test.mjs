// M2.6 workspace-churn regression. Stresses the AdhocWorkspace update path under mixed
// Add / Update / Remove calls to catch silent drops (R9), stale compilations (R12), and
// OpenDocument leaks (R14). Also times the full loop to establish a perf baseline.
import { test } from "node:test";
import assert from "node:assert/strict";
import { CarbideSession } from "../../dist/index.js";

test("50 interleaved add/update/getDiagnostics iterations stay consistent", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject();
    project.addSource("Program.cs", `Console.Write("start");`);

    // Each iteration: add a new helper, update it, get diagnostics, verify no errors.
    const N = 50;
    const start = Date.now();
    for (let i = 0; i < N; i++) {
        const path = `Helper${i}.cs`;
        project.addSource(path, `public static class H${i} { public static int Value => ${i}; }`);
        project.updateSource(path, `public static class H${i} { public static int Value => ${i * 2}; }`);
        const diag = await project.getDiagnostics();
        const errors = diag.filter((d) => d.severity === "error");
        assert.deepEqual(
            errors,
            [],
            `iteration ${i} produced errors: ${JSON.stringify(errors)}`,
        );
    }
    const elapsed = Date.now() - start;
    // Acceptance §2.5 target: 10 successive update/diag cycles in under 5s; we ran 50 so
    // the budget scales to 25s. In practice Node comes in well under.
    assert.ok(elapsed < 25_000, `churn loop too slow: ${elapsed}ms for ${N} iterations`);
});

test("100 add + remove cycles with fresh paths leave a stable workspace", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject();
    project.addSource("Program.cs", `Console.Write("x");`);

    for (let i = 0; i < 100; i++) {
        const path = `Ephemeral${i}.cs`;
        project.addSource(path, `public static class E${i} {}`);
        project.removeSource(path);
    }

    // Project still runnable after all that churn.
    const result = await project.run();
    assert.equal(result.success, true, JSON.stringify(result));
    assert.equal(result.stdOut, "x");
});

test("add-update-remove round-trip with cross-file references stays consistent", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject();
    project.addSource(
        "Helper.cs",
        `namespace X; public static class H { public static int V => 1; }`,
    );
    project.addSource("Program.cs", `using X; Console.Write(H.V);`);

    // Update Helper.cs so V changes; rerun reflects new value.
    project.updateSource(
        "Helper.cs",
        `namespace X; public static class H { public static int V => 7; }`,
    );
    const r1 = await project.run();
    assert.equal(r1.stdOut, "7");

    // Remove Helper.cs; Program.cs now fails to compile (H unresolved).
    project.removeSource("Helper.cs");
    const diag = await project.getDiagnostics();
    const errors = diag.filter((d) => d.severity === "error");
    assert.ok(errors.length >= 1, `expected errors after removing Helper.cs, got: ${JSON.stringify(diag)}`);

    // Re-add Helper.cs with the new signature; Program.cs now compiles and runs.
    project.addSource(
        "Helper.cs",
        `namespace X; public static class H { public static int V => 99; }`,
    );
    const r2 = await project.run();
    assert.equal(r2.success, true, JSON.stringify(r2));
    assert.equal(r2.stdOut, "99");
});
