// T2 — ReadKeyAsync smoke. Fixture drives printable, arrow, function, and DEL key
// sequences; KeyParser decodes each into the expected ConsoleKey.
import { test, expect } from "@playwright/test";

test("interactive: CarbideConsole.ReadKeyAsync decodes printable, arrow, F-key, and DEL", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/interactive-readkey.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 90_000 });

    const payload = JSON.parse(await resultLocator.textContent());
    expect(payload.runResult.success).toBe(true);
    expect(payload.runResult.stdOut).toContain("k1=A k2=UpArrow k3=F1 k4=Backspace");

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
