// U1.2 — CLI verbosity knobs.
//
// Precedence: explicit `--log-level <level>` flag > `--verbose` / `--quiet` aliases >
// `CARBIDE_LOG_LEVEL` environment variable > default.
//
// Default: "warning". Pre-U1 behaviour (info + trace + debug on stderr) is reachable via
// `--verbose` / `--log-level information`.

import type { ParsedArgs } from "./args.js";
import { lastString } from "./args.js";

/** Matches Carbide.Core's `MinLogLevel` knob in `WebAssemblyConsoleLogger`. */
export type CarbideLogLevel = "trace" | "debug" | "information" | "warning" | "error" | "none";

const LEVEL_ALIASES: Record<string, CarbideLogLevel> = {
    trace: "trace",
    debug: "debug",
    info: "information",
    information: "information",
    warn: "warning",
    warning: "warning",
    error: "error",
    quiet: "error",
    none: "none",
    silent: "none",
};

export class LogLevelParseError extends Error {
    constructor(value: string) {
        super(
            `carbide: invalid log level '${value}'. Valid: trace, debug, info, warning, error, quiet.`,
        );
        this.name = "LogLevelParseError";
    }
}

/**
 * Thrown when the CLI is given a combination of flags that can't be reconciled
 * (`--verbose --quiet`, etc.). Classified as `flag-error` by the CLI's error handler.
 */
export class ConflictingFlagsError extends Error {
    constructor(message: string) {
        super(message);
        this.name = "ConflictingFlagsError";
    }
}

/**
 * Resolve the effective log level for the CLI invocation. Throws {@link LogLevelParseError}
 * on an invalid `--log-level` value; throws {@link ConflictingFlagsError} when mutually
 * exclusive flags are combined (`--verbose --quiet`).
 */
export function resolveLogLevel(args: ParsedArgs): CarbideLogLevel {
    const explicit = lastString(args, "log-level");
    const verbose = args.flags.has("verbose");
    const quiet = args.flags.has("quiet");

    if (verbose && quiet) {
        throw new ConflictingFlagsError(
            "carbide: --verbose and --quiet are mutually exclusive.",
        );
    }
    if (explicit !== undefined) {
        const parsed = LEVEL_ALIASES[explicit.toLowerCase()];
        if (!parsed) throw new LogLevelParseError(explicit);
        return parsed;
    }
    if (verbose) return "information";
    if (quiet) return "error";

    const envLevel = process.env.CARBIDE_LOG_LEVEL;
    if (envLevel && envLevel.length > 0) {
        const parsed = LEVEL_ALIASES[envLevel.toLowerCase()];
        if (!parsed) throw new LogLevelParseError(envLevel);
        return parsed;
    }
    return "warning";
}

/** Flags every CLI command must accept for U1.2 to work consistently. */
export const LOG_LEVEL_STRING_FLAGS = ["log-level"] as const;
export const LOG_LEVEL_BOOLEAN_FLAGS = ["verbose", "quiet"] as const;
