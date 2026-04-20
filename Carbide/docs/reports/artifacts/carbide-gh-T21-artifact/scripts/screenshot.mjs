// Opens the demo in headless Chromium, waits for the REPL to be live, and snaps a PNG
// of the page. Useful for README screenshots + sanity-checking UI changes at a glance.
const playwrightUrl = new URL("../../../packages/core/node_modules/playwright/index.mjs", import.meta.url);
const { chromium } = await import(playwrightUrl.href);

const demoUrl = process.env.DEMO_URL ?? "http://127.0.0.1:34570/docs/reports/artifacts/carbide-gh-T21-artifact/";
const out = process.env.OUT ?? new URL("../screenshot.png", import.meta.url).pathname.replace(/^\//, "");

console.log(`screenshot: loading ${demoUrl}`);
const browser = await chromium.launch({ headless: true });
try {
    const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
    await page.goto(demoUrl);
    await page.waitForFunction(
        () => document.getElementById("status")?.dataset.state === "ready",
        { timeout: 180_000 },
    );
    // Wait a while for user-code output to accumulate.
    await page.waitForTimeout(10000);
    await page.screenshot({ path: out, fullPage: true });
    console.log(`screenshot: saved ${out}`);
} finally {
    await browser.close();
}
