// M4.9 Playwright browser smoke for build() round-trip: in-browser Carbide builds MyLib
// into PE bytes, feeds them back through session.addReference, runs a program against the
// emitted reference. Asserts stdout and that PE/PDB are non-empty.
import { test, expect } from "@playwright/test";

test("project.build() round-trips through session.addReference in headless Chromium", async ({ page }) => {
    const pageErrors = [];
    page.on("pageerror", (e) => pageErrors.push(e.message));

    await page.goto("/test/browser/build-roundtrip.html");

    const resultLocator = page.locator("#result");
    await expect(resultLocator).toHaveAttribute("data-status", "ok", { timeout: 120_000 });

    const payload = await resultLocator.textContent();
    expect(payload).toBeTruthy();
    const out = JSON.parse(payload);

    expect(out.libSuccess).toBe(true);
    expect(out.libPeLen).toBeGreaterThan(0);
    expect(out.libPdbLen).toBeGreaterThan(0);
    expect(out.runSuccess).toBe(true);
    expect(out.stdOut).toBe("Thing<42>\n");

    if (pageErrors.length) {
        throw new Error(`page reported JS errors:\n${pageErrors.join("\n")}`);
    }
});
