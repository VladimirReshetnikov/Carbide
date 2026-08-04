// The terminal's initial geometry must reach the program, not just later resize events.
//
// Every other interactive fixture builds its mock terminal at exactly 80x24 — which is also
// the fallback CarbideConsole reports when the real geometry never arrives. The suite could
// therefore not distinguish "delivered correctly" from "silently dropped". This fixture uses
// 120x40 and reads the size before any resize event is sent.
import { test, expect } from "@playwright/test";

test("interactive: the terminal's initial size reaches the program", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/interactive-initial-geometry.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 90_000 });

    const payload = JSON.parse(await resultLocator.textContent());
    expect(payload.runResult.success).toBe(true);

    // Both the Carbide API and the T3-forked stock Console must report the real geometry.
    expect(payload.fullText).toContain("carbide: 120x40");
    expect(payload.fullText).toContain("stock: 120x40");

    expect(pageErrors).toEqual([]);
});
