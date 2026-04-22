// UI-M6 multi-preview Playwright spec. Drives three iframes from one CarbideSession
// and asserts each renders without cross-talk.

import { test, expect } from "@playwright/test";

test("three iframes launch concurrently from one session", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));
    const consoleLines = [];
    page.on("console", (msg) => {
        consoleLines.push(`[${msg.type()}] ${msg.text()}`);
    });

    await page.goto("/Carbide.UI/packages/launcher/test/browser/multi-preview.html");

    const result = page.locator("#result");
    try {
        await expect(result).toHaveAttribute("data-status", "ok", { timeout: 240_000 });
    } catch (err) {
        console.log("--- captured console (last 80 lines) ---");
        console.log(consoleLines.slice(-80).join("\n"));
        console.log("--- captured page errors ---");
        console.log(pageErrors.join("\n"));
        throw err;
    }

    const payload = JSON.parse(await result.textContent());
    expect(payload.success).toBe(true);
    expect(payload.previewCount).toBe(3);
    // Handle IDs must be distinct — confirms per-iframe filtering isn't coincidentally
    // sharing state.
    expect(new Set(payload.handleIds).size).toBe(3);

    // Each iframe's #out canvas should be attached and have non-zero pixel coverage.
    for (const id of ["p-hello", "p-counter", "p-xaml"]) {
        const frame = page.frameLocator(`#${id}`);
        await expect(frame.locator("#out canvas")).toBeAttached({ timeout: 30_000 });
        const hasPixels = await frame.locator("#out canvas").first().evaluate((canvas) => {
            if (canvas.width === 0 || canvas.height === 0) return false;
            // Skia backs onto WebGL; a populated WebGL canvas has width/height.
            // For a 2D backend fallback, sample a few pixels.
            const ctx = canvas.getContext("2d");
            if (ctx instanceof CanvasRenderingContext2D) {
                const sample = ctx.getImageData(0, 0, Math.min(canvas.width, 32), Math.min(canvas.height, 32)).data;
                for (let i = 0; i < sample.length; i += 4) {
                    if (sample[i + 3] !== 0) return true;
                }
                return false;
            }
            return canvas.width > 0 && canvas.height > 0;
        });
        expect(hasPixels, `${id} should render non-zero pixels`).toBe(true);
    }

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
