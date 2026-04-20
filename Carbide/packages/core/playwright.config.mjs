import { defineConfig, devices } from "@playwright/test";

const PORT = Number(process.env.CARBIDE_BROWSER_PORT ?? 34567);

export default defineConfig({
    testDir: "./test/browser",
    testMatch: /.*\.spec\.mjs/,
    // T2 — bumped from 120s to 240s because the fixture count grew from 4 (pre-T1) to 11
    // (post-T2) and 6-worker parallelism puts real CPU pressure on each test's Mono-WASM
    // boot. Individual tests still complete in ~30–60s when solo; the bump absorbs
    // contention-induced slowdown without masking real regressions. The per-test assertion
    // `toHaveAttribute` timeout (90_000 in the specs) stays where it is — the test-level
    // budget is the outer envelope.
    timeout: 240_000,
    fullyParallel: false,
    // T2 — cap workers at 2. With 11 test files each booting a fresh Mono-WASM runtime
    // (~30–60s on a contended CPU), 6-worker parallelism regularly pushes per-test wall
    // time past the specs' 90s assertion timeout. 2 workers give each test enough CPU to
    // complete inside the per-assertion budget without bloating total wall time beyond
    // reason.
    workers: 2,
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
