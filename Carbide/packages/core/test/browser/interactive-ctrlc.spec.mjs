// T2 — Ctrl+C smoke. Delivering `\x03` fires CancelKeyPress handlers and trips the
// run's CancellationToken.
import { test, expect } from "@playwright/test";

test("interactive: Ctrl+C fires CancelKeyPress and cancels the run token", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/interactive-ctrlc.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 90_000 });

    const payload = JSON.parse(await resultLocator.textContent());
    expect(payload.runResult.success).toBe(true);
    // CancelKeyPress handler must have fired; RunCancellationToken must have tripped.
    expect(payload.runResult.stdOut).toContain("done handled=True cancelled=True");

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
