// T3.1 — Console.Beep(freq, duration) and CarbideConsole.BeepAsync drive the Web Audio
// bridge installed by `bridge.ts`. The fixture wraps AudioContext so the spec can assert
// on Oscillator start/stop events without depending on headless Chromium actually emitting
// sound — autoplay policy + CI-muted audio stacks make "did we hear it?" unreliable.
import { test, expect } from "@playwright/test";

test("interactive: Console.Beep + BeepAsync drive Web Audio oscillators", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/interactive-beep.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 90_000 });

    const payload = JSON.parse(await resultLocator.textContent());
    expect(payload.runResult.success).toBe(true);
    expect(payload.runResult.exitCode).toBe(0);
    expect(payload.runResult.stdOut).toContain("beep: starting");
    expect(payload.runResult.stdOut).toContain("beep: done");

    // Exactly three oscillator start events — two from Console.Beep and one from
    // CarbideConsole.BeepAsync — at the requested frequencies, in order.
    const starts = payload.audioEvents.filter((e) => e.kind === "start");
    expect(starts).toHaveLength(3);
    expect(starts[0].freq).toBe(440);
    expect(starts[1].freq).toBe(880);
    expect(starts[2].freq).toBe(1320);
    for (const s of starts) {
        expect(s.type).toBe("sine");
    }

    // Queue invariant: each oscillator starts at or after the previous one's stop time,
    // so tones don't overlap even though the two fire-and-forget `Console.Beep` calls
    // returned immediately.
    const stops = payload.audioEvents.filter((e) => e.kind === "stop");
    expect(stops).toHaveLength(3);
    for (let i = 1; i < starts.length; i++) {
        expect(starts[i].when).toBeGreaterThanOrEqual(stops[i - 1].when - 1e-6);
    }

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
