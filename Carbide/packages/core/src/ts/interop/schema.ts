/**
 * JSON schema versions crossing the JSExport boundary.
 *
 * A version mismatch is a hard error (architecture §5): the boundary should fail fast with a
 * typed message rather than silently accepting malformed payloads. Bump SCHEMA_VERSION here
 * and on the C# side in lock-step.
 */
export const SCHEMA_VERSION = 1 as const;

export interface ProjectOptionsRequest {
    schemaVersion: number;
    targetFramework?: "net8.0" | "net10.0";
    languageVersion?: string | null;
    nullable?: boolean | null;
    implicitUsings?: boolean | null;
    assemblyName?: string | null;
    rootNamespace?: string | null;
}

export class CarbideSchemaError extends Error {
    constructor(public readonly payload: string, public readonly expected: number, public readonly got: unknown) {
        super(`Carbide JSON schema mismatch: expected schemaVersion ${expected}, got ${JSON.stringify(got)}.`);
        this.name = "CarbideSchemaError";
    }
}

export function parseDiagnostics(json: string): import("../types.js").Diagnostic[] {
    const parsed = JSON.parse(json) as unknown;
    if (!Array.isArray(parsed)) {
        throw new TypeError("Carbide: expected diagnostics JSON to be an array.");
    }
    return parsed as import("../types.js").Diagnostic[];
}

export function parseRunResult(json: string): import("../types.js").RunResult {
    const parsed = JSON.parse(json) as import("../types.js").RunResult & { schemaVersion?: unknown };
    if (parsed.schemaVersion !== SCHEMA_VERSION) {
        throw new CarbideSchemaError(json, SCHEMA_VERSION, parsed.schemaVersion);
    }
    return parsed;
}

/** Wire shape of BuildResult — matches C# BuildResultDto. PE/PDB are base64 here. */
interface BuildResultWire {
    schemaVersion: number;
    success: boolean;
    peBase64?: string | null;
    pdbBase64?: string | null;
    diagnostics: import("../types.js").Diagnostic[];
    durationMs: number;
}

export function parseBuildResult(json: string): import("../types.js").BuildResult {
    const parsed = JSON.parse(json) as BuildResultWire & { schemaVersion?: unknown };
    if (parsed.schemaVersion !== SCHEMA_VERSION) {
        throw new CarbideSchemaError(json, SCHEMA_VERSION, parsed.schemaVersion);
    }
    return {
        schemaVersion: parsed.schemaVersion,
        success: parsed.success,
        pe: decodeBase64(parsed.peBase64),
        pdb: decodeBase64(parsed.pdbBase64),
        diagnostics: parsed.diagnostics ?? [],
        durationMs: parsed.durationMs,
    };
}

function decodeBase64(value: string | null | undefined): Uint8Array | undefined {
    if (!value) return undefined;
    // Prefer Node's Buffer (single-shot, no chunking). Fall back to atob in the browser.
    const nodeBuffer = (globalThis as { Buffer?: { from(s: string, enc: "base64"): Uint8Array } }).Buffer;
    if (typeof nodeBuffer?.from === "function") {
        return new Uint8Array(nodeBuffer.from(value, "base64"));
    }
    if (typeof atob !== "function") {
        throw new Error("No base64 decoder available (neither Buffer.from nor atob).");
    }
    const binary = atob(value);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
    }
    return bytes;
}
