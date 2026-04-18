import { test } from "node:test";
import assert from "node:assert/strict";
import { CarbideSession } from "../../dist/index.js";

test("hello world round-trips through build + run", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    project.addSource("Program.cs", 'Console.WriteLine("hello");');

    const diagnostics = await project.getDiagnostics();
    assert.deepEqual(
        diagnostics.filter((d) => d.severity === "error"),
        [],
        "expected zero error-severity diagnostics",
    );

    const result = await project.run();
    assert.equal(result.success, true, `run failed: ${JSON.stringify(result)}`);
    assert.equal(result.stdOut, "hello\n");
    assert.equal(result.stdErr, "");
    assert.equal(result.uncaughtException ?? null, null);
    assert.equal(result.exitCode ?? 0, 0);
});
