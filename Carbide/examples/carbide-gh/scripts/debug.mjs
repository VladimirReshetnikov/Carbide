const playwrightUrl = new URL("../../../packages/core/node_modules/playwright/index.mjs", import.meta.url);
const { chromium } = await import(playwrightUrl.href);

const demoUrl = process.env.DEMO_URL ?? "http://127.0.0.1:34570/examples/carbide-gh/";
const browser = await chromium.launch({ headless: true });
try {
    const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
    page.on("pageerror", (e) => console.error(`[pageerror] ${e.message}\n${e.stack}`));
    page.on("console", (m) => console.log(`[${m.type()}] ${m.text()}`));
    await page.goto(demoUrl);
    await page.waitForFunction(
        () => document.getElementById("status")?.dataset.state === "ready",
        { timeout: 180_000 },
    );
    // Wait a few seconds for the REPL loop to either succeed at idle or blow up.
    await page.waitForTimeout(5000);
    const termText = await page.evaluate(() => window.__dumpBuffer?.() ?? "no buffer");
    console.log("---- terminal buffer ----");
    console.log(termText);
    console.log("---- end ----");
} finally {
    await browser.close();
}
