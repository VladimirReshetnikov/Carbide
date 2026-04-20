// T1 — interactive terminal smoke: a program that writes 10 lines via Console.WriteLine
// reaches the mock terminal's `write` sink unchanged. Exercises the streaming writer + the
// JSImport bridge end-to-end.
import { test, expect } from "@playwright/test";

test("interactive: ten Console.WriteLine lines reach terminal.write", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/interactive-hello.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 90_000 });

    const payload = JSON.parse(await resultLocator.textContent());
    expect(payload.runResult.success).toBe(true);
    expect(payload.runResult.exitCode).toBe(0);

    // The program wrote 10 lines; the concatenated terminal output should match exactly.
    const expected = Array.from({ length: 10 }, (_, i) => `line ${i}\n`).join("");
    expect(payload.fullText).toBe(expected);

    // RunResult.stdOut mirrors the terminal output (the C# tee captures raw text before
    // SGR-wrapping for stderr; stdout is never SGR-wrapped).
    expect(payload.runResult.stdOut).toBe(expected);

    // Chunks should have coalesced — we shouldn't see 10 separate `write` calls for 10 lines
    // of ~10 bytes each. The 4 KB / 32 ms default flush window makes this comfortably
    // one chunk (sometimes two if an await point lands mid-loop). Allow up to 3.
    expect(payload.chunks.length).toBeGreaterThanOrEqual(1);
    expect(payload.chunks.length).toBeLessThanOrEqual(3);

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
