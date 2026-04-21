// Systematic probe runner: accepts an HTML fixture path under /test/browser/ and prints
// its status + full run-result payload. Used for T2.1 follow-up empirical investigation.
const playwrightUrl = new URL("../../node_modules/playwright/index.mjs", import.meta.url);
const { chromium } = await import(playwrightUrl.href);

const fixture = process.argv[2];
if (!fixture) {
    console.error("usage: node fixture-probe.mjs <fixture-filename>");
    process.exit(2);
}
const url = `http://127.0.0.1:34567/test/browser/${fixture}`;
const browser = await chromium.launch({ headless: true });
try {
    const page = await browser.newPage();
    page.on("pageerror", (e) => console.error("[pageerror]", e.message));
    page.on("console", (m) => { const t = m.type(); if (t !== "debug") console.log(`[${t}]`, m.text()); });
    await page.goto(url);
    await page.waitForFunction(
        () => {
            const s = document.getElementById("result")?.dataset.status;
            return s === "ok" || s === "fail" || s === "error";
        },
        { timeout: 120_000 },
    ).catch(() => {});
    const status = await page.$eval("#result", (el) => el.dataset.status).catch(() => "timeout");
    const raw = await page.$eval("#result", (el) => el.textContent).catch(() => "<no result node>");
    console.log("===", fixture, "===");
    console.log("status:", status);
    try {
        const payload = JSON.parse(raw);
        console.log("success:", payload.runResult?.success ?? payload.result?.success ?? "?");
        console.log("stdOut:", JSON.stringify(payload.runResult?.stdOut ?? payload.result?.stdOut ?? ""));
        console.log("stdErr:", JSON.stringify(payload.runResult?.stdErr ?? payload.result?.stdErr ?? ""));
        console.log("uncaught:", payload.runResult?.uncaughtException ?? payload.result?.uncaughtException ?? "<none>");
    } catch {
        console.log("raw:", raw.substring(0, 500));
    }
} finally {
    await browser.close();
}
