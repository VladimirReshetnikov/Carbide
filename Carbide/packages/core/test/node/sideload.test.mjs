// core-P1: CarbideOptions.sideload auto-resolves ref-pack DLLs and feeds them through
// session.addReference. Verified here against the monorepo sibling @carbide-ui/refs-avalonia
// (8 DLLs, Avalonia 12.0.1). See plan §10.1 and §3.2 core-P1.
import { test } from "node:test";
import assert from "node:assert/strict";
import { CarbideSession } from "../../dist/index.js";

test("sideload @carbide-ui/refs-avalonia wires Avalonia refs into the session", async (t) => {
    const session = await CarbideSession.initializeAsync({
        sideload: ["@carbide-ui/refs-avalonia"],
    });
    t.after(async () => await session.shutdown());

    // A trivial Avalonia-referencing program must compile now that the refs are attached.
    const project = session.createProject({ assemblyName: "SideloadSmoke" });
    project.addSource("App.cs", `
        using Avalonia;
        public class App : Application { public override void Initialize() {} }
    `);

    const build = await project.build();
    assert.equal(build.success, true, JSON.stringify(build.diagnostics));
    assert.ok(build.pe instanceof Uint8Array);
    assert.ok(build.pe.length > 0);
});

test("sideload surfaces a descriptive error when a package is not locatable", async (t) => {
    await assert.rejects(
        CarbideSession.initializeAsync({
            sideload: ["@carbide-ui/does-not-exist"],
        }),
        (err) => {
            // The error text should name the package and mention the sideload context.
            assert.ok(err instanceof Error);
            assert.match(err.message, /sideload.*@carbide-ui\/does-not-exist/);
            return true;
        },
    );
});

test("sideload with empty array is a no-op", async (t) => {
    const session = await CarbideSession.initializeAsync({ sideload: [] });
    t.after(async () => await session.shutdown());
    const project = session.createProject();
    project.addSource("X.cs", `public class X {}`);
    const build = await project.build();
    assert.equal(build.success, true, JSON.stringify(build.diagnostics));
});
