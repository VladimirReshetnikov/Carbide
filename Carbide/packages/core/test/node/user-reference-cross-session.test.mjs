// M3 cross-session acceptance: a handle from session A cannot be attached to a project in
// session B. Catches accidental handle sharing with a clear, typed error (architecture §5 D28).
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { CarbideSession } from "../../dist/index.js";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const FIXTURE = path.resolve(HERE, "../fixtures/helper-dll");

test("attaching a handle to a project in a different session throws", async (t) => {
    const sessionA = await CarbideSession.initializeAsync();
    const sessionB = await CarbideSession.initializeAsync();
    t.after(async () => {
        await sessionA.shutdown();
        await sessionB.shutdown();
    });

    const bytes = new Uint8Array(readFileSync(path.join(FIXTURE, "MyHelper.dll")));
    const handleA = sessionA.addReference(bytes, "MyHelper");

    const projectB = sessionB.createProject();
    assert.throws(
        () => projectB.addReference(handleA),
        /different session|session-scoped|cross-session/i,
    );
});

test("handles become invalid after session.shutdown()", async () => {
    const session = await CarbideSession.initializeAsync();
    const bytes = new Uint8Array(readFileSync(path.join(FIXTURE, "MyHelper.dll")));
    const handle = session.addReference(bytes, "MyHelper");

    await session.shutdown();
    assert.equal(handle.disposed, true, "handle should be marked disposed after shutdown");
});
