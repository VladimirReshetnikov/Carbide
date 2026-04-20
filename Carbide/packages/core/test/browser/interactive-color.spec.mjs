// T2 — SGR/cursor/title/clear smoke. Every member of CarbideConsole that emits ANSI
// should surface its escape sequence in the terminal output unchanged.
import { test, expect } from "@playwright/test";

test("interactive: CarbideConsole.* emits the expected ANSI sequences", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/interactive-color.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 90_000 });

    const payload = JSON.parse(await resultLocator.textContent());
    expect(payload.runResult.success).toBe(true);
    const text = payload.fullText;

    // Foreground Red → \x1b[91m followed by "R".
    expect(text).toContain("\x1b[91mR");
    // Background DarkBlue → \x1b[44m followed by "B".
    expect(text).toContain("\x1b[44mB");
    // Reset → \x1b[39;49m followed by "_".
    expect(text).toContain("\x1b[39;49m_");
    // Cursor position (0-based left=5, top=2) → CUP with 1-based (3;6).
    expect(text).toContain("\x1b[3;6H");
    // Cursor hide.
    expect(text).toContain("\x1b[?25l");
    // Title OSC 0.
    expect(text).toContain("\x1b]0;t2\x07");
    // Clear: ED + CUP home.
    expect(text).toContain("\x1b[2J\x1b[H");

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
