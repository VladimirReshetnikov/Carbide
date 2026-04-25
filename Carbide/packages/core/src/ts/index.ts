export { CarbideSession, type CarbideOptions } from "./session.js";
export { Project } from "./project.js";
export type {
    Diagnostic,
    DiagnosticSeverity,
    RunResult,
    RunOptions,
    RunAssemblyOptions,
    BuildResult,
    ProjectOptions,
    ReferenceHandle,
    InteractiveRunOptions,
    TerminalSession,
    XtermTerminalLike,
} from "./types.js";
export { CARBIDE_VERSION } from "./version.js";
export { BrowserHostAdapter, type BrowserAdapterOptions } from "./host/browser/browser-adapter.js";
export type { HostAdapter, ReferencePackDescriptor } from "./host/adapter.js";
export { CarbideSchemaError, SCHEMA_VERSION } from "./interop/schema.js";

/** @deprecated transitional M0 helper; replaced by CarbideSession.initializeAsync. */
export async function initialize(): Promise<string> {
    return "Carbide initialised";
}

// Node-only types (NodeHostAdapter, startAssetServer) ship under the "@carbide/core/node"
// subpath to keep the browser static import graph free of node:* built-ins. Import them from
// that subpath when working in Node.
