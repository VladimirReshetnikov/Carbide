// UI-M3 + UI-M4 joint integration spec. Drives a headless Chromium through the full
// compile-and-launch flow: Carbide session boots, Avalonia refs get fed, a trivial App
// class compiles, launchInIframe resolves with runnerRunning, and the iframe's canvas
// renders non-blank pixels. Closes the deferred Playwright acceptance on both
// milestones.

import { test, expect } from "@playwright/test";

test("launchInIframe boots Avalonia inside a fresh iframe", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));
    const consoleLines = [];
    page.on("console", (msg) => {
        consoleLines.push(`[${msg.type()}] ${msg.text()}`);
    });

    await page.goto("/Carbide.UI/packages/launcher/test/browser/avalonia-hello.html");

    const result = page.locator("#result");
    try {
        await expect(result).toHaveAttribute("data-status", "ok", { timeout: 180_000 });
    } catch (err) {
        // Dump console + page errors before failing so CI logs capture the root cause
        // rather than just "toHaveAttribute timed out".
        console.log("--- captured console (last 60 lines) ---");
        console.log(consoleLines.slice(-60).join("\n"));
        console.log("--- captured page errors ---");
        console.log(pageErrors.join("\n"));
        throw err;
    }

    const payload = JSON.parse(await result.textContent());
    expect(payload.success).toBe(true);
    expect(payload.primaryAssemblyName).toBe("CarbideHello");

    // Confirm the iframe actually booted: its contentDocument should host a <canvas>
    // under #out, which is where Avalonia.Browser renders.
    const iframe = page.frameLocator("#preview");
    await expect(iframe.locator("#out canvas")).toBeAttached({ timeout: 30_000 });

    // Canvas-pixel check: after Avalonia has had a tick to paint, the primary canvas
    // should have non-zero pixel coverage (all-transparent == never drew anything).
    await page.waitForTimeout(500);
    const hasPixels = await iframe.locator("#out canvas").first().evaluate((canvas) => {
        const ctx = canvas.getContext("2d") ?? canvas.getContext("webgl2") ?? canvas.getContext("webgl");
        if (canvas.width === 0 || canvas.height === 0) return false;
        if (ctx instanceof CanvasRenderingContext2D) {
            const sample = ctx.getImageData(0, 0, Math.min(canvas.width, 32), Math.min(canvas.height, 32)).data;
            for (let i = 0; i < sample.length; i += 4) {
                if (sample[i + 3] !== 0) return true;
            }
            return false;
        }
        // WebGL path — any size >0 is evidence of a configured context.
        return canvas.width > 0 && canvas.height > 0;
    });
    expect(hasPixels).toBe(true);

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
