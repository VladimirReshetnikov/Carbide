import type { HostAdapter, SideloadedRefPack } from "../adapter.js";
import type { EmscriptenModuleOverlays } from "../../runtime/dotnet-types.js";
import type { TerminalBridgeSink } from "../../terminal/bridge.js";
import { uninstallBridge } from "../../terminal/bridge.js";

export interface BrowserAdapterOptions {
    /**
     * Explicit override for the base URL that serves `_framework/`. When omitted, the adapter
     * derives it from the directory containing the module-level import.meta.url plus the
     * standard path that `@carbide/core`'s publish places the runtime at.
     */
    frameworkAssetsBaseUrl?: string;
    /** Module URL used to derive the default base URL. */
    moduleUrl?: string;
    /**
     * core-P1 browser follow-up: base URL the browser adapter joins against to resolve
     * sideload packages. Each package name is fetched as
     * `${sideloadBaseUrl}/${packageName}/refpack.json`. Typical values:
     *
     * - Vite/Webpack dev server: `"/node_modules"` — the dev server serves it directly.
     * - Production deploy with an npm-installed tree: whatever path serves `node_modules/`.
     * - CDN: e.g. `"https://unpkg.com"` (refpack.json + ref/*.dll must all be reachable).
     *
     * When omitted, `loadSideloadRefPack` rejects with a message telling the caller to
     * set this option. No URL is silently guessed.
     */
    sideloadBaseUrl?: string;
}

/**
 * Browser host adapter.
 *
 * In T1 the adapter grew a terminal-sink slot and the emscripten `print`/`printErr` overlay
 * that routes native-side writes (including bytes from `Console.OpenStandardOutput()`) into
 * the active interactive terminal's bridge when one is attached, and to `console.log` /
 * `console.error` otherwise.
 */
export class BrowserHostAdapter implements HostAdapter {
    public readonly hostKind = "browser" as const;
    private readonly baseUrl: string;
    private readonly sideloadBaseUrl: string | undefined;
    /**
     * Non-null while an interactive terminal session is live. The emscripten `print` /
     * `printErr` multiplexers consult this slot on each invocation; switching sessions is
     * a matter of calling {@link attachTerminalSink} / {@link detachTerminalSink} rather
     * than re-configuring the runtime (which can't happen after boot).
     */
    private _terminalSink: TerminalBridgeSink | null = null;

    constructor(options: BrowserAdapterOptions = {}) {
        this.sideloadBaseUrl = options.sideloadBaseUrl
            ? ensureTrailingSlash(options.sideloadBaseUrl)
            : undefined;

        if (options.frameworkAssetsBaseUrl) {
            this.baseUrl = ensureTrailingSlash(options.frameworkAssetsBaseUrl);
            return;
        }

        const moduleUrl = options.moduleUrl;
        if (!moduleUrl) {
            throw new Error(
                "BrowserHostAdapter requires either frameworkAssetsBaseUrl or moduleUrl; pass import.meta.url from the call site.",
            );
        }
        const moduleDir = moduleUrl.slice(0, moduleUrl.lastIndexOf("/") + 1);
        // Default layout: the TS entry sits at dist/, the published _framework/ is at src/bin/Release/net10.0/publish/wwwroot/_framework/.
        // Consumers that package differently can override via frameworkAssetsBaseUrl.
        this.baseUrl = new URL("../src/bin/Release/net10.0/publish/wwwroot/_framework/", moduleDir).toString();
    }

    resolveFrameworkAssetsBaseUrl(): Promise<string> {
        return Promise.resolve(this.baseUrl);
    }

    /**
     * T1 — returns `print` / `printErr` multiplexers that route to the active terminal
     * sink when one is attached, or to `console.log` / `console.error` otherwise.
     * Emscripten calls `print(line)` / `printErr(line)` per line with the trailing newline
     * stripped; the multiplexer re-appends `\n` so the xterm buffer sees consistent line
     * structure.
     */
    resolveRuntimeConfigOverlays(): Promise<EmscriptenModuleOverlays> {
        return Promise.resolve({
            print: (text: string): void => {
                const sink = this._terminalSink;
                if (sink) {
                    sink.writeStdOut(text + "\n");
                } else {
                    console.log(text);
                }
            },
            printErr: (text: string): void => {
                const sink = this._terminalSink;
                if (sink) {
                    sink.writeStdErr(text + "\n");
                } else {
                    console.error(text);
                }
            },
        });
    }

    /**
     * T1 — bind an interactive session's terminal sink so native-side writes reach xterm.
     * Called by {@link import("../../terminal/session.js").startInteractiveSession} at the
     * top of each interactive run.
     */
    attachTerminalSink(sink: TerminalBridgeSink): void {
        this._terminalSink = sink;
    }

    /** T1 — release the terminal sink. Called by the session shell's teardown. */
    detachTerminalSink(): void {
        this._terminalSink = null;
    }

    /**
     * core-P1 browser follow-up: fetch `{sideloadBaseUrl}/{packageName}/refpack.json`
     * and stream each listed DLL over HTTP. Mirrors the Node adapter's semantics so a
     * `CarbideOptions.sideload` declaration works uniformly across host kinds.
     *
     * Rejects with a self-descriptive error when:
     *   - `sideloadBaseUrl` was not supplied to the adapter constructor
     *   - the manifest fetch returns non-2xx
     *   - the manifest is malformed (no `refDirectory` or empty `dlls` array)
     *   - any referenced DLL returns non-2xx
     *
     * On success the session applies the returned refs via `addReference` during
     * `initializeAsync`; the DLLs are also auto-attached to every project created
     * from the session per core-P1's default-reference semantics.
     */
    async loadSideloadRefPack(packageName: string): Promise<SideloadedRefPack> {
        if (!this.sideloadBaseUrl) {
            throw new Error(
                `[sideload] ${packageName}: browser adapter needs a sideloadBaseUrl to resolve ` +
                `package URLs. Pass it when constructing BrowserHostAdapter, e.g. ` +
                `new BrowserHostAdapter({ ..., sideloadBaseUrl: "/node_modules" }).`,
            );
        }
        // npm package names (including scoped ones like "@carbide-ui/refs-avalonia")
        // are valid URL path segments as-is — `@` and `/` are RFC 3986 pchar and
        // delimiter respectively; no component encoding needed.
        const manifestUrl = new URL(
            `${packageName}/refpack.json`,
            this.sideloadBaseUrl,
        ).toString();
        const manifestResp = await fetch(manifestUrl);
        if (!manifestResp.ok) {
            throw new Error(
                `[sideload] ${packageName}: failed to fetch ${manifestUrl} ` +
                `(${manifestResp.status} ${manifestResp.statusText}).`,
            );
        }
        const manifest = (await manifestResp.json()) as {
            refDirectory?: string;
            dlls?: Array<{ name?: string }>;
        };
        if (!manifest.refDirectory) {
            throw new Error(
                `[sideload] ${packageName}: refpack.json is missing the 'refDirectory' field.`,
            );
        }
        const dllList = (manifest.dlls ?? []).filter(
            (d): d is { name: string } => typeof d?.name === "string",
        );
        if (dllList.length === 0) {
            throw new Error(`[sideload] ${packageName}: refpack.json lists no DLLs.`);
        }
        const pkgBase = new URL(`${packageName}/`, this.sideloadBaseUrl);
        const refBase = new URL(ensureTrailingSlash(manifest.refDirectory), pkgBase);
        const dlls = await Promise.all(
            dllList.map(async (d) => {
                const dllUrl = new URL(d.name, refBase).toString();
                const resp = await fetch(dllUrl);
                if (!resp.ok) {
                    throw new Error(
                        `[sideload] ${packageName}: failed to fetch DLL ${d.name} from ${dllUrl} ` +
                        `(${resp.status} ${resp.statusText}).`,
                    );
                }
                const bytes = new Uint8Array(await resp.arrayBuffer());
                return { name: d.name, bytes };
            }),
        );
        return { packageName, manifestPath: manifestUrl, dlls };
    }

    dispose(): Promise<void> {
        // Belt-and-suspenders: if the caller shuts down mid-interactive-run without
        // explicitly calling `session.dispose()` first, make sure the `globalThis.Carbide.Terminal`
        // pointer doesn't linger past this adapter's lifetime. `uninstallBridge` is idempotent
        // so calling it when no session is live is a no-op.
        this._terminalSink = null;
        uninstallBridge();
        return Promise.resolve();
    }
}

function ensureTrailingSlash(url: string): string {
    return url.endsWith("/") ? url : `${url}/`;
}
