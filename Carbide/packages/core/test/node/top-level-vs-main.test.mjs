import { test } from "node:test";
import assert from "node:assert/strict";
import { CarbideSession } from "../../dist/index.js";

test("top-level-statements program runs", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    project.addSource("Program.cs", `Console.Write("tls-ok");`);

    const result = await project.run();
    assert.equal(result.success, true, JSON.stringify(result));
    assert.equal(result.stdOut, "tls-ok");
});

test("explicit static Main program runs", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    project.addSource(
        "Program.cs",
        `public static class Program { public static void Main() { Console.Write("main-ok"); } }`,
    );

    const result = await project.run();
    assert.equal(result.success, true, JSON.stringify(result));
    assert.equal(result.stdOut, "main-ok");
});

// Regression for review R1 §5 / R2 §4 — project.build() used to check only for
// GlobalStatementSyntax (top-level statements), so a program with an explicit
// `static Main` but no top-level statements compiled to a DLL with no entry point,
// even though project.run() happily executed the same source as a console app.
// InferOutputKind now probes for an entry point via Roslyn.
test("build() of explicit static Main program emits a ConsoleApplication", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    project.addSource(
        "Program.cs",
        `public static class Program { public static void Main() { Console.Write("main-ok"); } }`,
    );

    const build = await project.build();
    assert.equal(build.success, true, JSON.stringify(build));
    // A console-app PE carries a CLI entry-point RVA; a DLL doesn't. Sniff the PE
    // optional-header's "Subsystem" field. 3 = Console, 2 = Windows.
    // PE layout: DOS header (0..0x40) → "PE\0\0" + COFF (20 bytes) → optional header.
    // Subsystem is at offset +68 of the optional header (PE32+, which Mono uses).
    const pe = Buffer.from(build.pe);
    const peOffset = pe.readUInt32LE(0x3c);
    const optionalHeaderStart = peOffset + 4 /* PE\0\0 */ + 20 /* COFF */;
    const subsystem = pe.readUInt16LE(optionalHeaderStart + 68);
    assert.equal(subsystem, 3, "explicit-Main program should build as ConsoleApplication (Subsystem=3)");
});

test("Task<int> Main returns the awaited value", async (t) => {
    // Note: Mono-WASM is single-threaded and cannot block on monitors, so we avoid any
    // async primitive that reschedules onto a thread (e.g. Task.Yield, Task.Delay).
    // Task.FromResult completes synchronously, which is enough to exercise the Task<int>
    // entry-point branch in ProjectCompiler.RunAsync.
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    project.addSource(
        "Program.cs",
        `public static class Program {
            public static Task<int> Main() {
                Console.Write("task-int-ok");
                return Task.FromResult(7);
            }
        }`,
    );

    const result = await project.run();
    assert.equal(result.success, true, JSON.stringify(result));
    assert.equal(result.stdOut, "task-int-ok");
    assert.equal(result.exitCode, 7);
});
