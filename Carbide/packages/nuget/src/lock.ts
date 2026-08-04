// carbide.lock.json read/write. Shape per M6 §3 M6.11.

import { readFile, writeFile, mkdir } from "node:fs/promises";
import path from "node:path";
import type { ResolveLock, ResolvedPackage, Warning } from "./types.js";

export const LOCK_SCHEMA_VERSION = 1 as const;

export class LockReadError extends Error {
    constructor(message: string) {
        super(message);
        this.name = "LockReadError";
    }
}

/** Ordinal comparison. See the note in {@link buildLock} on why `localeCompare` is unusable. */
function compareOrdinal(a: string, b: string): number {
    return a < b ? -1 : a > b ? 1 : 0;
}

/**
 * Build the lock document. The output is byte-reproducible for a given graph, which is the
 * entire point of the artifact: it is committed to source control, so an identical resolve
 * must produce an identical file or every re-resolve shows up as a diff and hides the real
 * changes among the noise.
 *
 * Two things used to break that. Ordering ran through `localeCompare`, which both orders
 * differently from ordinal (punctuation and case are collated, so `a.b` and `ab` move relative
 * to capitalised ids) and varies with the host's locale data — so two developers could commit
 * different byte orderings of the same graph. And a `generatedAt` timestamp guaranteed a diff
 * on every run regardless of whether anything changed; no other ecosystem's lock file carries
 * one, nothing in Carbide reads it, and git already records when the file changed.
 * `ResolveLock.generatedAt` stays optional so previously written locks still parse.
 */
export function buildLock(packages: readonly ResolvedPackage[], warnings: readonly Warning[]): ResolveLock {
    // Deterministic output: sort by id then version, ordinally.
    const sorted = [...packages].sort((a, b) => {
        const cmp = compareOrdinal(a.id, b.id);
        if (cmp !== 0) return cmp;
        return compareOrdinal(a.version, b.version);
    });
    return {
        schemaVersion: LOCK_SCHEMA_VERSION,
        generator: "carbide",
        packages: sorted.map((p) => ({
            ...p,
            requestedBy: [...p.requestedBy].sort(),
            dependencies: [...p.dependencies].sort(),
        })),
        warnings: [...warnings],
    };
}

export async function writeLock(lockPath: string, lock: ResolveLock): Promise<void> {
    await mkdir(path.dirname(lockPath), { recursive: true });
    const content = JSON.stringify(lock, null, 2) + "\n";
    await writeFile(lockPath, content);
}

export async function readLock(lockPath: string): Promise<ResolveLock> {
    let raw: string;
    try {
        raw = await readFile(lockPath, "utf8");
    } catch (err) {
        throw new LockReadError(`Cannot read lock file '${lockPath}': ${(err as Error).message}`);
    }
    let parsed: ResolveLock;
    try {
        parsed = JSON.parse(raw) as ResolveLock;
    } catch (err) {
        throw new LockReadError(`Malformed lock file '${lockPath}': ${(err as Error).message}`);
    }
    if (parsed.schemaVersion !== LOCK_SCHEMA_VERSION) {
        throw new LockReadError(
            `Unsupported lock schemaVersion in '${lockPath}': expected ${LOCK_SCHEMA_VERSION}, got ${parsed.schemaVersion}.`,
        );
    }
    if (!Array.isArray(parsed.packages)) {
        throw new LockReadError(`Lock file '${lockPath}' has no packages array.`);
    }
    return parsed;
}
