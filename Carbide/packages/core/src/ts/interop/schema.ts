/**
 * JSON schema versions crossing the JSExport boundary.
 *
 * A version mismatch is a hard error (architecture §5): the boundary should fail fast with a
 * typed message rather than silently accepting malformed payloads. Bump SCHEMA_VERSION here
 * and on the C# side in lock-step.
 */
// M5: bumped to 2 when ProjectOptions gained defineConstants. The C# side accepts both 1 and
// 2; new TS clients always send 2.
// U2: bumped to 3 when RunAsync gained argv + stdin forwarding (`RunOptionsRequest`). The
// C# side accepts 2 or 3; new TS clients always send 3.
// T1: bumped to 4 when the interactive terminal path (`RunInteractiveAsync` + bridge-mediated
// streaming output) landed. The C# side accepts 1/2/3/4; new TS clients always send 4.
// T2: bumped to 5 when the input-side bridge exports (DeliverStdIn, NotifyResize,
// DeliverSignal, SetTreatControlCAsInput) landed. The C# side accepts 1/2/3/4/5; new TS
// clients always send 5.
export const SCHEMA_VERSION = 5 as const;

export interface ProjectOptionsRequest {
    schemaVersion: number;
    targetFramework?: "net8.0" | "net10.0";
    languageVersion?: string | null;
    nullable?: boolean | null;
    implicitUsings?: boolean | null;
    assemblyName?: string | null;
    rootNamespace?: string | null;
    defineConstants?: string[] | null;
}

/**
 * U2 — optional knobs for {@link import("../project.js").Project.run}. When no args are
 * provided, `run()` may pass an empty string to the interop to skip JSON parsing entirely
 * (the C# side treats an empty string as defaults).
 */
export interface RunOptionsRequest {
    schemaVersion: number;
    args?: string[];
    stdin?: string | null;
}

/**
 * T1 — optional knobs for {@link import("../project.js").Project.runInteractive}. Unlike
 * {@link RunOptionsRequest}, an interactive run always goes through the JSON payload — the
 * empty-string fast-path is not a shape `runInteractive` ever takes.
 */
export interface RunInteractiveOptionsRequest {
    schemaVersion: number;
    args?: string[];
    /**
     * SGR style applied (on the C# side) around each stderr flush chunk before it reaches
     * the bridge. `"plain"` — no wrap. `"dim"` — `\x1b[2m…\x1b[22m`. `"red"` — `\x1b[31m…\x1b[39m`.
     */
    stderrStyle?: "plain" | "dim" | "red";
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

/**
 * Versions accepted by the TS-side parsers during T1's bring-up transition. Matches the C#
 * validator's own "accept N-1 or N" policy (see `CompilationInterop.ValidateSchemaVersion`).
 * TS clients always emit `SCHEMA_VERSION` on outbound payloads; parsers tolerate one minor
 * back-version so a partially-rebuilt tree (TS bumped, C# publish not refreshed yet) fails
 * loudly on real mismatches, not on the one-step transition.
 */
const ACCEPTED_INBOUND_SCHEMAS: readonly number[] = [SCHEMA_VERSION - 1, SCHEMA_VERSION];

function checkSchema(json: string, got: unknown): void {
    if (typeof got !== "number" || !ACCEPTED_INBOUND_SCHEMAS.includes(got)) {
        throw new CarbideSchemaError(json, SCHEMA_VERSION, got);
    }
}

export function parseRunResult(json: string): import("../types.js").RunResult {
    const parsed = JSON.parse(json) as import("../types.js").RunResult & { schemaVersion?: unknown };
    checkSchema(json, parsed.schemaVersion);
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
    checkSchema(json, parsed.schemaVersion);
    return {
        schemaVersion: parsed.schemaVersion as number,
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
