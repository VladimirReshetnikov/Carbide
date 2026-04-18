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

export function buildLock(packages: readonly ResolvedPackage[], warnings: readonly Warning[]): ResolveLock {
    // Deterministic output: sort by id then version.
    const sorted = [...packages].sort((a, b) => {
        const cmp = a.id.localeCompare(b.id);
        if (cmp !== 0) return cmp;
        return a.version.localeCompare(b.version);
    });
    return {
        schemaVersion: LOCK_SCHEMA_VERSION,
        generator: "carbide",
        generatedAt: new Date().toISOString(),
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
