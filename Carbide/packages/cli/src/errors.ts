// U1.3 — centralised CLI error classification.
//
// Every thrown error that bubbles to the top of a `carbide` subcommand is routed through
// `handleCliFailure`, which:
//   - picks a stable exit code from the error's static type,
//   - emits a structured `error` field in the JSON payload (under `--format json`),
//   - prints a friendly one-liner to stderr (under `--format human`),
//   - optionally dumps a truncated stack preview under `--verbose`.
//
// The exit-code taxonomy (see carbide-U1-detailed-plan §2.3):
//   0 success; 1 user-source errors; 2 I/O or internal; 3 flag/config errors;
//   4 NuGet policy refusal; 5 NuGet network / cache miss.
//
// Adding a new error type: add an `if (err instanceof X)` branch to `classifyError`,
// register the `error.category` string, and (if the category is new) document the exit
// code in packages/cli/README.md.

import {
    AllowListRefusedError,
    SafetyRefusalError,
    OfflineCacheMissError,
    LockReadError,
} from "@carbide/nuget";
import { CarbideSchemaError } from "@carbide/core";
import {
    ProjectGraphCycleError,
    ProjectGraphNameCollisionError,
    ProjectReferenceNotFoundError,
} from "./project-graph.js";
import { ArgParseError } from "./args.js";
import { LogLevelParseError, ConflictingFlagsError } from "./logging.js";
import type { Format } from "./format.js";
import { writeJson } from "./json-output.js";

/** Exit codes the CLI promises to use. Stable across releases. */
export type CliExitCode = 0 | 1 | 2 | 3 | 4 | 5;

/** Closed set of error categories surfaced in the `error.category` JSON field. */
export type CliErrorCategory =
    | "internal"
    | "schema-mismatch"
    | "flag-error"
    | "lock-read"
    | "allow-list-refusal"
    | "safety-refusal"
    | "offline-cache-miss"
    | "nuget-fetch-failed"
    | "project-graph-cycle"
    | "assembly-name-collision"
    | "project-reference-missing";

export interface ClassifiedError {
    exitCode: CliExitCode;
    category: CliErrorCategory;
    /** Carbide diagnostic-style code (e.g. `MSNUGET021`, `MSPROJ001`) when one fits. */
    code: string;
    /** Human-readable one-liner. */
    message: string;
    /** Arbitrary category-specific payload (package id, csproj paths, etc.). */
    details?: Record<string, unknown>;
    /** Stack trace, kept for `--verbose` rendering. Trimmed at emit time. */
    stack?: string;
}

/**
 * Classify an unknown throw into a structured error record. Errors this module doesn't
 * recognise fall through to `category: "internal"` with the original message preserved.
 */
export function classifyError(err: unknown): ClassifiedError {
    if (err instanceof ProjectGraphCycleError) {
        return {
            exitCode: 1,
            category: "project-graph-cycle",
            code: "MSPROJ001",
            message: err.message,
            details: { cyclePath: err.cyclePath },
            stack: err.stack,
        };
    }
    if (err instanceof ProjectGraphNameCollisionError) {
        return {
            exitCode: 3,
            category: "assembly-name-collision",
            code: "MSPROJ002",
            message: err.message,
            details: { assemblyName: err.assemblyName, csprojPaths: err.csprojPaths },
            stack: err.stack,
        };
    }
    if (err instanceof ProjectReferenceNotFoundError) {
        return {
            exitCode: 1,
            category: "project-reference-missing",
            code: "MSPROJ004",
            message: err.message,
            details: {
                referrer: err.referrerPath,
                include: err.includeAttr,
                resolvedPath: err.resolvedPath,
            },
            stack: err.stack,
        };
    }
    if (err instanceof AllowListRefusedError) {
        return {
            exitCode: 4,
            category: "allow-list-refusal",
            code: "MSNUGET021",
            message: err.message,
            details: { package: { id: err.packageId } },
            stack: err.stack,
        };
    }
    if (err instanceof SafetyRefusalError) {
        return {
            exitCode: 4,
            category: "safety-refusal",
            code: err.code,
            message: err.message,
            stack: err.stack,
        };
    }
    if (err instanceof OfflineCacheMissError) {
        return {
            exitCode: 5,
            category: "offline-cache-miss",
            code: "MSNUGET030",
            message: err.message,
            details: { package: { id: err.id, version: err.version } },
            stack: err.stack,
        };
    }
    if (err instanceof LockReadError) {
        return {
            exitCode: 2,
            category: "lock-read",
            code: "MSNUGET031",
            message: err.message,
            stack: err.stack,
        };
    }
    if (err instanceof CarbideSchemaError) {
        return {
            exitCode: 2,
            category: "schema-mismatch",
            code: "CARBIDE_SCHEMA",
            message: err.message,
            details: { expected: err.expected, got: err.got },
            stack: err.stack,
        };
    }
    if (
        err instanceof LogLevelParseError ||
        err instanceof ArgParseError ||
        err instanceof ConflictingFlagsError
    ) {
        return {
            exitCode: 3,
            category: "flag-error",
            code: "CARBIDE_FLAG",
            message: (err as Error).message,
            stack: (err as Error).stack,
        };
    }
    // Unknown: keep the message + stack for verbose mode, fall through to "internal".
    const message = err instanceof Error ? err.message : String(err);
    const stack = err instanceof Error ? err.stack : undefined;
    return {
        exitCode: 2,
        category: "internal",
        code: "CARBIDE_INTERNAL",
        message,
        stack,
    };
}

export interface HandleCliFailureOptions {
    /** When true, append a truncated stack preview to stderr. */
    verbose?: boolean;
}

/**
 * Handle an error thrown from a CLI command. Writes the structured payload (JSON mode) or
 * friendly one-liner (human mode), returns the chosen exit code, and never rethrows.
 * The caller is expected to `return` the result immediately as the command's exit code.
 */
export function handleCliFailure(
    err: unknown,
    format: Format,
    options: HandleCliFailureOptions = {},
): CliExitCode {
    const classified = classifyError(err);

    if (format === "json") {
        const errorField: Record<string, unknown> = {
            code: classified.code,
            category: classified.category,
            message: classified.message,
        };
        if (classified.details) errorField.details = classified.details;
        writeJson({
            success: false,
            error: errorField,
            diagnostics: [],
            warnings: [],
        });
    } else {
        process.stderr.write(`carbide: ${classified.message}\n`);
    }

    if (options.verbose && classified.stack) {
        // Truncate to the first few frames so we don't deluge the terminal on benign
        // errors. 5 frames is usually enough to see the throw site and its immediate call
        // chain without drowning in Node internals.
        const lines = classified.stack.split("\n").slice(0, 6);
        process.stderr.write(lines.join("\n") + "\n");
    }

    return classified.exitCode;
}
