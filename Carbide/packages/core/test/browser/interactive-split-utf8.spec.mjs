// Regression — handle-level writes must not corrupt a multi-byte UTF-8 sequence split across
// calls. `CarbideStdWriteStream` decoded each Write with a stateless `Encoding.UTF8.GetString`,
// turning a split sequence into two invalid fragments; a stateful `Decoder` holds the partial
// sequence until the rest arrives.
import { test, expect } from "@playwright/test";

test("interactive: byte-at-a-time handle writes reassemble multi-byte characters", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/interactive-split-utf8.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 90_000 });

    const payload = JSON.parse(await resultLocator.textContent());
    expect(payload.runResult.success).toBe(true);
    expect(payload.payload).toBe("café ☕ 𝄞 naïve");
    expect(payload.fullText).toContain(payload.payload);
    // Pre-fix every non-ASCII character arrived as one or more U+FFFD.
    expect(payload.fullText).not.toContain("�");

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
