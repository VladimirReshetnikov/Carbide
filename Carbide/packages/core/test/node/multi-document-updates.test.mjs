import { test } from "node:test";
import assert from "node:assert/strict";
import { CarbideSession } from "../../dist/index.js";

test("addSource on duplicate path throws", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    project.addSource("Program.cs", `Console.Write("x");`);
    assert.throws(
        () => project.addSource("Program.cs", `Console.Write("y");`),
        /already in the project/i,
    );
});

test("updateSource on unknown path throws", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    assert.throws(
        () => project.updateSource("Nowhere.cs", `class X {}`),
        /unknown document path/i,
    );
});

test("removeSource on unknown path is a silent no-op (D16)", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    // Must not throw; must leave the project usable.
    project.removeSource("Never.cs");
    project.addSource("Program.cs", `Console.Write("ok");`);
    const result = await project.run();
    assert.equal(result.stdOut, "ok");
});

test("updateSource replaces content and run reflects the new source", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    project.addSource("Program.cs", `Console.Write("before");`);
    const r1 = await project.run();
    assert.equal(r1.stdOut, "before");

    project.updateSource("Program.cs", `Console.Write("after");`);
    const r2 = await project.run();
    assert.equal(r2.stdOut, "after");
});

test("removeSource drops types so dependent code fails to compile", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    project.addSource(
        "Helper.cs",
        `namespace MyApp; public static class H { public static string S() => "h"; }`,
    );
    project.addSource(
        "Program.cs",
        `using MyApp; Console.Write(H.S());`,
    );

    const r1 = await project.run();
    assert.equal(r1.success, true, JSON.stringify(r1));
    assert.equal(r1.stdOut, "h");

    project.removeSource("Helper.cs");
    const diag = await project.getDiagnostics();
    const errors = diag.filter((d) => d.severity === "error");
    assert.ok(
        errors.length >= 1,
        `expected errors after removing Helper.cs, got: ${JSON.stringify(diag, null, 2)}`,
    );
});

test("reserved path 'Carbide.GlobalUsings.g.cs' cannot be added, updated, or removed", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    assert.throws(
        () => project.addSource("Carbide.GlobalUsings.g.cs", `// ...`),
        /reserved path/i,
    );
    assert.throws(
        () => project.updateSource("Carbide.GlobalUsings.g.cs", `// ...`),
        /reserved path/i,
    );
    assert.throws(
        () => project.removeSource("Carbide.GlobalUsings.g.cs"),
        /reserved path/i,
    );
});

test("path identity is byte-for-byte; casing matters (D17)", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    // Two paths differing only in case are distinct documents. The first supplies the TLS
    // program; the second is a stub class in a different file.
    project.addSource("Program.cs", `Console.Write("lower");`);
    project.addSource("PROGRAM.cs", `public static class DummyUpper {}`);

    const result = await project.run();
    assert.equal(result.success, true, JSON.stringify(result));
    assert.equal(result.stdOut, "lower");
});
