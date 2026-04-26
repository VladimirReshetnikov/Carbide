import test from "node:test";
import { launchPwshBrowser } from "./pwsh-browser-harness.mjs";

async function withArtifacts(shell, name, body) {
    try {
        await body();
        shell.assertNoPageErrors();
    } catch (error) {
        const prefix = await shell.saveArtifacts(name, {
            error: error.stack ?? String(error),
        });
        error.message += `\nArtifacts written with prefix ${prefix}`;
        throw error;
    }
}

async function prepareDotnetProbe(shell) {
    await shell.sendLine("mkdir /nested");
    await shell.sendLine(
        "Set-Content /nested/Nested.cs -Value 'Console.WriteLine($\"nested-dotnet:{string.Join(\"|\", args)}\"); return args.Length + 10;'",
        { entryMode: "paste" },
    );

    const buildMark = await shell.sendLine("dotnet build /nested/Nested.cs -o /nested/out", {
        timeoutMs: 180_000,
    });
    await shell.expectText("built /nested/out/Nested.dll", { since: buildMark });
}

test("browser pwsh: nested cmd can run dotnet facade", async (t) => {
    const shell = await launchPwshBrowser();
    t.after(async () => {
        await shell.close();
    });

    await withArtifacts(shell, "nested-cmd-dotnet-failure", async () => {
        await prepareDotnetProbe(shell);

        const enterMark = await shell.sendLine("cmd", { waitForPrompt: false });
        await shell.expectText("C:\\home\\user>", { since: enterMark });

        const runMark = await shell.sendLine("dotnet /nested/out/Nested.dll cmd one", {
            waitForPrompt: false,
            timeoutMs: 180_000,
        });
        await shell.expectText("nested-dotnet:cmd|one", { since: runMark });
        await shell.expectText("C:\\home\\user>", { since: runMark });

        const exitMark = await shell.sendLine("exit", { waitForPrompt: false });
        await shell.waitForPrompt({ since: exitMark });
    });
});

test("browser pwsh: nested bash can run dotnet facade", async (t) => {
    const shell = await launchPwshBrowser();
    t.after(async () => {
        await shell.close();
    });

    await withArtifacts(shell, "nested-bash-dotnet-failure", async () => {
        await prepareDotnetProbe(shell);

        const enterMark = await shell.sendLine("bash", { waitForPrompt: false });
        await shell.expectText("user@carbide:/home/user$ ", { since: enterMark });

        const runMark = await shell.sendLine("dotnet /nested/out/Nested.dll bash two", {
            waitForPrompt: false,
            timeoutMs: 180_000,
        });
        await shell.expectText("nested-dotnet:bash|two", { since: runMark });
        await shell.expectText("user@carbide:/home/user$ ", { since: runMark });

        const exitMark = await shell.sendLine("exit", { waitForPrompt: false });
        await shell.waitForPrompt({ since: exitMark });
    });
});

test("browser pwsh: nested perl debugger pseudo-REPL can run dotnet facade", async (t) => {
    const shell = await launchPwshBrowser();
    t.after(async () => {
        await shell.close();
    });

    await withArtifacts(shell, "nested-perl-dotnet-failure", async () => {
        await prepareDotnetProbe(shell);

        const enterMark = await shell.sendLine("perl -de 0", { waitForPrompt: false });
        await shell.expectText("CarbidePerl", { since: enterMark });
        await shell.expectText("DB<1>", { since: enterMark });

        const runMark = await shell.sendLine(
            'system("dotnet", "/nested/out/Nested.dll", "perl", "three")',
            {
                waitForPrompt: false,
                timeoutMs: 180_000,
            },
        );
        await shell.expectText("nested-dotnet:perl|three", { since: runMark });
        await shell.expectText("DB<2>", { since: runMark });

        const exitMark = await shell.sendLine("q", { waitForPrompt: false });
        await shell.waitForPrompt({ since: exitMark });
    });
});
