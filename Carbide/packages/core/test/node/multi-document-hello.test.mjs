import { test } from "node:test";
import assert from "node:assert/strict";
import { CarbideSession } from "../../dist/index.js";

test("two-file hello: Program.cs uses a type declared in Helper.cs", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    project.addSource(
        "Helper.cs",
        `
        namespace MyApp;
        public static class Greeter {
            public static string Greet(string name) => $"hello, {name}";
        }
        `.trim(),
    );
    project.addSource(
        "Program.cs",
        `
        using MyApp;
        Console.WriteLine(Greeter.Greet("Vladimir"));
        `.trim(),
    );

    const diagnostics = await project.getDiagnostics();
    assert.deepEqual(
        diagnostics.filter((d) => d.severity === "error"),
        [],
        `expected zero errors, got: ${JSON.stringify(diagnostics, null, 2)}`,
    );

    const result = await project.run();
    assert.equal(result.success, true, JSON.stringify(result));
    assert.equal(result.stdOut, "hello, Vladimir\n");
});

test("error inside Helper.cs carries path='Helper.cs' through diagnostics", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    project.addSource(
        "Program.cs",
        `
        using MyApp;
        Console.WriteLine(Greeter.Greet("Vladimir"));
        `.trim(),
    );
    project.addSource(
        "Helper.cs",
        `
        namespace MyApp;
        public static class Greeter {
            // Missing return statement body — CS0161 "not all code paths return a value".
            public static string Greet(string name) {
                var x = name;
            }
        }
        `.trim(),
    );

    const diagnostics = await project.getDiagnostics();
    const errors = diagnostics.filter((d) => d.severity === "error");
    assert.ok(errors.length >= 1, `expected at least one error, got: ${JSON.stringify(diagnostics, null, 2)}`);

    const helperErrors = errors.filter((d) => d.path === "Helper.cs");
    assert.ok(
        helperErrors.length >= 1,
        `expected at least one error with path='Helper.cs'; got paths=${JSON.stringify(errors.map((d) => d.path))}`,
    );
});
