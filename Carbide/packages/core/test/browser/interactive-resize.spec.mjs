// T2 — resize-propagation smoke. CarbideConsole.WindowWidth/Height reflects the
// xterm's cols/rows at start, and updates on each onResize delivery. TerminalResized
// event fires per resize.
//
// TODO(T2.1): awaiting a TCS completed from a JSExport-triggered event handler currently
// throws `PlatformNotSupportedException: Cannot wait on monitors on this runtime` on
// Mono-WASM browser without a proper SynchronizationContext/Dispatcher. The initial
// WindowWidth/Height priming (via the session's boot-time NotifyResize) works; the
// `WaitForResizeAsync` follow-up does not. Skipped pending runtime fix.
import { test, expect } from "@playwright/test";

test.skip("interactive: CarbideConsole.Window* reflects xterm resize events", async ({ page }) => {
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
