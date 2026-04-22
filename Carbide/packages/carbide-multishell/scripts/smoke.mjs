// Headless smoke test for the carbide-multishell demo. Assumes `node scripts/serve.mjs`
// is running on its default port. Drives the REPL through a cross-shell exercise that
// touches pwsh, cmd, and bash via the sub-REPL / stub-path mechanics.
const playwrightUrl = new URL("../../core/node_modules/playwright/index.mjs", import.meta.url);
const { chromium } = await import(playwrightUrl.href);

const demoUrl = process.env.DEMO_URL ?? "http://127.0.0.1:34572/packages/carbide-multishell/";
const TIMEOUT_MS = Number(process.env.TIMEOUT_MS ?? 180_000);

console.log(`smoke: loading ${demoUrl}`);
const browser = await chromium.launch({ headless: true });
let failed = false;
try {
    const page = await browser.newPage();
    page.setDefaultTimeout(TIMEOUT_MS);
    page.on("pageerror", (e) => { console.error(`[pageerror] ${e.message}`); failed = true; });
    page.on("console", (m) => { if (m.type() === "error") console.error(`[browser error] ${m.text()}`); });
    await page.goto(demoUrl);

    console.log("smoke: waiting for banner + pwsh prompt in xterm\u2026");
    await page.waitForFunction(
        () => {
            const t = window.__dumpBuffer?.() ?? "";
            return t.includes("Carbide Multishell") && t.includes("PS ");
        },
        undefined,
        { timeout: TIMEOUT_MS },
    );
    console.log("smoke: initial pwsh REPL reached");

    const sendLine = async (line, waitMs) => {
        await page.evaluate((l) => window.__term.input(l), line + "\r");
        await page.waitForTimeout(waitMs);
    };

    const expectInBuffer = async (needle) => {
        const buf = await page.evaluate(() => window.__dumpBuffer());
        if (!buf.includes(needle)) {
            throw new Error(`smoke: expected '${needle}' in xterm buffer. Tail:\n${buf.split('\n').slice(-40).join('\n')}`);
        }
        console.log(`smoke: found '${needle}' OK`);
    };

    // Start in pwsh. A trivial check.
    await sendLine("2 + 2", 500);
    await expectInBuffer("4");

    // Enter bash via bare invocation. Prompt should change.
    await sendLine("bash", 800);
    await expectInBuffer("user@carbide");

    // Run something bash-specific: brace expansion.
    await sendLine("echo {a,b,c}", 500);
    await expectInBuffer("a b c");

    // Set an env var in bash; leave bash; read it from pwsh.
    await sendLine("export FROM_BASH=yes", 400);
    await sendLine("exit", 800);
    await expectInBuffer("PS ");
    await sendLine("$env:FROM_BASH", 500);
    await expectInBuffer("yes");

    // Enter cmd, list the stub executables.
    await sendLine("cmd", 800);
    await expectInBuffer("C:\\");
    await sendLine("DIR /B /usr/bin", 600);
    await expectInBuffer("bash");
    await expectInBuffer("cmd.exe");

    // Invoke a stub by path.
    await sendLine("/usr/bin/bash", 800);
    await expectInBuffer("user@carbide");
    await sendLine("echo nested", 500);
    await expectInBuffer("nested");
    await sendLine("exit", 500); // leave nested bash
    await sendLine("exit", 500); // leave cmd

    // Back in pwsh.
    await expectInBuffer("PS ");

    // Clean session exit.
    await sendLine("exit", 2000);
    await page.waitForFunction(
        () => {
            const s = document.getElementById("status");
            return s && s.dataset.state === "ready" && s.textContent.startsWith("exit \u2713");
        },
        undefined,
        { timeout: 10_000 },
    );
    console.log("smoke: clean exit");
    console.log("smoke: PASSED");
} catch (e) {
    console.error("smoke: FAILED:", e.message);
    failed = true;
} finally {
    await browser.close();
}
process.exit(failed ? 1 : 0);
