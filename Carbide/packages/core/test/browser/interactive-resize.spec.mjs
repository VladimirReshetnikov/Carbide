// T2 — resize-propagation smoke. CarbideConsole.WindowWidth/Height reflects the
// xterm's cols/rows at start, and updates on each onResize delivery. TerminalResized
// event fires per resize.
import { test, expect } from "@playwright/test";

test("interactive: CarbideConsole.Window* reflects xterm resize events", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/interactive-resize.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 90_000 });

    const payload = JSON.parse(await resultLocator.textContent());
    expect(payload.runResult.success).toBe(true);
    expect(payload.runResult.stdOut).toContain("initial: 80x24");
    expect(payload.runResult.stdOut).toContain("resized: 120x40");

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
