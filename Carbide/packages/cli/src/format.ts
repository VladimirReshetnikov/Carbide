// JSON vs human output rendering shared across CLI commands.

import path from "node:path";
import type { Diagnostic } from "@carbide/core";
import { writeJson } from "./json-output.js";

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

/**
 * Render a diagnostic carrying an optional `project` attribution (M9). When `project` is
 * non-null and distinct from the root csproj, the csproj filename is prepended — keeps
 * single-project output byte-identical and makes sub-project diagnostics obvious in a
 * multi-project graph.
 */
export function renderAttributedDiagnostic(d: Diagnostic & { project?: string | null }): string {
    const base = renderDiagnostic(d);
    if (!d.project) return base;
    const prefix = path.basename(d.project);
    return `${prefix}: ${base}`;
}

// U1: `writeJson` moved to `json-output.ts` (now stamps the sentinel + schemaVersion).
// Re-exported here so existing import sites stay stable.
export { writeJson };
