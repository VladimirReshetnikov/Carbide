// core-P3: BuildResult carries peSchemaVersion and primaryAssemblyName on successful
// builds, and omits them on failed builds. See plan §10.3 and proposal §10.3.
import { test } from "node:test";
import assert from "node:assert/strict";
import { CarbideSession } from "../../dist/index.js";

test("BuildResult.peSchemaVersion is 1 on successful build", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject({ assemblyName: "PeSchemaVersionCheck" });
    project.addSource("X.cs", `public class X {}`);

    const build = await project.build();
    assert.equal(build.success, true, JSON.stringify(build.diagnostics));
    assert.equal(build.peSchemaVersion, 1);
});

test("BuildResult.primaryAssemblyName reflects the project's assembly name", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject({ assemblyName: "CustomAssemblyName" });
    project.addSource("X.cs", `public class X {}`);

    const build = await project.build();
    assert.equal(build.success, true, JSON.stringify(build.diagnostics));
    assert.equal(build.primaryAssemblyName, "CustomAssemblyName");
});

test("BuildResult.peSchemaVersion and primaryAssemblyName are omitted on failed build", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject({ assemblyName: "FailedBuild" });
    project.addSource("Broken.cs", `class X {`);

    const build = await project.build();
    assert.equal(build.success, false);
    assert.equal(build.peSchemaVersion, undefined);
    assert.equal(build.primaryAssemblyName, undefined);
});
