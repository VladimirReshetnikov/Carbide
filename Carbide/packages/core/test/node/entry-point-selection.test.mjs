// Which method Carbide actually invokes as the entry point.
//
// T2.1 added a substitution: when the CLR's entry point is Roslyn's *synthesised* wrapper
// for `async Task Main` / top-level-statements-with-await, invoking that wrapper deadlocks on
// single-threaded Mono-WASM, so Carbide finds the underlying async method and awaits it in
// its own frame instead.
//
// The search originally ran for EVERY non-awaitable entry point, including a user's genuine
// `static void Main`. A single unrelated helper in the same class — `static async Task
// WarmUpAsync()` — was then invoked *instead of Main*, with success: true, empty stdout, and
// no diagnostic. `carbide build` emitted a PE whose entry point was Main while `carbide run`
// on the same sources ran something else.

import { test } from "node:test";
import assert from "node:assert/strict";
import { CarbideSession } from "../../dist/index.js";

async function runSource(t, source) {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });
    const project = session.createProject();
    project.addSource("Program.cs", source);
    const result = await project.run();
    assert.equal(
        result.success,
        true,
        `expected success; errors: ${JSON.stringify(result.diagnostics.filter((d) => d.severity === "error"))}`,
    );
    return result;
}

test("a user-declared sync Main runs, even beside an awaitable sibling", async (t) => {
    const result = await runSource(t, [
        "using System.Threading.Tasks;",
        "public class Program {",
        '    public static void Main() { System.Console.WriteLine("main-ok"); }',
        '    public static async Task WarmUpAsync() { await Task.Yield(); System.Console.WriteLine("WARMUP-RAN"); }',
        "}",
    ].join("\n"));
    assert.equal(result.stdOut, "main-ok\n", "the sibling must not be invoked in place of Main");
});

test("a sync int Main keeps its own exit code beside an awaitable sibling", async (t) => {
    const result = await runSource(t, [
        "using System.Threading.Tasks;",
        "public class Program {",
        '    public static int Main() { System.Console.WriteLine("main-ok"); return 7; }',
        "    public static async Task<int> ComputeAsync() { await Task.Yield(); return 42; }",
        "}",
    ].join("\n"));
    assert.equal(result.stdOut, "main-ok\n");
    assert.equal(result.exitCode, 7, "the sibling's return value must not become the exit code");
});

test("a private awaitable sibling is also not substituted", async (t) => {
    // The reflection search includes NonPublic, so accessibility is no protection.
    const result = await runSource(t, [
        "using System.Threading.Tasks;",
        "public class Program {",
        '    public static void Main() { System.Console.WriteLine("main-ok"); }',
        '    private static async Task HelperAsync() { await Task.Yield(); System.Console.WriteLine("HELPER-RAN"); }',
        "}",
    ].join("\n"));
    assert.equal(result.stdOut, "main-ok\n");
});

test("several awaitable siblings still do not displace Main", async (t) => {
    const result = await runSource(t, [
        "using System.Threading.Tasks;",
        "public class Program {",
        '    public static void Main() { System.Console.WriteLine("main-ok"); }',
        "    public static async Task AAsync() { await Task.Yield(); }",
        "    public static async Task BAsync() { await Task.Yield(); }",
        "}",
    ].join("\n"));
    assert.equal(result.stdOut, "main-ok\n");
});

test("async Task Main still works — the case the substitution exists for", async (t) => {
    const result = await runSource(t, [
        "using System.Threading.Tasks;",
        "public class Program {",
        '    public static async Task Main() { await Task.Yield(); System.Console.WriteLine("async-main-ok"); }',
        "}",
    ].join("\n"));
    assert.equal(result.stdOut, "async-main-ok\n");
});

test("async Task<int> Main still carries its exit code", async (t) => {
    const result = await runSource(t, [
        "using System.Threading.Tasks;",
        "public class Program {",
        "    public static async Task<int> Main() { await Task.Yield(); return 3; }",
        "}",
    ].join("\n"));
    assert.equal(result.exitCode, 3);
});

test("top-level statements with await still work", async (t) => {
    const result = await runSource(t, [
        "using System.Threading.Tasks;",
        "await Task.Yield();",
        'System.Console.WriteLine("tls-ok");',
    ].join("\n"));
    assert.equal(result.stdOut, "tls-ok\n");
});

test("the sync Main a program actually declares is the one build() emits", async (t) => {
    // The defect made `build` and `run` disagree about the same sources; assert they agree.
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });
    const project = session.createProject();
    project.addSource(
        "Program.cs",
        [
            "using System.Threading.Tasks;",
            "public class Program {",
            '    public static void Main() { System.Console.WriteLine("main-ok"); }',
            "    public static async Task NoiseAsync() { await Task.Yield(); }",
            "}",
        ].join("\n"),
    );

    const build = await project.build();
    assert.equal(build.success, true);
    const ran = await session.runAssembly({ pe: build.pe });
    assert.equal(ran.stdOut, "main-ok\n", "the emitted PE must run the same method project.run() does");
});
