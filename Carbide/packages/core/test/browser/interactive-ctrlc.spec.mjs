// T2 — Ctrl+C smoke. Delivering `\x03` fires CancelKeyPress handlers and trips the
// run's CancellationToken.
//
// TODO(T2.1): relies on `CarbideConsole.DelayAsync(ms, ct)` + the CT-tripped path, both
// of which hit the same Mono-WASM browser async scheduler regression that blocks the
// readkey and resize fixtures. Skipped pending runtime fix. The C# surface works — see
// the test/node path for API coverage.
import { test, expect } from "@playwright/test";

test.skip("interactive: Ctrl+C fires CancelKeyPress and cancels the run token", async ({ page }) => {
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
