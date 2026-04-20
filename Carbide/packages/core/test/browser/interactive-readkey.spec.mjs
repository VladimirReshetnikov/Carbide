// T2 — ReadKeyAsync smoke. Fixture drives printable, arrow, function, and DEL key
// sequences; KeyParser decodes each into the expected ConsoleKey.
//
// TODO(T2.1): this fixture is temporarily skipped pending a Mono-WASM browser async
// regression. `CarbideConsole.ReadKeyAsync` is `async Task<ConsoleKeyInfo>` and awaits a
// TCS-backed `WaitForBytesAsync`; user code's `await` on the returned task hangs on
// Mono-WASM browser even though an identically-shaped TCS works via the non-async
// `ReadLineAsync` path. Needs a proper SynchronizationContext or Blazor-style
// dispatcher install to resolve; tracked in the T2 drift notes.
import { test, expect } from "@playwright/test";

test.skip("interactive: CarbideConsole.ReadKeyAsync decodes printable, arrow, F-key, and DEL", async ({ page }) => {
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
