import assert from "node:assert/strict";
import test from "node:test";
import { launchPwshBrowser } from "./pwsh-browser-harness.mjs";

test("browser pwsh: dotnet builds, emits, and runs a multi-file loose-source app", async (t) => {
    const shell = await launchPwshBrowser();
    t.after(async () => {
        await shell.close();
    });

    try {
        await shell.sendLine("mkdir /work");
        await shell.sendLine(
            "Set-Content /work/Calc.cs -Value 'using System.Linq; namespace Demo; public static class Calc { public static int Sum(string[] args) => args.Select(int.Parse).Sum(); }'",
            { entryMode: "paste" },
        );
        await shell.sendLine(
            "Set-Content /work/Program.cs -Value 'using Demo; Console.WriteLine($\"sum={Calc.Sum(args)}\"); Console.WriteLine(string.Join(\"|\", args.Select(s => s.ToUpperInvariant()))); return Calc.Sum(args) % 10;'",
            { entryMode: "paste" },
        );

        const buildMark = await shell.sendLine("dotnet build /work/Program.cs /work/Calc.cs -o /work/out", {
            timeoutMs: 180_000,
        });
        await shell.expectText("built /work/out/Program.dll", { since: buildMark });

        const testPathMark = await shell.sendLine("Test-Path /work/out/Program.dll");
        await shell.expectText("True", { since: testPathMark });

        const execMark = await shell.sendLine("dotnet /work/out/Program.dll 7 11 13", {
            timeoutMs: 180_000,
        });
        await shell.expectText("sum=31", { since: execMark });
        await shell.expectText("7|11|13", { since: execMark });

        const exitCodeMark = await shell.sendLine("$LASTEXITCODE");
        await shell.expectText("1", { since: exitCodeMark });
        shell.assertNoPageErrors();
    } catch (error) {
        const prefix = await shell.saveArtifacts("loose-source-dotnet-failure", {
            error: error.stack ?? String(error),
        });
        error.message += `\nArtifacts written with prefix ${prefix}`;
        throw error;
    }
});

test("browser pwsh: dotnet run --project uses project defaults and propagates Main exit code", async (t) => {
    const shell = await launchPwshBrowser();
    t.after(async () => {
        await shell.close();
    });

    try {
        await shell.sendLine("mkdir /project");
        await shell.sendLine(
            "Set-Content /project/PolyDemo.csproj -Value '<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AssemblyName>PolyDemo</AssemblyName></PropertyGroup></Project>'",
            { entryMode: "paste" },
        );
        await shell.sendLine(
            "Set-Content /project/Formatter.cs -Value 'public sealed class Formatter { public string Describe(string[] args) => string.Join(\";\", args.Select((x, i) => $\"{i}:{x.ToUpperInvariant()}\")); }'",
            { entryMode: "paste" },
        );
        await shell.sendLine(
            "Set-Content /project/Program.cs -Value 'using System.Security.Cryptography; using System.Text; var phrase = string.Join(\",\", args); var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(phrase)))[..12]; Console.WriteLine($\"digest={digest}\"); Console.WriteLine(new Formatter().Describe(args)); return args.Length;'",
            { entryMode: "paste" },
        );

        const runMark = await shell.sendLine("dotnet run --project /project/PolyDemo.csproj -- alpha beta gamma", {
            timeoutMs: 180_000,
        });
        await shell.expectText("digest=", { since: runMark });
        await shell.expectText("0:ALPHA;1:BETA;2:GAMMA", { since: runMark });

        const exitCodeMark = await shell.sendLine("$LASTEXITCODE");
        await shell.expectText("3", { since: exitCodeMark });

        const commandMark = await shell.sendLine("Get-Command dotnet");
        await shell.expectText("Application", { since: commandMark });
        await shell.expectText("dotnet", { since: commandMark });

        const finalBuffer = await shell.textBuffer();
        assert.match(finalBuffer, /PS \/home\/user>/);
        shell.assertNoPageErrors();
    } catch (error) {
        const prefix = await shell.saveArtifacts("project-dotnet-failure", {
            error: error.stack ?? String(error),
        });
        error.message += `\nArtifacts written with prefix ${prefix}`;
        throw error;
    }
});
