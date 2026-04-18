// M3 lifecycle acceptance: add/remove round-trip, malformed bytes, duplicate names.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { CarbideSession } from "../../dist/index.js";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const FIXTURE = path.resolve(HERE, "../fixtures/helper-dll");

function loadHelperBytes() {
    return new Uint8Array(readFileSync(path.join(FIXTURE, "MyHelper.dll")));
}

test("removeReference invalidates the handle and drops the ref from attached projects", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const bytes = loadHelperBytes();
    const handle = session.addReference(bytes, "MyHelper");
    assert.equal(handle.disposed, false);

    const project = session.createProject();
    project.addReference(handle);
    project.addSource("Program.cs", `using MyHelper; Console.Write(Thing.Describe(1));`);

    // First run: succeeds.
    const r1 = await project.run();
    assert.equal(r1.success, true, JSON.stringify(r1));
    assert.equal(r1.stdOut, "Thing<1>");

    // Remove the reference. Handle becomes disposed.
    session.removeReference(handle);
    assert.equal(handle.disposed, true);

    // Next getDiagnostics surfaces the missing namespace.
    const diag = await project.getDiagnostics();
    const errors = diag.filter((d) => d.severity === "error");
    assert.ok(errors.length >= 1, `expected errors after remove, got: ${JSON.stringify(diag)}`);

    // Attaching the disposed handle throws.
    assert.throws(
        () => project.addReference(handle),
        /disposed/i,
    );
});

test("removeReference on an unregistered handle is tolerant", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const bytes = loadHelperBytes();
    const handle = session.addReference(bytes, "MyHelper");
    session.removeReference(handle);
    // Second remove: handle already disposed, silent no-op.
    session.removeReference(handle);
    assert.equal(handle.disposed, true);
});

test("addReference rejects malformed bytes synchronously", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    // 16 bytes of garbage — not a PE image.
    const garbage = new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);
    assert.throws(
        () => session.addReference(garbage, "garbage"),
        /not a valid managed PE image|managed PE|CLR metadata/i,
    );
});

test("addReference rejects empty bytes", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());
    assert.throws(
        () => session.addReference(new Uint8Array(), "empty"),
        /non-empty/i,
    );
});

test("duplicate names coexist under distinct handles (D30)", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const bytes = loadHelperBytes();
    const h1 = session.addReference(bytes, "MyHelper");
    const h2 = session.addReference(bytes, "MyHelper");
    assert.notEqual(h1.id, h2.id, "two adds must return distinct handles");
    assert.equal(h1.name, "MyHelper");
    assert.equal(h2.name, "MyHelper");
});
