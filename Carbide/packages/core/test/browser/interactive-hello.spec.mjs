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

    // T2.1 — StreamingStdOutWriter is line-buffered: it flushes on `\n` so interactive
    // prompts reach the JS terminal before user code awaits input. A tight 10-WriteLine
    // loop therefore produces up to one chunk per line. The 4 KB / 32 ms size+time bounds
    // still coalesce mid-line writes; lines themselves are the atomic unit.
    expect(payload.chunks.length).toBeGreaterThanOrEqual(1);
    expect(payload.chunks.length).toBeLessThanOrEqual(10);

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
