import { defineConfig, devices } from "@playwright/test";

const PORT = Number(process.env.CARBIDE_UI_BROWSER_PORT ?? 34568);

export default defineConfig({
    testDir: "./test/browser",
    testMatch: /.*\.spec\.mjs/,
    // UI-M3/UI-M4 Avalonia boot includes a full Mono-WASM runtime + Avalonia's Skia
    // native init in the iframe. Budget 4 min per test for headless Chromium on a
    // contended CI box.
    timeout: 240_000,
    fullyParallel: false,
    workers: 1,
    retries: 0,
    webServer: {
        command: "node test/browser/static-server.mjs",
        env: { PORT: String(PORT) },
        // Probe a known-resolvable asset: this launcher's own dist/index.js.
        url: `http://127.0.0.1:${PORT}/Carbide.UI/packages/launcher/dist/index.js`,
        reuseExistingServer: false,
        timeout: 15_000,
    },
    use: {
        baseURL: `http://127.0.0.1:${PORT}`,
    },
    projects: [
        {
            name: "chromium",
            use: { ...devices["Desktop Chrome"] },
        },
    ],
});
