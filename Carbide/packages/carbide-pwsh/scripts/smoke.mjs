// Headless smoke test for the carbide-pwsh demo. Assumes `node scripts/serve.mjs` is
// running on its default port. Drives the REPL through a curated set of Phase 1+2+3
// expressions, asserts xterm buffer output, and runs the Phase 3 aggregate exit-gate.
const playwrightUrl = new URL("../../core/node_modules/playwright/index.mjs", import.meta.url);
const { chromium } = await import(playwrightUrl.href);

const demoUrl = process.env.DEMO_URL ?? "http://127.0.0.1:34571/packages/carbide-pwsh/";
const TIMEOUT_MS = Number(process.env.TIMEOUT_MS ?? 180_000);

console.log(`smoke: loading ${demoUrl}`);
const browser = await chromium.launch({ headless: true });
let failed = false;
try {
    const page = await browser.newPage();
    page.setDefaultTimeout(TIMEOUT_MS);
    page.on("pageerror", (e) => { console.error(`[pageerror] ${e.message}`); failed = true; });
    page.on("console", (m) => { if (m.type() === "error") console.error(`[browser error] ${m.text()}`); });
    await page.goto(demoUrl);

    console.log("smoke: waiting for banner + prompt in xterm\u2026");
    await page.waitForFunction(
        () => {
            const t = window.__dumpBuffer?.() ?? "";
            return t.includes("carbide-pwsh") && t.includes("PS ");
        },
        undefined,
        { timeout: TIMEOUT_MS },
    );
    console.log("smoke: REPL prompt reached");

    const sendLine = async (line, waitMs) => {
        await page.evaluate((l) => window.__term.input(l), line + "\r");
        await page.waitForTimeout(waitMs);
    };

    const cases = [
        // Phase 1 regression.
        { input: "2 + 2", expect: "4" },

        // Phase 2 regression.
        { input: "@(5,3,1,4,2) | Sort-Object", expect: "1" },

        // Phase 3: control flow.
        { input: "foreach ($x in 1..3) { $x * $x }", expect: "9" },
        { input: "if (5 -gt 3) { 'bigger' } else { 'not' }", expect: "bigger" },

        // Phase 3: functions.
        { input: "function Dbl { param($n) $n * 2 }", expect: null },
        { input: "Dbl 21", expect: "42" },

        // Phase 3: try/catch.
        { input: "try { throw 'boom' } catch { \"caught: $($_.Exception.Message)\" }", expect: "caught: boom" },

        // Phase 3: operators.
        { input: "'hello world' -match 'hello'", expect: "True" },
        { input: "'hello world' -replace 'world', 'universe'", expect: "hello universe" },
        { input: "'{0:X}' -f 255", expect: "FF" },
        { input: "@('a','b','c') -join ','", expect: "a,b,c" },
        { input: "@(1,2,3) -contains 2", expect: "True" },

        // Phase 3: classes + enums.
        { input: "class Counter { [int] $N = 0; [int] Inc() { $this.N++; return $this.N } }", expect: null },
        { input: "$c = [Counter]::new(); $c.Inc(); $c.Inc(); $c.Inc()", expect: "3" },
        { input: "enum Color { Red; Green; Blue }; [Color]::Green", expect: "Green" },

        // Phase 3: exit-gate aggregate.
        {
            input: "function Retry { param([scriptblock] $Action, [int] $Times = 3) for ($i = 1; $i -le $Times; $i++) { try { return & $Action } catch { if ($i -eq $Times) { throw } } } }",
            expect: null,
        },
    ];

    for (const c of cases) {
        await sendLine(c.input, 700);
        if (c.expect != null) {
            const buf = await page.evaluate(() => window.__dumpBuffer());
            if (!buf.includes(c.expect)) {
                throw new Error(`smoke: input '${c.input}' did not produce expected '${c.expect}'. Buffer tail:\n${buf.split('\n').slice(-30).join('\n')}`);
            }
            console.log(`smoke: '${c.input.slice(0, 60)}' -> contains '${c.expect}' OK`);
        } else {
            console.log(`smoke: '${c.input.slice(0, 60)}' OK (no output)`);
        }
    }

    await sendLine("exit", 2000);
    await page.waitForFunction(
        () => {
            const s = document.getElementById("status");
            return s && s.dataset.state === "ready" && s.textContent.startsWith("exit \u2713");
        },
        undefined,
        { timeout: 10_000 },
    );
    console.log("smoke: clean exit");
    console.log("smoke: PASSED");
} catch (e) {
    console.error("smoke: FAILED:", e.message);
    failed = true;
} finally {
    await browser.close();
}
process.exit(failed ? 1 : 0);
