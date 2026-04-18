// M3 primary acceptance: user-supplied DLL, added via session.addReference, attached to a
// project via project.addReference, referenced from Program.cs, and run to produce the
// expected stdout.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { CarbideSession } from "../../dist/index.js";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const FIXTURE = path.resolve(HERE, "../fixtures/helper-dll");

function loadHelperBytes() {
    const dll = path.join(FIXTURE, "MyHelper.dll");
    try {
        return new Uint8Array(readFileSync(dll));
    } catch (e) {
        throw new Error(
            `MyHelper.dll not found at ${dll}. Run \`npm run build:test-fixtures\` first. ` +
                `Underlying error: ${e.message}`,
        );
    }
}

test("user DLL registered via addReference + attached to a project compiles and runs", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const helperBytes = loadHelperBytes();
    const handle = session.addReference(helperBytes, "MyHelper");
    assert.equal(typeof handle.id, "string");
    assert.equal(handle.id.length, 32, "handle id should be a 32-char GUID hex");
    assert.equal(handle.name, "MyHelper");
    assert.equal(handle.disposed, false);

    const project = session.createProject();
    project.addReference(handle);
    project.addSource(
        "Program.cs",
        `
        using MyHelper;
        Console.WriteLine(Thing.Describe(42));
        `.trim(),
    );

    const diagnostics = await project.getDiagnostics();
    assert.deepEqual(
        diagnostics.filter((d) => d.severity === "error"),
        [],
        `expected no errors, got: ${JSON.stringify(diagnostics, null, 2)}`,
    );

    const result = await project.run();
    assert.equal(result.success, true, JSON.stringify(result));
    assert.equal(result.stdOut, "Thing<42>\n");
});

test("project without the attached reference fails to find the helper's namespace", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject();
    // Note: intentionally no addReference here — we expect CS0234/CS0246.
    project.addSource(
        "Program.cs",
        `
        using MyHelper;
        Console.WriteLine(Thing.Describe(42));
        `.trim(),
    );

    const diagnostics = await project.getDiagnostics();
    const errors = diagnostics.filter((d) => d.severity === "error");
    assert.ok(errors.length >= 1, `expected errors without the reference, got: ${JSON.stringify(diagnostics)}`);
});
