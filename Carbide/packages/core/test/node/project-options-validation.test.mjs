// ProjectOptions values that Carbide cannot honour must fail loudly.
//
// `languageVersion` used to fall through to the default when Roslyn could not parse it, so a
// typo like "lastest" compiled as if nothing had been asked for. `csc` rejects the same
// input with CS1617; so does Carbide.

import { test } from "node:test";
import assert from "node:assert/strict";
import { CarbideSession } from "../../dist/index.js";

test("an unrecognised languageVersion throws instead of silently using the default", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    assert.throws(
        () => session.createProject({ languageVersion: "lastest" }),
        (error) => {
            assert.match(String(error.message), /languageVersion 'lastest'/);
            assert.match(String(error.message), /latest|preview/);
            return true;
        },
    );
});

test("recognised languageVersion spellings are accepted", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    for (const languageVersion of ["latest", "preview", "latestMajor", "default", "12"]) {
        const project = session.createProject({ languageVersion });
        project.addSource("Program.cs", `Console.WriteLine("${languageVersion}");`);
        const result = await project.run();
        assert.equal(result.success, true, `${languageVersion}: ${JSON.stringify(result.diagnostics)}`);
        assert.equal(result.stdOut, `${languageVersion}\n`);
    }
});

test("omitting languageVersion still uses Roslyn's default", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject({});
    project.addSource("Program.cs", `Console.WriteLine("default");`);
    const result = await project.run();
    assert.equal(result.success, true);
    assert.equal(result.stdOut, "default\n");
});

test("an old languageVersion is honoured, not ignored", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    // Top-level statements are C# 9+. Under C# 8 the same source must fail to compile —
    // which is the observable proof that the value reached Roslyn.
    const project = session.createProject({ languageVersion: "8" });
    project.addSource("Program.cs", `Console.WriteLine("hi");`);
    const result = await project.run();
    assert.equal(result.success, false, "C# 8 must reject top-level statements");
    assert.ok(
        result.diagnostics.some((d) => d.severity === "error"),
        `expected a language-version error, got ${JSON.stringify(result.diagnostics)}`,
    );
});
