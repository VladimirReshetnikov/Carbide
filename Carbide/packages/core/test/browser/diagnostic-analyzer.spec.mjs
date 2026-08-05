// Diagnostic analyzers must run on the browser host, not just on Node.
//
// The Node host has a thread pool; the browser host does not. Roslyn's analyzer driver is the
// kind of machinery that historically has not survived that difference, so "it works on Node"
// is not evidence about the browser — this spec is. (Measured: the driver works under either
// `concurrentAnalysis` setting; Carbide ships `false` as the conservative default.)
import { test, expect } from "@playwright/test";

test("browser: a diagnostic analyzer runs and reports through getDiagnostics", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/diagnostic-analyzer.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 90_000 });

    const payload = JSON.parse(await resultLocator.textContent());
    const rule = payload.diagnostics.find((d) => d.id === "CARBIDETEST001");
    expect(rule, `expected CARBIDETEST001 among ${JSON.stringify(payload.diagnostics)}`).toBeTruthy();
    expect(rule.severity).toBe("warning");
    expect(rule.message).toContain("lowerCased");
    expect(rule.path).toBe("Program.cs");

    // CARBIDE_GEN002 / CARBIDE_GEN003 mean the analyzer or the driver itself threw. Those
    // degrade to a warning by design rather than failing the run, so without this assertion a
    // driver that cannot work on this runtime would look like a pass.
    expect(payload.analyzerIds).not.toContain("CARBIDE_GEN002");
    expect(payload.analyzerIds).not.toContain("CARBIDE_GEN003");

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
