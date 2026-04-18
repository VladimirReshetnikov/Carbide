// M3.9 Playwright browser smoke for user-supplied DLL injection.
// Fetches MyHelper.dll bytes through the static server, adds + attaches + runs, then removes
// the reference and verifies the next compilation fails.
import { test, expect } from "@playwright/test";

test("user DLL round-trips in headless Chromium; remove invalidates", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/user-reference.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 90_000 });

    const payload = await resultLocator.textContent();
    expect(payload).toBeTruthy();
    const out = JSON.parse(payload);

    expect(out.firstRun.success).toBe(true);
    expect(out.firstRun.stdOut).toBe("Thing<42>\n");
    expect(out.removeBroke).toBe(true);
    expect(out.disposed).toBe(true);
    expect(typeof out.handleId).toBe("string");
    expect(out.handleId.length).toBe(32);

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
