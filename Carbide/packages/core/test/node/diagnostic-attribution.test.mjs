// M2.4 diagnostic-attribution guardrail: three distinct path sources.
// 1. User document: path field matches caller-supplied path.
// 2. Hidden Carbide.GlobalUsings.g.cs: path matches the reserved name, diagnostic severity
//    stays benign (hidden "unused using" warnings are fine, errors must not originate there).
// 3. Compilation-wide errors without a source location: path is null-or-empty.
import { test } from "node:test";
import assert from "node:assert/strict";
import { CarbideSession } from "../../dist/index.js";

test("user document errors carry the caller-supplied path", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject();
    project.addSource("MyLib/Helper.cs", `namespace X; public class Y { public int NotReturning => }`);

    const diag = await project.getDiagnostics();
    const userErrors = diag.filter((d) => d.severity === "error" && d.path === "MyLib/Helper.cs");
    assert.ok(userErrors.length >= 1, `expected error attributed to 'MyLib/Helper.cs', got: ${JSON.stringify(diag)}`);
});

test("implicit-usings document never surfaces error-severity diagnostics in a clean project", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject();
    project.addSource("Program.cs", `Console.WriteLine("hi");`);

    const diag = await project.getDiagnostics();
    const badFromUsings = diag.filter(
        (d) => d.severity === "error" && d.path === "Carbide.GlobalUsings.g.cs",
    );
    assert.equal(
        badFromUsings.length,
        0,
        `implicit-usings document surfaced errors: ${JSON.stringify(badFromUsings, null, 2)}`,
    );
});

test("compilation-wide 'no Main' error surfaces with null or empty path", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject();
    // A library-shaped file — no top-level statements, no Main, ConsoleApplication output.
    project.addSource(
        "Lib.cs",
        `namespace Lib; public static class C { public static int X() => 1; }`,
    );

    const result = await project.run();
    // Either: CS5001 "no entry point" comes through as a CompileFailure (success=false,
    // diagnostics non-empty with path null/empty), or the runtime errors on load.
    assert.equal(result.success, false, JSON.stringify(result));
    const noLocation = (result.diagnostics ?? []).filter(
        (d) => d.severity === "error" && (d.path === null || d.path === undefined || d.path === ""),
    );
    assert.ok(
        noLocation.length >= 1,
        `expected at least one compilation-wide error without a path, got: ${JSON.stringify(result)}`,
    );
});

test("GlobalUsings document does not trigger CS8802 when user adds a TLS file", async (t) => {
    // M2 R13 regression: global-using directives must not count as top-level statements.
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject();
    project.addSource("Program.cs", `Console.Write("tls-only");`);
    const result = await project.run();
    assert.equal(result.success, true, JSON.stringify(result));
    assert.equal(result.stdOut, "tls-only");
});
