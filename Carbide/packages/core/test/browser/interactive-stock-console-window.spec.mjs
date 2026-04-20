// T3 — confirm that stock `Console.WindowWidth` / `Console.WindowHeight` on the forked
// System.Console.dll return the xterm instance's current geometry (via the T3 bridge's
// `getCols`/`getRows` JSImports). A fallback to Mono-WASM's default 80×24 — which stock
// behavior returns — would show up as a mismatch since the fixture primes a 120×40
// terminal.
import { test, expect } from "@playwright/test";

test("interactive: stock Console.WindowWidth/Height reflects xterm geometry", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/interactive-stock-console-window.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 90_000 });

    const payload = JSON.parse(await resultLocator.textContent());
    expect(payload.runResult.success).toBe(true);
    expect(payload.fullText).toBe("w=120 h=40");

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
