import { mkdir, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { startCarbidePwshStaticServer } from "../../scripts/serve.mjs";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PACKAGE_ROOT = path.resolve(__dirname, "..", "..");
const ARTIFACT_ROOT = path.join(PACKAGE_ROOT, "..", "..", "test-results", "carbide-pwsh-browser");
const playwrightUrl = new URL("../../../core/node_modules/playwright/index.mjs", import.meta.url);
const { chromium } = await import(playwrightUrl.href);

const ANSI_SGR = /\x1b\[[0-9;]*m/g;

export function stripAnsi(text) {
    return text.replace(ANSI_SGR, "");
}

export async function launchPwshBrowser(options = {}) {
    const timeoutMs = Number(options.timeoutMs ?? process.env.CARBIDE_PWSH_BROWSER_TIMEOUT_MS ?? 240_000);
    const headless = options.headless ?? !["0", "false", "no"].includes(
        String(process.env.CARBIDE_PWSH_BROWSER_HEADLESS ?? "true").toLowerCase(),
    );
    const serverInfo = await startCarbidePwshStaticServer({
        port: Number(options.port ?? process.env.CARBIDE_PWSH_BROWSER_PORT ?? 0),
    });
    const browser = await chromium.launch({
        headless,
        args: [
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-search-engine-choice-screen",
        ],
    });
    const page = await browser.newPage({
        viewport: options.viewport ?? { width: 1280, height: 900 },
    });
    page.setDefaultTimeout(timeoutMs);

    const logs = [];
    const pageErrors = [];
    page.on("console", (message) => {
        logs.push({
            type: message.type(),
            text: message.text(),
        });
    });
    page.on("pageerror", (error) => {
        pageErrors.push(error.stack ?? error.message);
    });

    const shell = new PwshBrowserShell({
        browser,
        logs,
        page,
        pageErrors,
        server: serverInfo.server,
        timeoutMs,
        url: serverInfo.url,
    });

    await page.goto(serverInfo.url);
    await shell.waitForReady();
    await page.locator("#term").click();
    return shell;
}

export class PwshBrowserShell {
    constructor({ browser, logs, page, pageErrors, server, timeoutMs, url }) {
        this.browser = browser;
        this.logs = logs;
        this.page = page;
        this.pageErrors = pageErrors;
        this.server = server;
        this.timeoutMs = timeoutMs;
        this.url = url;
    }

    async close() {
        await this.browser.close();
        await new Promise((resolve) => this.server.close(resolve));
    }

    async waitForReady() {
        await this.page.waitForFunction(
            () => {
                const status = document.getElementById("status");
                const buffer = window.__dumpBuffer?.() ?? "";
                return status?.dataset.state === "ready" &&
                    buffer.includes("carbide-pwsh") &&
                    buffer.includes("PS ");
            },
            undefined,
            { timeout: this.timeoutMs },
        );
    }

    async mark() {
        return (await this.rawBuffer()).length;
    }

    async rawBuffer() {
        return this.page.evaluate(() => window.__dumpBuffer?.() ?? "");
    }

    async textBuffer() {
        return stripAnsi(await this.rawBuffer());
    }

    async tail(lineCount = 80) {
        return (await this.textBuffer()).split("\n").slice(-lineCount).join("\n");
    }

    async waitForText(needle, options = {}) {
        const since = Number(options.since ?? 0);
        const timeout = Number(options.timeoutMs ?? this.timeoutMs);
        await this.page.waitForFunction(
            ({ expected, minLength }) => {
                const raw = window.__dumpBuffer?.() ?? "";
                if (raw.length <= minLength) return false;
                return raw.replace(/\x1b\[[0-9;]*m/g, "").includes(expected);
            },
            { expected: needle, minLength: since },
            { timeout },
        );
    }

    async waitForPrompt(options = {}) {
        await this.waitForText(options.prompt ?? "PS /home/user>", options);
    }

    async sendLine(line, options = {}) {
        const before = await this.mark();
        await this.page.locator("#term").click();
        if (options.entryMode === "paste") {
            const pasted = await this.page.evaluate((text) => {
                if (!window.__term?.paste) return false;
                window.__term.paste(text);
                return true;
            }, line);
            if (!pasted) {
                await this.page.keyboard.type(line, { delay: Number(options.delayMs ?? 0) });
            }
        } else {
            await this.page.keyboard.type(line, { delay: Number(options.delayMs ?? 0) });
        }
        await this.page.keyboard.press("Enter");
        if (options.waitForPrompt !== false) {
            await this.waitForPrompt({
                since: before,
                timeoutMs: options.timeoutMs,
            });
        }
        return before;
    }

    async expectText(needle, options = {}) {
        await this.waitForText(needle, options);
        const text = await this.textBuffer();
        if (!text.includes(needle)) {
            throw new Error(`Expected terminal buffer to contain '${needle}'. Tail:\n${await this.tail()}`);
        }
    }

    assertNoPageErrors() {
        if (this.pageErrors.length) {
            throw new Error(`Browser page errors:\n${this.pageErrors.join("\n\n")}`);
        }
    }

    async saveArtifacts(name, extra = {}) {
        const safeName = name.replace(/[^a-z0-9_.-]+/gi, "-").replace(/^-+|-+$/g, "") || "artifact";
        await mkdir(ARTIFACT_ROOT, { recursive: true });
        const prefix = path.join(ARTIFACT_ROOT, `${Date.now()}-${safeName}`);
        await this.page.screenshot({ path: `${prefix}.png`, fullPage: true });
        await writeFile(`${prefix}.buffer.txt`, await this.textBuffer(), "utf8");
        await writeFile(
            `${prefix}.json`,
            JSON.stringify({
                url: this.url,
                pageErrors: this.pageErrors,
                logs: this.logs,
                ...extra,
            }, null, 2),
            "utf8",
        );
        return prefix;
    }
}
