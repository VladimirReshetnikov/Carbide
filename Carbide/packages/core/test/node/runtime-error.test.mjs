import { test } from "node:test";
import assert from "node:assert/strict";
import { CarbideSession } from "../../dist/index.js";

test("runtime FormatException surfaces as an uncaught exception", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    project.addSource("Program.cs", `Console.WriteLine(int.Parse("not-a-number"));`);

    const result = await project.run();
    assert.equal(result.success, false);
    assert.ok(result.uncaughtException, `expected uncaughtException text, got ${JSON.stringify(result)}`);
    assert.match(result.uncaughtException, /FormatException/, "expected FormatException to be reported");
    assert.match(result.stdErr, /FormatException/, "stderr should echo the exception");
});
