import type { HostAdapter } from "./host/adapter.js";
import { BrowserHostAdapter } from "./host/browser/browser-adapter.js";
import { bootRuntime } from "./runtime/boot.js";
import type { CarbideInteropExports } from "./runtime/dotnet-types.js";
import { SCHEMA_VERSION, type ProjectOptionsRequest } from "./interop/schema.js";
import type { ProjectOptions } from "./types.js";
import { Project } from "./project.js";

export interface CarbideOptions {
    /** Injectable host adapter. When omitted the session auto-picks based on the runtime. */
    hostAdapter?: HostAdapter;
    debugLevel?: number;
    enableDiagnosticTracing?: boolean;
}

export class CarbideSession {
    private constructor(
        private readonly adapter: HostAdapter,
        private readonly interop: CarbideInteropExports,
        private readonly sessionId: string,
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
        const request: ProjectOptionsRequest = {
            schemaVersion: SCHEMA_VERSION,
            targetFramework: options.targetFramework,
            languageVersion: options.languageVersion ?? null,
            nullable: options.nullable ?? null,
            implicitUsings: options.implicitUsings ?? null,
            assemblyName: options.assemblyName ?? null,
            rootNamespace: options.rootNamespace ?? null,
        };
        const projectId = this.interop.CreateProject(this.sessionId, JSON.stringify(request));
        return new Project(this.interop, projectId);
    }

    async shutdown(): Promise<void> {
        try {
            this.interop.DisposeSession(this.sessionId);
        } finally {
            await this.adapter.dispose();
        }
    }
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
