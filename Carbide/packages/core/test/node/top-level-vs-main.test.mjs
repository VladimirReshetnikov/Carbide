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
