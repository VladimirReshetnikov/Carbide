// Regression for review R1 C3 / R2 §2 — `TerminalSession.dispose()` mid-run must
// unblock a pending `Console.In.ReadLineAsync()` and resolve `exitPromise` in bounded
// time. Previously the C# `DisposeInteractive` was a no-op so exitPromise hung forever.
import { test, expect } from "@playwright/test";

test("interactive: dispose() mid-run unblocks ReadLineAsync and resolves exitPromise", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/interactive-dispose-midrun.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 90_000 });

    const payload = JSON.parse(await resultLocator.textContent());
    expect(payload.runResult.success).toBe(true);
    // The reader observed EOF → ReadLineAsync returned null → program wrote "disposed".
    expect(payload.runResult.stdOut).toContain("ready");
    expect(payload.runResult.stdOut).toContain("disposed");
    expect(payload.runResult.stdOut).not.toContain("got: ");
    // Bounded wait — the fix should resolve exitPromise within a second or two at most.
    expect(payload.disposeWaitMs).toBeLessThan(10_000);

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
