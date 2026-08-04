// P0.1 (post-M9 usability plan §2.1) — the MSCAP00* capture-bypass advisories.
//
// Writes made through `Console.OpenStandardOutput()` / `OpenStandardError()` never reach
// Carbide's `Console.SetOut` capture: Mono-WASM sends them down the file-descriptor path,
// so they land on the host process's real stdio and are absent from the returned
// RunResult. The runtime's fd layer is not a supported extension point, so Carbide reports
// the call sites instead of silently losing the bytes.
//
// These tests are the contract for that reporting: which call sites are flagged, which are
// not, and that the advisory never masquerades as a compile error.

import { test } from "node:test";
import assert from "node:assert/strict";
import { CarbideSession } from "../../dist/index.js";

const advisoriesOf = (result) => result.diagnostics.filter((d) => d.id.startsWith("MSCAP"));
const errorsOf = (result) => result.diagnostics.filter((d) => d.severity === "error");

test("MSCAP001 flags Console.OpenStandardOutput and names the field the bytes are missing from", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    project.addSource(
        "Program.cs",
        [
            "using System.Text;",
            'Console.WriteLine("captured");',
            "var raw = Console.OpenStandardOutput();",
            'var bytes = Encoding.UTF8.GetBytes("escaped" + Environment.NewLine);',
            "raw.Write(bytes, 0, bytes.Length);",
            "raw.Flush();",
        ].join("\n"),
    );

    const result = await project.run();
    assert.equal(result.success, true);
    // The whole point of the advisory: this text is NOT in stdOut.
    assert.equal(result.stdOut, "captured\n");

    const advisories = advisoriesOf(result);
    assert.equal(advisories.length, 1, `expected exactly one advisory, got ${JSON.stringify(advisories)}`);
    const [advisory] = advisories;
    assert.equal(advisory.id, "MSCAP001");
    assert.equal(advisory.severity, "warning");
    assert.equal(advisory.path, "Program.cs");
    assert.equal(advisory.lineStart, 3, "advisory should point at the OpenStandardOutput call site");
    assert.match(advisory.message, /OpenStandardOutput/);
    assert.match(advisory.message, /RunResult\.stdOut/);
});

test("MSCAP001 flags Console.OpenStandardError with the stderr wording", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    project.addSource(
        "Program.cs",
        [
            "using System.Text;",
            "var raw = Console.OpenStandardError();",
            'var bytes = Encoding.UTF8.GetBytes("escaped" + Environment.NewLine);',
            "raw.Write(bytes, 0, bytes.Length);",
        ].join("\n"),
    );

    const result = await project.run();
    const advisories = advisoriesOf(result);
    assert.equal(advisories.length, 1);
    assert.equal(advisories[0].id, "MSCAP001");
    assert.match(advisories[0].message, /OpenStandardError/);
    assert.match(advisories[0].message, /RunResult\.stdErr/);
});

test("MSCAP002 flags Console.OpenStandardInput", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    project.addSource(
        "Program.cs",
        ["var raw = Console.OpenStandardInput();", "System.GC.KeepAlive(raw);"].join("\n"),
    );

    const result = await project.run({ stdin: "ignored by the handle\n" });
    const advisories = advisoriesOf(result);
    assert.equal(advisories.length, 1);
    assert.equal(advisories[0].id, "MSCAP002");
    assert.equal(advisories[0].severity, "warning");
    assert.match(advisories[0].message, /Console\.In/);
});

test("a bare call through `using static System.Console` is still flagged", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    project.addSource(
        "Program.cs",
        [
            "using static System.Console;",
            "var raw = OpenStandardOutput();",
            "System.GC.KeepAlive(raw);",
        ].join("\n"),
    );

    const result = await project.run();
    assert.equal(advisoriesOf(result).length, 1, "the detector must bind symbols, not match text");
    assert.equal(advisoriesOf(result)[0].id, "MSCAP001");
});

test("a user-defined method with the same name is not flagged", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    project.addSource(
        "Program.cs",
        [
            "static System.IO.Stream OpenStandardOutput() => System.IO.Stream.Null;",
            "var stream = OpenStandardOutput();",
            'Console.WriteLine(stream.GetType().Name);',
        ].join("\n"),
    );

    const result = await project.run();
    assert.equal(result.success, true);
    assert.equal(result.stdOut, "NullStream\n");
    assert.deepEqual(advisoriesOf(result), [], "binding must discriminate the user's own method");
});

test("a clean program carries no advisories", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    project.addSource(
        "Program.cs",
        ['Console.WriteLine("out");', 'Console.Error.WriteLine("err");'].join("\n"),
    );

    const result = await project.run();
    assert.equal(result.success, true);
    assert.deepEqual(result.diagnostics, []);
});

test("the advisory rides along with an uncaught exception without becoming a compile error", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    project.addSource(
        "Program.cs",
        [
            "var raw = Console.OpenStandardOutput();",
            "System.GC.KeepAlive(raw);",
            'throw new InvalidOperationException("boom");',
        ].join("\n"),
    );

    const result = await project.run();
    assert.equal(result.success, false);
    assert.ok(result.uncaughtException, "the run failed at runtime, not at compile time");
    assert.match(result.uncaughtException, /InvalidOperationException/);
    assert.equal(advisoriesOf(result).length, 1);
    // Consumers branch on severity; an advisory must never look like a compile failure.
    assert.deepEqual(errorsOf(result), []);
});

test("a compile failure reports compile diagnostics only", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    // The bypass call is present, but the program never compiles — advisories are computed
    // from a successful compilation, so only the compile errors come back.
    project.addSource("Program.cs", ["var raw = Console.OpenStandardOutput()", "this is not C#"].join("\n"));

    const result = await project.run();
    assert.equal(result.success, false);
    assert.ok(errorsOf(result).length > 0, "expected compile errors");
    assert.deepEqual(advisoriesOf(result), []);
});
