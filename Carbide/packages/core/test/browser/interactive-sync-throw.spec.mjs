// T2 — synchronous Console.In.ReadLine throws a pointed NotSupportedException instead
// of deadlocking the Mono-WASM main thread. The error message must point the user at
// the Async variants.
import { test, expect } from "@playwright/test";

test("interactive: synchronous Console.In.ReadLine throws with a pointed message", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/interactive-sync-throw.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 90_000 });

    const payload = JSON.parse(await resultLocator.textContent());
    expect(payload.runResult.success).toBe(true);
    expect(payload.runResult.stdOut).toContain("threw:");
    expect(payload.runResult.stdOut).toContain("deadlock");
    expect(payload.runResult.stdOut).toContain("ReadLineAsync");

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
