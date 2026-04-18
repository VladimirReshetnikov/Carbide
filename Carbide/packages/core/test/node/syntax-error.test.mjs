import { test } from "node:test";
import assert from "node:assert/strict";
import { CarbideSession } from "../../dist/index.js";

test("syntax error in Program.cs surfaces with path + span populated", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    project.addSource("Program.cs", "class X {");

    const diagnostics = await project.getDiagnostics();
    const errors = diagnostics.filter((d) => d.severity === "error");
    assert.ok(errors.length >= 1, `expected at least one error, got ${JSON.stringify(diagnostics)}`);

    const first = errors[0];
    assert.equal(first.path, "Program.cs", "error should carry the source path");
    assert.ok(typeof first.spanStart === "number" && first.spanStart >= 0);
    assert.ok(typeof first.spanEnd === "number" && first.spanEnd >= first.spanStart);
    assert.ok(first.lineStart !== undefined && first.lineStart > 0);
});
