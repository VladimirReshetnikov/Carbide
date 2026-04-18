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
