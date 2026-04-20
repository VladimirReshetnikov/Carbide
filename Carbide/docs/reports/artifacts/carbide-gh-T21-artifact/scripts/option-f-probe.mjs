// Option F probe: run the `interactive-await-suspend-probe.html` fixture headlessly
// against the test server and print both the JSON payload and the captured terminal
// text. Assumes the core package's static server is running on its default port 34567.
const playwrightUrl = new URL("../../../../../packages/core/node_modules/playwright/index.mjs", import.meta.url);
const { chromium } = await import(playwrightUrl.href);

const url = process.env.PROBE_URL ?? "http://127.0.0.1:34567/test/browser/await-suspend-noninteractive-probe.html";

const browser = await chromium.launch({ headless: true });
try {
    const page = await browser.newPage();
    page.on("pageerror", (e) => console.error("[pageerror]", e.message));
    page.on("console", (m) => { const t = m.type(); if (t === "error" || t === "log" || t === "warning") console.log(`[${t}]`, m.text()); });
    await page.goto(url);
    await page.waitForFunction(
        () => {
            const s = document.getElementById("result")?.dataset.status;
            return s === "ok" || s === "fail" || s === "error";
        },
        { timeout: 180_000 },
    );
    const status = await page.$eval("#result", (el) => el.dataset.status);
    const raw = await page.$eval("#result", (el) => el.textContent);
    console.log("status:", status);
    try {
        const payload = JSON.parse(raw);
        console.log("--- run result ---");
        console.log(JSON.stringify(payload.result, null, 2));
        console.log("--- terminal text ---");
        console.log(payload.text);
    } catch {
        console.log("--- raw (not JSON) ---");
        console.log(raw);
    }
} finally {
    await browser.close();
}
