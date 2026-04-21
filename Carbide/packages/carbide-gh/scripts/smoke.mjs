// Headless smoke test for the carbide-gh demo. Assumes `node scripts/serve.mjs` is
// running on its default port. Launches headless Chromium, loads the page, drives
// the REPL through `help`, sets a repo, lists live PRs via HttpClient, then exits.
// Pass-condition: final status is `exit ✓`.
const playwrightUrl = new URL("../../core/node_modules/playwright/index.mjs", import.meta.url);
const { chromium } = await import(playwrightUrl.href);

const demoUrl = process.env.DEMO_URL ?? "http://127.0.0.1:34570/packages/carbide-gh/";
const TIMEOUT_MS = Number(process.env.TIMEOUT_MS ?? 180_000);
const SKIP_NETWORK = process.env.SKIP_NETWORK === "1";

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
            return t.includes("Spectre.Console GitHub REPL") && t.includes("\u203A");
        },
        undefined,
        { timeout: TIMEOUT_MS },
    );
    console.log("smoke: REPL prompt reached");

    const sendLine = async (line, waitMs) => {
        await page.evaluate((l) => window.__term.input(l), line + "\r");
        await page.waitForTimeout(waitMs);
    };

    await sendLine("help", 800);
    const afterHelp = await page.evaluate(() => window.__dumpBuffer());
    if (!afterHelp.includes("show this panel")) {
        throw new Error("smoke: help panel not rendered");
    }
    console.log("smoke: help panel rendered");

    if (!SKIP_NETWORK) {
        await sendLine("repo anthropics/claude-code", 400);
        await sendLine("prs --state=open", 15000);
        const afterPrs = await page.evaluate(() => window.__dumpBuffer());
        if (!afterPrs.includes("pull requests in anthropics/claude-code")) {
            throw new Error("smoke: PR table not rendered (HTTP path may have failed; set SKIP_NETWORK=1 to skip)");
        }
        console.log("smoke: live PR list rendered via HttpClient");
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
