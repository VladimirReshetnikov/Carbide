// T2 — ReadLineAsync smoke. Fixture drives `onData("abc\r")`; Carbide's line editor
// echoes the typed characters + a CRLF, then delivers the committed line to C#. The
// program reads via `await CarbideConsole.ReadLineAsync()` and prints what it got.
import { test, expect } from "@playwright/test";

test("interactive: CarbideConsole.ReadLineAsync resolves with the committed line", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/interactive-readline.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 90_000 });

    const payload = JSON.parse(await resultLocator.textContent());
    expect(payload.runResult.success).toBe(true);
    expect(payload.runResult.exitCode).toBe(0);
    expect(payload.runResult.stdOut).toContain("got: [abc]");

    // The line editor should have echoed the typed characters ("abc") plus a CRLF.
    expect(payload.fullText).toContain("abc");
    expect(payload.fullText).toContain("got: [abc]");

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
