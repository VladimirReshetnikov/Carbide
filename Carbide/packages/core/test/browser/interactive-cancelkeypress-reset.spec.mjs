// Regression — a `Console.CancelKeyPress` handler registered by one run must not survive into
// the next. The forked System.Console holds the chain in a static field that outlives the
// run's collectible ALC, so an earlier run's `e.Cancel = true` handler silently vetoed Ctrl+C
// for every later run on the page.
import { test, expect } from "@playwright/test";

test("interactive: a previous run's CancelKeyPress handler does not veto Ctrl+C in the next run", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/interactive-cancelkeypress-reset.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 120_000 });

    const payload = JSON.parse(await resultLocator.textContent());
    expect(payload.resultA.success).toBe(true);
    expect(payload.resultA.stdOut).toContain("a-registered");

    expect(payload.resultB.success).toBe(true);
    expect(payload.resultB.stdOut).toContain("ready");
    // The whole point: run B's token tripped. Pre-fix this read `cancelled=False`, because
    // run A's handler was still on the chain vetoing the trip.
    expect(payload.resultB.stdOut).toContain("cancelled=True");
    // And it tripped promptly rather than the program simply outliving its 5s delay.
    expect(payload.ctrlCWaitMs).toBeLessThan(4_000);

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
