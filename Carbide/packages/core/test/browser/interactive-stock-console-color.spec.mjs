// T3 — confirm that a program using ONLY stock `System.Console.*` (no CarbideConsole)
// emits the same ANSI sequences as its CarbideConsole twin (`interactive-color.spec.mjs`).
// This is the smoke that fails loudly if the forked System.Console.dll is not shipped in
// `_framework/` or the stock one re-overlays it.
import { test, expect } from "@playwright/test";

test("interactive: stock Console.* emits ANSI via the T3 forked System.Console.dll", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/interactive-stock-console-color.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 90_000 });

    const payload = JSON.parse(await resultLocator.textContent());
    expect(payload.runResult.success).toBe(true);
    const text = payload.fullText;

    // Every assertion mirrors interactive-color.spec.mjs, proving the stock API path
    // produces identical wire-level output to the CarbideConsole API path.
    expect(text).toContain("\x1b[91mR");
    expect(text).toContain("\x1b[44mB");
    expect(text).toContain("\x1b[39;49m_");
    expect(text).toContain("\x1b[3;6H");
    expect(text).toContain("\x1b[?25l");
    expect(text).toContain("\x1b]0;t3\x07");
    expect(text).toContain("\x1b[2J\x1b[H");

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
