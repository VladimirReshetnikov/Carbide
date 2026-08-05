// Regression — a newline-less prompt on `Console.Error` must reach the terminal before the
// program suspends on input. Only `Console.Out` was flushed before an input suspension, so
// the terminal showed nothing while the program was in fact waiting for a line, and the
// prompt appeared retroactively after the input that answered it.
import { test, expect } from "@playwright/test";

test("interactive: a newline-less stderr prompt is flushed before the program blocks on input", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/interactive-stderr-prompt-flush.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 90_000 });

    const payload = JSON.parse(await resultLocator.textContent());
    expect(payload.runResult.success).toBe(true);
    // The point of the test: visible while blocked, not after the answer arrived.
    expect(payload.promptVisibleBeforeInput).toBe(true);
    expect(payload.textBeforeInput).toContain("pw? ");
    expect(payload.textBeforeInput).not.toContain("line=");
    expect(payload.runResult.stdOut).toContain("line=[secret]");

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
