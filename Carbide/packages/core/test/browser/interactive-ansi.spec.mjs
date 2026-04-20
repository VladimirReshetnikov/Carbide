// T1 — interactive terminal smoke: ANSI escape sequences pass through the bridge
// byte-for-byte. Carbide is a pipe; xterm.js (on the consumer side) is what parses SGR.
// The test asserts Carbide doesn't rewrite, escape, or filter the escape bytes on their
// way out.
import { test, expect } from "@playwright/test";

test("interactive: ANSI escape bytes reach terminal.write unchanged", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/interactive-ansi.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 90_000 });

    const payload = JSON.parse(await resultLocator.textContent());
    expect(payload.runResult.success).toBe(true);

    // ESC = \x1b. The program wrote "before " + ESC[1;33m + "hello" + ESC[0m + " after\n".
    // All bytes should be present, in order, with nothing stripped or rewritten.
    const expected = "before \x1b[1;33mhello\x1b[0m after\n";
    expect(payload.fullText).toBe(expected);
    expect(payload.runResult.stdOut).toBe(expected);

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
