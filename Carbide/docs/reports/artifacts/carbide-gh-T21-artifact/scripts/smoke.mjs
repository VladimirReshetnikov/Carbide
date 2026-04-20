// Headless boot-sanity for the carbide-gh demo. Assumes `node scripts/serve.mjs` is
// running on its default port. Launches headless Chromium, loads the page, and waits
// until the xterm contains the "gh ›" prompt — which means the Carbide session booted,
// Spectre.Console.dll loaded, the REPL compiled, the banner rendered, and the loop is
// waiting on input. Does not exercise any GitHub API calls.
// Import Playwright from the sibling core package's node_modules to avoid making the
// demo its own installable npm package just for a smoke script.
const playwrightUrl = new URL("../../../packages/core/node_modules/playwright/index.mjs", import.meta.url);
const { chromium } = await import(playwrightUrl.href);

const demoUrl = process.env.DEMO_URL ?? "http://127.0.0.1:34570/docs/reports/artifacts/carbide-gh-T21-artifact/";
const TIMEOUT_MS = Number(process.env.TIMEOUT_MS ?? 180_000);

console.log(`smoke: loading ${demoUrl}`);
const browser = await chromium.launch({ headless: true });
try {
    const page = await browser.newPage();
    page.on("pageerror", (e) => console.error(`[page error] ${e.message}`));
    page.on("console", (m) => { if (m.type() === "error") console.error(`[browser console] ${m.text()}`); });
    await page.goto(demoUrl);

    console.log("smoke: waiting for session ready\u2026");
    await page.waitForFunction(
        () => document.getElementById("status")?.dataset.state === "ready",
        { timeout: TIMEOUT_MS },
    );
    console.log("smoke: session ready, waiting for REPL prompt in xterm\u2026");
    // The Spectre banner is a FigletText (big block-letter glyphs), so its `textContent`
    // representation is shaped whitespace. Scan for the plain-text subtitle and the
    // prompt chevron instead — both land on single rows.
    await page.waitForFunction(
        () => {
            const text = document.querySelector("#term")?.textContent ?? "";
            return text.includes("Spectre.Console GitHub REPL") && text.includes("\u203A");
        },
        { timeout: 60_000 },
    );
    console.log("smoke: PASSED");
} finally {
    await browser.close();
}
