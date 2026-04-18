// JSON vs human output rendering shared across CLI commands.

import type { Diagnostic } from "@carbide/core";

export type Format = "json" | "human";

export function parseFormat(raw: string | undefined): Format {
    if (raw === undefined) return "json";
    if (raw === "json" || raw === "human") return raw;
    throw new Error(`--format must be 'json' or 'human'; got '${raw}'.`);
}

/** Format a diagnostic as a one-line human-readable string. */
export function renderDiagnostic(d: Diagnostic): string {
    const loc = d.path
        ? `${d.path}(${d.lineStart ?? "?"}, ${d.columnStart ?? "?"})`
        : "<unknown>";
    return `${loc}: ${d.severity} ${d.id}: ${d.message}`;
}

export function writeJson(payload: unknown): void {
    process.stdout.write(JSON.stringify(payload) + "\n");
}
