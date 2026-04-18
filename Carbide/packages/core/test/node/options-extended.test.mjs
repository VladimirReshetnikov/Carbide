// M5 extended ProjectOptions: defineConstants, nullable, implicitUsings-false, rootNamespace.
import { test } from "node:test";
import assert from "node:assert/strict";
import { CarbideSession } from "../../dist/index.js";

test("defineConstants unlocks #if-guarded code", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject({
        assemblyName: "DefineDemo",
        defineConstants: ["MY_FEATURE"],
    });
    project.addSource(
        "Program.cs",
        `
        #if MY_FEATURE
        Console.Write("on");
        #else
        Console.Write("off");
        #endif
        `.trim(),
    );

    const result = await project.run();
    assert.equal(result.success, true, JSON.stringify(result));
    assert.equal(result.stdOut, "on");
});

test("missing defineConstants leaves #if-guarded code off", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject({ assemblyName: "DefineDemoOff" });
    project.addSource(
        "Program.cs",
        `
        #if MY_FEATURE
        Console.Write("on");
        #else
        Console.Write("off");
        #endif
        `.trim(),
    );

    const result = await project.run();
    assert.equal(result.success, true);
    assert.equal(result.stdOut, "off");
});

test("implicitUsings: false means bare Console.WriteLine does NOT compile", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject({
        assemblyName: "NoImplicit",
        implicitUsings: false,
    });
    project.addSource("Program.cs", `Console.WriteLine("hi");`);
    const diagnostics = await project.getDiagnostics();
    const errors = diagnostics.filter((d) => d.severity === "error");
    assert.ok(
        errors.length >= 1,
        `expected errors with implicitUsings:false, got: ${JSON.stringify(diagnostics)}`,
    );
});

test("implicitUsings: false + explicit using System compiles cleanly", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject({
        assemblyName: "ExplicitUsing",
        implicitUsings: false,
    });
    project.addSource("Program.cs", `using System; Console.Write("ok");`);
    const result = await project.run();
    assert.equal(result.success, true, JSON.stringify(result));
    assert.equal(result.stdOut, "ok");
});

test("languageVersion flows through to Roslyn parse options", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    // C# 7.0 predates pattern matching on strings — a C# 8 feature. Setting LangVersion=7.0
    // should flag an error when we use modern pattern syntax.
    const project = session.createProject({
        assemblyName: "LangVer",
        languageVersion: "7.0",
    });
    project.addSource(
        "Program.cs",
        `
        string s = "foo";
        if (s is { Length: > 0 }) Console.Write("ok");
        `.trim(),
    );
    const diagnostics = await project.getDiagnostics();
    const errors = diagnostics.filter((d) => d.severity === "error");
    assert.ok(
        errors.length >= 1,
        `expected LangVersion=7.0 to reject property pattern, got: ${JSON.stringify(diagnostics)}`,
    );
});

test("nullable: true flags unassigned non-null initialisers", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject({
        assemblyName: "NullableOn",
        nullable: true,
    });
    project.addSource(
        "Program.cs",
        `
        #nullable enable
        string x = null;
        System.Console.Write(x);
        `.trim(),
    );
    const diagnostics = await project.getDiagnostics();
    const warnings = diagnostics.filter((d) => d.severity === "warning");
    assert.ok(
        warnings.some((w) => w.id === "CS8600"),
        `expected CS8600 (converting null to non-nullable), got: ${JSON.stringify(diagnostics)}`,
    );
});
