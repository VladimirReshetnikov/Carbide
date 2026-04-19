// U1 — structured JSON output with a sentinel line framing the trailer.
//
// Why the sentinel: a user program can write raw bytes to stdout via
// `Console.OpenStandardOutput()` (Carbide's `Console.SetOut`-based capture doesn't cover
// that handle-level path). That can smear bytes across stdout and confuse consumers that
// try to parse "stdout as JSON." Framing the JSON trailer with a sentinel gives consumers
// an unambiguous delimiter to scan for, while keeping the legacy "last non-empty line"
// heuristic working for pre-U1 consumers.
//
// Wire format for `--format json` (the default):
//
//     [... any bytes from the user program ...]
//     \x1E\x1Ecarbide-json\x1E\x1E\n      ← this line exactly (16 bytes + LF)
//     {"schemaVersion":3,"success":true,...}\n
//
// Consumers: read stdout to EOF, split on LF, find the last line byte-equal to the
// sentinel string (without the LF), parse the next line as JSON. Equivalently, parse the
// last non-empty line — the sentinel is not valid JSON but the trailer that follows is.

/**
 * CLI JSON schema version. Distinct from `@carbide/core`'s interop `SCHEMA_VERSION`
 * because this governs the CLI-to-consumer wire, not the TS-to-C# wire.
 *
 * History:
 * - 2 (implicit) — shape that shipped with M4/M5/M6/M9; `success`, `assemblyName`,
 *   `diagnostics`, `warnings`, run-specific fields (`stdOut`, `stdErr`, `exitCode`).
 * - 3 — U1. Adds `schemaVersion` as an always-present top-level field; adds an `error`
 *   field on failure payloads with `code` / `category` / `message` / `details`.
 */
export const CLI_SCHEMA_VERSION = 3 as const;

/** Sentinel line separating user-program output from the JSON trailer. 16 bytes + LF. */
export const JSON_SENTINEL = "\x1E\x1Ecarbide-json\x1E\x1E";

/**
 * Write `payload` as the JSON trailer. Emits a leading newline (so the sentinel lands on
 * its own line even if user output didn't end with `\n`), then the sentinel, then the
 * JSON body. `schemaVersion: CLI_SCHEMA_VERSION` is stamped at the top of the object;
 * callers should not duplicate it.
 */
export function writeJson(payload: Record<string, unknown>): void {
    const body = { schemaVersion: CLI_SCHEMA_VERSION, ...payload };
    process.stdout.write("\n" + JSON_SENTINEL + "\n" + JSON.stringify(body) + "\n");
}
