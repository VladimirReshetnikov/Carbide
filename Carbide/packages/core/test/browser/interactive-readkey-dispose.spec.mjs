// Regression — disposing a run parked in `CarbideConsole.ReadKeyAsync()` must end the read
// instead of spinning. The key-mode wait completes immediately once the reader is closed, and
// with no EOF check the loop spun; on single-threaded Mono-WASM browser that starves the JS
// event loop, so the tab freezes and `await exitPromise` can never resolve.
import { test, expect } from "@playwright/test";

test("interactive: dispose() mid-run releases a pending ReadKeyAsync without freezing the page", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/interactive-readkey-dispose.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 90_000 });

    const payload = JSON.parse(await resultLocator.textContent());
    expect(payload.runResult.success).toBe(true);
    expect(payload.runResult.stdOut).toContain("ready");
    expect(payload.runResult.stdOut).toContain("released");
    expect(payload.runResult.stdOut).not.toContain("got: ");
    expect(payload.disposeWaitMs).toBeLessThan(10_000);
    // The page is still alive after the teardown: a second program compiled and ran. A
    // spinning C# loop pins the only thread, so neither this nor the assertions above would
    // ever be reached — the spec would time out instead.
    expect(payload.runResult2.success).toBe(true);
    expect(payload.fullText2).toContain("next-run-ok");

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
