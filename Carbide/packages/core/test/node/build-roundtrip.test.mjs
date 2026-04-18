// M4 round-trip acceptance (in-process variant of the CLI test).
// Build a library, feed its PE bytes back as a reference to a second project, run that
// project. Proves the Carbide-emitted PE is loadable via session.addReference.
import { test } from "node:test";
import assert from "node:assert/strict";
import { CarbideSession } from "../../dist/index.js";

test("Carbide-emitted PE round-trips through session.addReference", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    // Stage 1: build MyLib from source.
    const lib = session.createProject({ assemblyName: "MyLib" });
    lib.addSource(
        "Thing.cs",
        `namespace MyLib; public static class Thing { public static string Describe(int v) => $"Thing<{v}>"; }`,
    );
    const libBuild = await lib.build();
    assert.equal(libBuild.success, true, JSON.stringify(libBuild.diagnostics));
    assert.ok(libBuild.pe && libBuild.pe.length > 0);

    // Stage 2: feed the library bytes back as a reference to a fresh project.
    const libRef = session.addReference(libBuild.pe, "MyLib");
    const app = session.createProject({ assemblyName: "MyApp" });
    app.addReference(libRef);
    app.addSource(
        "Program.cs",
        `using MyLib; Console.WriteLine(Thing.Describe(42));`,
    );

    const run = await app.run();
    assert.equal(run.success, true, JSON.stringify(run));
    assert.equal(run.stdOut, "Thing<42>\n");
});

test("chained build: MyLib -> MyMid -> MyApp", async (t) => {
    // Two levels of round-tripping to prove the PE identity holds through a second layer.
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const lib = session.createProject({ assemblyName: "ChainLib" });
    lib.addSource(
        "Thing.cs",
        `namespace ChainLib; public static class Thing { public static string Describe(int v) => $"Thing<{v}>"; }`,
    );
    const libBuild = await lib.build();
    assert.equal(libBuild.success, true);

    const libRef = session.addReference(libBuild.pe, "ChainLib");

    const mid = session.createProject({ assemblyName: "ChainMid" });
    mid.addReference(libRef);
    mid.addSource(
        "Helper.cs",
        `using ChainLib; namespace ChainMid; public static class Helper { public static string Greet(int v) => "hi " + Thing.Describe(v); }`,
    );
    const midBuild = await mid.build();
    assert.equal(midBuild.success, true, JSON.stringify(midBuild.diagnostics));

    const midRef = session.addReference(midBuild.pe, "ChainMid");

    const app = session.createProject({ assemblyName: "ChainApp" });
    app.addReference(libRef);
    app.addReference(midRef);
    app.addSource(
        "Program.cs",
        `using ChainMid; Console.WriteLine(Helper.Greet(7));`,
    );
    const run = await app.run();
    assert.equal(run.success, true, JSON.stringify(run));
    assert.equal(run.stdOut, "hi Thing<7>\n");
});
