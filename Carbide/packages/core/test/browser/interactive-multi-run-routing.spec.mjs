// Regression — every interactive run must write into its OWN terminal. Mono-WASM caches the
// function object a JSImport resolved to, so publishing a fresh `Carbide.Terminal.write` per
// run left the C# side calling the first run's closure forever: a browser IDE showed a
// working first tab and permanently silent ones after it. `RunResult.stdOut` stayed correct
// throughout (the C# side tees separately), so the routing assertions below are the only
// thing that can catch it.
import { test, expect } from "@playwright/test";

test("interactive: three sequential runs each write to their own terminal", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/interactive-multi-run-routing.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 120_000 });

    const payload = JSON.parse(await resultLocator.textContent());
    expect(payload.labels).toEqual(["alpha", "bravo", "charlie"]);

    // The tee is not the thing under test, but if it were wrong the routing assertions
    // below would be meaningless.
    for (let i = 0; i < payload.labels.length; i++) {
        expect(payload.stdOuts[i]).toContain(payload.labels[i]);
    }

    // Each terminal saw its own run's line and nothing else. Pre-fix, terminalTexts[0]
    // contained all three labels and [1] and [2] were empty strings.
    for (let i = 0; i < payload.labels.length; i++) {
        expect(payload.terminalTexts[i]).toContain(payload.labels[i]);
        for (let j = 0; j < payload.labels.length; j++) {
            if (i === j) continue;
            expect(payload.terminalTexts[i]).not.toContain(payload.labels[j]);
        }
    }

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
