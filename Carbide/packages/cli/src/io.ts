// File I/O helpers shared across CLI commands.

import { readFile, writeFile, mkdir } from "node:fs/promises";
import path from "node:path";

/** Read a source file. `-` means stdin (slurped to EOF as UTF-8). */
export async function readSource(spec: string): Promise<{ path: string; code: string }> {
    if (spec === "-") {
        const code = await readStdin();
        return { path: "<stdin>.cs", code };
    }
    const code = await readFile(spec, "utf8");
    // Use the basename as the logical document path so diagnostics are short; the caller
    // can override by passing two --source with the same basename (which will fail at
    // addSource's duplicate-path guard and surface a clear error).
    return { path: path.basename(spec), code };
}

/** Read a reference DLL's bytes. */
export async function readReferenceBytes(spec: string): Promise<{ name: string; bytes: Uint8Array }> {
    const buf = await readFile(spec);
    const name = path.basename(spec, path.extname(spec));
    return { name, bytes: new Uint8Array(buf) };
}

/** Write a file, creating the parent directory as needed. */
export async function writeFileEnsuringDir(filePath: string, contents: Uint8Array): Promise<void> {
    await mkdir(path.dirname(filePath), { recursive: true });
    await writeFile(filePath, contents);
}

/** Read stdin to EOF as UTF-8. */
export async function readStdin(): Promise<string> {
    const chunks: Buffer[] = [];
    for await (const chunk of process.stdin) {
        chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk));
    }
    return Buffer.concat(chunks).toString("utf8");
}

/**
 * Derives an AssemblyName from CLI inputs. Uses --assembly-name when supplied, otherwise the
 * first source's basename without extension. Falls back to "CarbideApp".
 */
export function deriveAssemblyName(
    explicit: string | undefined,
    sources: readonly string[],
): string {
    if (explicit && explicit.length > 0) return explicit;
    if (sources.length > 0 && sources[0] !== "-") {
        const base = path.basename(sources[0], path.extname(sources[0]));
        if (base.length > 0) return base;
    }
    return "CarbideApp";
}
