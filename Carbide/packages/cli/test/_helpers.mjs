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

