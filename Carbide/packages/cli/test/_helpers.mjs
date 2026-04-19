export function parseJsonTrailer(stdout) {
    const text = Buffer.isBuffer(stdout) ? stdout.toString("utf8") : String(stdout ?? "");
    const lines = text.split(/\r?\n/);
    for (let i = lines.length - 1; i >= 0; i--) {
        const line = lines[i].trim();
        if (!line) continue;
        try {
            return JSON.parse(line);
        } catch {
            // Keep walking upward; JSON output is a trailer and may be preceded by other output.
        }
    }
    throw new Error(`No JSON trailer found in stdout (length=${text.length}).`);
}

/** The U1 sentinel line (without trailing newline). Keep in sync with `src/json-output.ts`. */
export const JSON_SENTINEL = "\x1E\x1Ecarbide-json\x1E\x1E";

/**
 * Parse a U1-framed JSON trailer by locating the sentinel line. Strict: throws if the
 * sentinel is not present. Use {@link parseJsonTrailer} for pre-U1 payloads.
 */
export function parseJsonBySentinel(stdout) {
    const text = Buffer.isBuffer(stdout) ? stdout.toString("utf8") : String(stdout ?? "");
    const lines = text.split(/\r?\n/);
    for (let i = lines.length - 1; i >= 0; i--) {
        if (lines[i] === JSON_SENTINEL && i + 1 < lines.length) {
            return JSON.parse(lines[i + 1]);
        }
    }
    throw new Error(`No Carbide JSON sentinel found in stdout (length=${text.length}).`);
}
