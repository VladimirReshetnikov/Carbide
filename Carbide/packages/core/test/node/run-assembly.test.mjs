import assert from "node:assert/strict";
import test from "node:test";
import { CarbideSession } from "../../dist/index.js";

test("runAssembly executes an emitted async Main with argv and stdin", async () => {
    const session = await CarbideSession.initializeAsync();
    try {
        const project = session.createProject({ assemblyName: "RunAssemblyProbe" });
        project.addSource("Program.cs", `
using System;
using System.Threading.Tasks;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("args=" + string.Join(",", args));
        Console.WriteLine("stdin=" + Console.In.ReadToEnd());
        await Task.Delay(1);
        Console.WriteLine("after-delay");
        return 42;
    }
}
`);
        const build = await project.build();
        assert.equal(build.success, true);
        assert.ok(build.pe);

        const run = await session.runAssembly({
            pe: build.pe,
            args: ["alpha", "beta"],
            stdin: "input-text",
        });

        assert.equal(run.success, true);
        assert.equal(run.exitCode, 42);
        assert.equal(run.stdOut, "args=alpha,beta\nstdin=input-text\nafter-delay\n");
        assert.equal(run.stdErr, "");
    } finally {
        await session.shutdown();
    }
});
