// M2.7 Playwright browser smoke for multi-document. Boots Carbide in headless Chromium,
// exercises addSource / updateSource / removeSource end-to-end, and checks all three paths.
import { test, expect } from "@playwright/test";

test("multi-document add/update/remove round-trips in headless Chromium", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/multi-document.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 90_000 });

    const payload = await resultLocator.textContent();
    expect(payload).toBeTruthy();
    const result = JSON.parse(payload);

    expect(result.first.success).toBe(true);
    expect(result.first.stdOut).toBe("hello, Vladimir\n");

    expect(result.second.success).toBe(true);
    expect(result.second.stdOut).toBe("hi, Vladimir\n");

    // After removing Helper.cs, Program.cs's using of Greeter must fail to compile.
    expect(result.removeBroke).toBe(true);

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
