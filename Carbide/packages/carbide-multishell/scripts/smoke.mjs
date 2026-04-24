// Headless smoke test for the legacy carbide-multishell URL. Assumes `node scripts/serve.mjs`
// is running on its default port. The old browser route should redirect to carbide-pwsh,
// which now hosts the public shared pwsh/cmd/bash session.
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

    console.log("smoke: waiting for redirect to carbide-pwsh...");
    await page.waitForFunction(
        () => {
            const t = window.__dumpBuffer?.() ?? "";
            return window.location.pathname.includes("/packages/carbide-pwsh/")
                && t.includes("carbide-pwsh")
                && t.includes("PS ");
        },
        undefined,
        { timeout: TIMEOUT_MS },
    );
    console.log("smoke: redirected pwsh REPL reached");

    const bufferLength = async () => page.evaluate(() => (window.__dumpBuffer?.() ?? "").length);

    const waitForFreshBufferText = async (expected, minLength) => {
        await page.waitForFunction(
            ({ needle, minimumLength }) => {
                const buf = window.__dumpBuffer?.() ?? "";
                if (buf.length <= minimumLength) {
                    return false;
                }
                return buf.replace(/\x1b\[[0-9;]*m/g, "").includes(needle);
            },
            { needle: expected, minimumLength: minLength },
            { timeout: TIMEOUT_MS },
        );
    };

    const sendLine = async (line, waitMs = 700) => {
        await page.locator("#term").click();
        await page.keyboard.type(line);
        await page.keyboard.press("Enter");
        await page.waitForTimeout(waitMs);
    };

    const resetPwshLine = async () => {
        await page.keyboard.press("Escape");
        await page.waitForTimeout(150);
    };

    const expectInBuffer = async (needle) => {
        const buf = await page.evaluate(() => window.__dumpBuffer());
        if (!buf.includes(needle)) {
            throw new Error(`smoke: expected '${needle}' in xterm buffer. Tail:\n${buf.split("\n").slice(-40).join("\n")}`);
        }
        console.log(`smoke: found '${needle}' OK`);
    };

    await sendLine("2 + 2", 500);
    await expectInBuffer("4");

    await sendLine("cmd", 900);
    await expectInBuffer("C:\\home\\user>");
    await sendLine("DIR /B /usr/bin", 700);
    await expectInBuffer("grep.exe");
    const beforeCmdExit = await bufferLength();
    await sendLine("exit", 900);
    await waitForFreshBufferText("PS /home/user>", beforeCmdExit);
    await page.locator("#term").click();
    await resetPwshLine();

    await sendLine("bash", 900);
    await expectInBuffer("user@carbide");
    await sendLine("echo {a,b,c}", 500);
    await expectInBuffer("a b c");
    const beforeBashExit = await bufferLength();
    await sendLine("exit", 900);
    await waitForFreshBufferText("PS /home/user>", beforeBashExit);
    await page.locator("#term").click();
    await resetPwshLine();

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
