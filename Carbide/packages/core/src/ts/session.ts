import type { HostAdapter } from "./host/adapter.js";
import { BrowserHostAdapter } from "./host/browser/browser-adapter.js";
import { bootRuntime } from "./runtime/boot.js";
import type { CarbideInteropExports } from "./runtime/dotnet-types.js";
import { parseRunResult, SCHEMA_VERSION, type ProjectOptionsRequest } from "./interop/schema.js";
import type { ProjectOptions, ReferenceHandle, RunAssemblyOptions, RunResult } from "./types.js";
import { Project } from "./project.js";

export interface CarbideOptions {
    /** Injectable host adapter. When omitted the session auto-picks based on the runtime. */
    hostAdapter?: HostAdapter;
    debugLevel?: number;
    enableDiagnosticTracing?: boolean;
    /**
     * Minimum log level for Carbide.Core's internal loggers. Defaults to `"warning"` —
     * typical invocations produce no stderr output. Pass `"information"` or higher for
     * the pre-U1 "chatty" behaviour (useful when debugging Carbide itself).
     */
    logLevel?: "trace" | "debug" | "information" | "warning" | "error" | "none";
    /**
     * core-P1 (plan §10.1): npm package names whose `refpack.json` manifests should be
     * resolved at session init and whose listed DLLs should be fed through
     * {@link CarbideSession.addReference}. Useful for adding a framework's compile-time
     * API surface (e.g. `"@carbide-ui/refs-avalonia"`) without hand-writing the
     * `addReference` loop. Node adapter resolves via `createRequire`; browser adapter
     * currently rejects — feed refs manually in the browser until a future adapter
     * revision lands. Non-empty `sideload` against an adapter that doesn't implement
     * `loadSideloadRefPack` surfaces a self-descriptive error and shuts the session
     * down before returning.
     */
    sideload?: readonly string[];
}

/** Mutable bag shared between session and its children so disposed-state propagates. */
interface MutableHandle {
    readonly id: string;
    readonly name?: string;
    readonly sessionId: string;
    disposed: boolean;
}

export class CarbideSession {
    private readonly handles = new Set<MutableHandle>();
    private shutdownStarted = false;
    /**
     * core-P1: references loaded via `CarbideOptions.sideload`. Every project created
     * through {@link createProject} automatically attaches these. Populated at init;
     * never mutated afterwards.
     */
    private readonly defaultReferences: ReferenceHandle[] = [];

    private constructor(
        /** @internal */ readonly adapter: HostAdapter,
        /** @internal */ readonly interop: CarbideInteropExports,
        /** @internal */ readonly sessionId: string,
    ) {}

    static async initializeAsync(options: CarbideOptions = {}): Promise<CarbideSession> {
        const adapter = options.hostAdapter ?? (await autoDetectAdapter());
        const { interop } = await bootRuntime({
            hostAdapter: adapter,
            debugLevel: options.debugLevel,
            enableDiagnosticTracing: options.enableDiagnosticTracing,
            logLevel: options.logLevel,
        });
        const sessionId = interop.CreateSession(JSON.stringify({ schemaVersion: SCHEMA_VERSION }));
        const session = new CarbideSession(adapter, interop, sessionId);

        // core-P1 (plan §10.1): resolve each sideload package's refpack.json and feed its
        // DLLs through addReference. Wrapped in try/catch so any failure (missing package,
        // malformed manifest, missing DLL) tears the session down before rethrowing — otherwise
        // a partial sideload would leak the C# session and hide its failure mode.
        if (options.sideload && options.sideload.length > 0) {
            try {
                if (!adapter.loadSideloadRefPack) {
                    throw new Error(
                        `CarbideOptions.sideload is set but host adapter '${adapter.hostKind}' does not implement loadSideloadRefPack.`,
                    );
                }
                for (const packageName of options.sideload) {
                    const pack = await adapter.loadSideloadRefPack(packageName);
                    for (const dll of pack.dlls) {
                        const handle = session.addReference(dll.bytes, dll.name);
                        session.defaultReferences.push(handle);
                    }
                }
            } catch (err) {
                await session.shutdown();
                throw err;
            }
        }

        return session;
    }

    createProject(options: ProjectOptions = {}): Project {
        this.assertAlive();
        const request: ProjectOptionsRequest = {
            schemaVersion: SCHEMA_VERSION,
            targetFramework: options.targetFramework,
            languageVersion: options.languageVersion ?? null,
            nullable: options.nullable ?? null,
            implicitUsings: options.implicitUsings ?? null,
            assemblyName: options.assemblyName ?? null,
            rootNamespace: options.rootNamespace ?? null,
            defineConstants: options.defineConstants ?? null,
        };
        const projectId = this.interop.CreateProject(this.sessionId, JSON.stringify(request));
        const project = new Project(this.interop, projectId, this.sessionId, this.adapter);
        // core-P1: sideloaded refs (from CarbideOptions.sideload) auto-attach to every
        // project created from this session. Matches the user expectation of "give me
        // these refs for every project" without requiring per-project wiring.
        for (const handle of this.defaultReferences) {
            project.addReference(handle);
        }
        return project;
    }

    /**
     * Registers raw metadata-reference bytes on this session. Returns an opaque handle that
     * can be passed to {@link Project.addReference}. PE metadata is validated synchronously;
     * malformed bytes throw before the handle is produced (see architecture §5 D29).
     */
    addReference(bytes: Uint8Array, name?: string): ReferenceHandle {
        this.assertAlive();
        if (!(bytes instanceof Uint8Array)) {
            throw new TypeError("CarbideSession.addReference: bytes must be a Uint8Array.");
        }
        if (bytes.length === 0) {
            throw new Error("CarbideSession.addReference: bytes must be non-empty.");
        }
        const base64 = bytesToBase64(bytes);
        const id = this.interop.AddReference(this.sessionId, base64, name ?? null);
        const handle: MutableHandle = {
            id,
            name,
            sessionId: this.sessionId,
            disposed: false,
        };
        this.handles.add(handle);
        return handle;
    }

    /**
     * Removes a reference from the session registry and detaches it from every project that
     * had it attached. The handle becomes invalid. No-op if the handle is already disposed.
     */
    removeReference(handle: ReferenceHandle): void {
        const mh = this.findHandle(handle);
        if (!mh || mh.disposed) return;
        this.interop.RemoveReference(this.sessionId, mh.id);
        mh.disposed = true;
        this.handles.delete(mh);
    }

    async runAssembly(options: RunAssemblyOptions): Promise<RunResult> {
        this.assertAlive();
        if (!options || !(options.pe instanceof Uint8Array)) {
            throw new TypeError("CarbideSession.runAssembly: options.pe must be a Uint8Array.");
        }
        if (options.pe.length === 0) {
            throw new Error("CarbideSession.runAssembly: options.pe must be non-empty.");
        }
        const payload = {
            schemaVersion: SCHEMA_VERSION,
            peBase64: bytesToBase64(options.pe),
            referencesBase64: (options.references ?? []).map((bytes) => {
                if (!(bytes instanceof Uint8Array)) {
                    throw new TypeError("CarbideSession.runAssembly: every reference must be a Uint8Array.");
                }
                return bytesToBase64(bytes);
            }),
            args: options.args ? [...options.args] : [],
            stdin: options.stdin ?? null,
        };
        const json = await this.interop.RunAssemblyAsync(this.sessionId, JSON.stringify(payload));
        return parseRunResult(json);
    }

    async shutdown(): Promise<void> {
        if (this.shutdownStarted) return;
        this.shutdownStarted = true;
        for (const h of this.handles) {
            h.disposed = true;
        }
        this.handles.clear();
        try {
            this.interop.DisposeSession(this.sessionId);
        } finally {
            await this.adapter.dispose();
        }
    }

    private assertAlive(): void {
        if (this.shutdownStarted) {
            throw new Error("CarbideSession has been shut down.");
        }
    }

    private findHandle(handle: ReferenceHandle): MutableHandle | undefined {
        for (const h of this.handles) {
            if (h.id === handle.id && h.sessionId === handle.sessionId) return h;
        }
        return undefined;
    }
}

function bytesToBase64(bytes: Uint8Array): string {
    // Prefer Node's Buffer when available (no chunking required). Fall back to a chunked
    // String.fromCharCode + btoa path in the browser to avoid "argument too long" errors.
    const nodeBuffer = (globalThis as { Buffer?: { from(b: Uint8Array): { toString(enc: "base64"): string } } })
        .Buffer;
    if (typeof nodeBuffer?.from === "function") {
        return nodeBuffer.from(bytes).toString("base64");
    }
    let binary = "";
    const chunkSize = 0x8000;
    for (let i = 0; i < bytes.length; i += chunkSize) {
        binary += String.fromCharCode(...bytes.subarray(i, Math.min(i + chunkSize, bytes.length)));
    }
    if (typeof btoa !== "function") {
        throw new Error("No base64 encoder available (neither Buffer.from nor btoa).");
    }
    return btoa(binary);
}

async function autoDetectAdapter(): Promise<HostAdapter> {
    if (isNode()) {
        // Node-specific module graph (node:http, node:fs/promises, node:url) must not appear
        // in the browser's static import graph, so the Node adapter is pulled in lazily.
        const mod = await import("./host/node/node-adapter.js");
        return new mod.NodeHostAdapter();
    }
    if (typeof import.meta === "object" && typeof (import.meta as { url?: unknown }).url === "string") {
        return new BrowserHostAdapter({ moduleUrl: (import.meta as { url: string }).url });
    }
    throw new Error(
        "Could not auto-detect a host adapter. Pass options.hostAdapter explicitly (BrowserHostAdapter or NodeHostAdapter).",
    );
}

function isNode(): boolean {
    const proc = (globalThis as { process?: { versions?: { node?: string } } }).process;
    return typeof proc !== "undefined" && typeof proc.versions?.node === "string";
}
