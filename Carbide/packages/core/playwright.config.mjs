import { defineConfig, devices } from "@playwright/test";

const PORT = Number(process.env.CARBIDE_BROWSER_PORT ?? 34567);

export default defineConfig({
    testDir: "./test/browser",
    testMatch: /.*\.spec\.mjs/,
    timeout: 120_000,
    fullyParallel: false,
    retries: 0,
    webServer: {
        command: `node test/browser/static-server.mjs`,
        env: { PORT: String(PORT) },
        url: `http://127.0.0.1:${PORT}/dist/index.js`,
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
