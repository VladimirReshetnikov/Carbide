// Headless smoke test for the carbide-pwsh demo. Assumes `node scripts/serve.mjs` is
// running on its default port. Launches headless Chromium, loads the page, drives the
// REPL through a curated set of Phase 1 expressions, and asserts that each produces the
// expected output in the xterm buffer. Pass-condition: final status is `exit ✓`.
const playwrightUrl = new URL("../../core/node_modules/playwright/index.mjs", import.meta.url);
const { chromium } = await import(playwrightUrl.href);

const demoUrl = process.env.DEMO_URL ?? "http://127.0.0.1:34571/packages/carbide-pwsh/";
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

    console.log("smoke: waiting for banner + prompt in xterm\u2026");
    await page.waitForFunction(
        () => {
            const t = window.__dumpBuffer?.() ?? "";
            return t.includes("carbide-pwsh") && t.includes("PS >");
        },
        undefined,
        { timeout: TIMEOUT_MS },
    );
    console.log("smoke: REPL prompt reached");

    const sendLine = async (line, waitMs) => {
        await page.evaluate((l) => window.__term.input(l), line + "\r");
        await page.waitForTimeout(waitMs);
    };

    const cases = [
        { input: "2 + 2", expect: "4" },
        { input: "$x = 5", expect: null /* assignment produces no output */ },
        { input: "$x + 2", expect: "7" },
        { input: "\"hello, $x\"", expect: "hello, 5" },
        { input: "[System.Math]::Sqrt(2)", expect: "1.4142135623730951" },
        { input: "@(1,2,3).Length", expect: "3" },
        { input: "1 -eq 1", expect: "True" },
    ];

    for (const c of cases) {
        await sendLine(c.input, 600);
        if (c.expect != null) {
            const buf = await page.evaluate(() => window.__dumpBuffer());
            if (!buf.includes(c.expect)) {
                throw new Error(`smoke: input '${c.input}' did not produce expected output '${c.expect}'. Buffer:\n${buf}`);
            }
            console.log(`smoke: '${c.input}' -> '${c.expect}' OK`);
        } else {
            console.log(`smoke: '${c.input}' OK (no output expected)`);
        }
    }

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
