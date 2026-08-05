// Regression — `session.shutdown()` mid-run must release a program parked in
// `Console.In.ReadLineAsync()`, exactly as `handle.dispose()` already did. `DisposeSession`
// only disposed the Roslyn workspace, so `exitPromise` hung forever and every abandoned
// run's TerminalInputState stayed in the registry.
import { test, expect } from "@playwright/test";

test("interactive: session.shutdown() mid-run unblocks ReadLineAsync and resolves exitPromise", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/interactive-shutdown-midrun.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 90_000 });

    const payload = JSON.parse(await resultLocator.textContent());
    expect(payload.runResult.success).toBe(true);
    // The reader observed EOF → ReadLineAsync returned null → program took the null branch.
    expect(payload.runResult.stdOut).toContain("ready");
    expect(payload.runResult.stdOut).toContain("shut-down");
    expect(payload.runResult.stdOut).not.toContain("got: ");
    expect(payload.shutdownWaitMs).toBeLessThan(10_000);

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
