// T1 — interactive terminal smoke: bytes written through `Console.OpenStandardOutput()`
// reach the active terminal's bridge via the emscripten print overlay. Pre-T1, they went
// straight to the browser devtools console (the U1 stdout-bypass footnote). The host
// adapter's `resolveRuntimeConfigOverlays()` -> `{ print, printErr }` closes the bypass.
import { test, expect } from "@playwright/test";

test("interactive: Console.OpenStandardOutput bytes reach the terminal via the print overlay",
    async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    // Capture console.log output too — if the print overlay ISN'T routing, the bytes would
    // end up here and the test would notice.
    const consoleLogs = [];
    page.on("console", (msg) => {
        if (msg.type() === "log") consoleLogs.push(msg.text());
    });

    await page.goto("/test/browser/interactive-openstdout.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 90_000 });

    const payload = JSON.parse(await resultLocator.textContent());
    expect(payload.runResult.success).toBe(true);

    // Both the Console.Write (via SetOut capture) and the OpenStandardOutput writes (via
    // the print overlay) should appear in the terminal sink. The exact interleaving can
    // vary because the two paths flush independently; assert substring presence.
    expect(payload.fullText).toContain("via-setout");
    expect(payload.fullText).toContain("via-openstdout");

    // Crucially: the OpenStandardOutput bytes should NOT have leaked to console.log. The
    // overlay's routing logic sees an attached sink and calls terminal.write, not the
    // console.log fallback.
    for (const line of consoleLogs) {
        expect(line).not.toContain("via-openstdout");
    }

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
