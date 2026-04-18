// Playwright browser smoke. Launches headless Chromium via Playwright, loads hello.html
// which boots @carbide/core in-browser, and asserts the RunResult matches the Node path.
import { test, expect } from "@playwright/test";

test("hello world round-trips in headless Chromium", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/hello.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 90_000 });

    const payload = await resultLocator.textContent();
    expect(payload).toBeTruthy();
    const runResult = JSON.parse(payload);
    expect(runResult.success).toBe(true);
    expect(runResult.stdOut).toBe("hello\n");
    expect(runResult.stdErr).toBe("");
    expect(runResult.exitCode).toBe(0);

    // JS-level errors (exceptions propagated out of the module script) are fatal. Network
    // requestfailed events, on the other hand, are routine during session.shutdown()
    // (Chrome aborts in-flight asset fetches when the page unloads) and are ignored here.
    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
