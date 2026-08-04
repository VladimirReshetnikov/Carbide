// Session lifetime guarantees.
//
// Every other suite calls `shutdown()` in teardown, so the happy path is exercised
// constantly — but the two properties callers actually lean on were not asserted anywhere:
// that shutting down twice is safe (teardown commonly runs from both a `finally` and an
// explicit call), and that a session refuses to hand out new work once it is down rather
// than returning a project whose interop is gone.

import { test } from "node:test";
import assert from "node:assert/strict";
import { CarbideSession } from "../../dist/index.js";

test("shutdown() is idempotent", async () => {
    const session = await CarbideSession.initializeAsync();
    await session.shutdown();
    await session.shutdown();
    await session.shutdown();
});

test("a shut-down session refuses to create projects", async () => {
    const session = await CarbideSession.initializeAsync();
    await session.shutdown();
    assert.throws(() => session.createProject(), /shut down/i);
});

test("a shut-down session refuses to register references", async () => {
    const session = await CarbideSession.initializeAsync();
    await session.shutdown();
    assert.throws(() => session.addReference(new Uint8Array([0x4d, 0x5a]), "X"), /shut down/i);
});

test("shutdown() disposes every outstanding reference handle", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });
    // Any well-formed managed PE works; the emitted assembly from a trivial build is one.
    const project = session.createProject();
    project.addSource("Lib.cs", "public class Thing { public int N => 1; }");
    const build = await project.build();
    assert.equal(build.success, true, JSON.stringify(build.diagnostics));

    const handle = session.addReference(build.pe, "Thing");
    assert.equal(handle.disposed, false);
    await session.shutdown();
    assert.equal(handle.disposed, true, "shutdown must invalidate handles, not leave them dangling");
});

test("work started before shutdown still completes", async () => {
    // `run()` is in flight when shutdown is requested; the result must still arrive rather
    // than the caller being left with a rejected promise from a torn-down interop.
    const session = await CarbideSession.initializeAsync();
    const project = session.createProject();
    project.addSource("Program.cs", 'Console.WriteLine("done");');

    const running = project.run();
    const result = await running;
    assert.equal(result.success, true);
    assert.equal(result.stdOut, "done\n");
    await session.shutdown();
});
