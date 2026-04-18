import type { HostAdapter } from "./host/adapter.js";
import { BrowserHostAdapter } from "./host/browser/browser-adapter.js";
import { bootRuntime } from "./runtime/boot.js";
import type { CarbideInteropExports } from "./runtime/dotnet-types.js";
import { SCHEMA_VERSION, type ProjectOptionsRequest } from "./interop/schema.js";
import type { ProjectOptions, ReferenceHandle } from "./types.js";
import { Project } from "./project.js";

export interface CarbideOptions {
    /** Injectable host adapter. When omitted the session auto-picks based on the runtime. */
    hostAdapter?: HostAdapter;
    debugLevel?: number;
    enableDiagnosticTracing?: boolean;
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

    private constructor(
        private readonly adapter: HostAdapter,
        /** @internal */ readonly interop: CarbideInteropExports,
        /** @internal */ readonly sessionId: string,
    ) {}

    static async initializeAsync(options: CarbideOptions = {}): Promise<CarbideSession> {
        const adapter = options.hostAdapter ?? (await autoDetectAdapter());
        const { interop } = await bootRuntime({
            hostAdapter: adapter,
            debugLevel: options.debugLevel,
            enableDiagnosticTracing: options.enableDiagnosticTracing,
        });
        const sessionId = interop.CreateSession(JSON.stringify({ schemaVersion: SCHEMA_VERSION }));
        return new CarbideSession(adapter, interop, sessionId);
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
        return new Project(this.interop, projectId, this.sessionId);
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
