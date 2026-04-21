// Drive the carbide-gh REPL through the `beep` and `fanfare` commands and assert that
// Web Audio oscillators were scheduled with the expected frequencies. Proves that the
// Console.Beep / CarbideConsole.BeepAsync bridge works end-to-end inside a real
// consumer program (Spectre.Console REPL + user-code await + Web Audio).
const playwrightUrl = new URL("../../core/node_modules/playwright/index.mjs", import.meta.url);
const { chromium } = await import(playwrightUrl.href);

const demoUrl = process.env.DEMO_URL ?? "http://127.0.0.1:34570/packages/carbide-gh/";

const browser = await chromium.launch({ headless: true });
let failed = false;
try {
    const page = await browser.newPage();
    page.setDefaultTimeout(180_000);
    page.on("pageerror", (e) => { console.error(`[pageerror] ${e.message}`); failed = true; });

    // Install an AudioContext spy BEFORE the page script loads so the bridge picks up
    // the wrapped constructor when it first calls `new AudioContext()`.
    await page.addInitScript(() => {
        window.__audioEvents = [];
        const Native = window.AudioContext ?? window.webkitAudioContext;
        if (!Native) return;
        class SpyCtx extends Native {
            createOscillator() {
                const osc = super.createOscillator();
                const origStart = osc.start.bind(osc);
                const origStop = osc.stop.bind(osc);
                osc.start = (when) => {
                    window.__audioEvents.push({
                        kind: "start",
                        when: when ?? this.currentTime,
                        freq: osc.frequency.value,
                        type: osc.type,
                    });
                    return origStart(when);
                };
                osc.stop = (when) => {
                    window.__audioEvents.push({ kind: "stop", when: when ?? this.currentTime });
                    return origStop(when);
                };
                return osc;
            }
        }
        window.AudioContext = SpyCtx;
    });

    await page.goto(demoUrl);
    await page.waitForFunction(
        () => {
            const t = window.__dumpBuffer?.() ?? "";
            return t.includes("Spectre.Console GitHub REPL") && t.includes("\u203A");
        },
        undefined,
        { timeout: 60_000 },
    );
    console.log("drive: boot complete, REPL at prompt");

    const send = async (line, waitMs) => {
        await page.evaluate((l) => window.__term.input(l), line + "\r");
        await page.waitForTimeout(waitMs);
    };

    // beep 660 120 -> one oscillator at 660 Hz
    await send("beep 660 120", 600);
    // fanfare -> four oscillators: 523, 659, 784, 1047
    await send("fanfare", 1400);

    const events = await page.evaluate(() => window.__audioEvents ?? []);
    const starts = events.filter((e) => e.kind === "start");
    console.log(`drive: observed ${starts.length} oscillator starts`);

    const expectedFreqs = [660, 523, 659, 784, 1047];
    if (starts.length !== expectedFreqs.length) {
        throw new Error(`expected ${expectedFreqs.length} tones, got ${starts.length}: ${JSON.stringify(starts.map((s) => s.freq))}`);
    }
    for (let i = 0; i < expectedFreqs.length; i++) {
        if (starts[i].freq !== expectedFreqs[i]) {
            throw new Error(`expected start[${i}].freq == ${expectedFreqs[i]}, got ${starts[i].freq}`);
        }
    }
    console.log(`drive: frequencies match: ${expectedFreqs.join(", ")}`);

    await send("exit", 2000);
    await page.waitForFunction(
        () => document.getElementById("status")?.textContent?.startsWith("exit \u2713"),
        undefined,
        { timeout: 10_000 },
    );
    console.log("drive: clean exit");
    console.log("drive: PASSED");
} catch (e) {
    console.error("drive: FAILED:", e.message);
    failed = true;
} finally {
    await browser.close();
}
process.exit(failed ? 1 : 0);
