// Measure the compressed tarball size of each @carbide-ui/* npm package.
// Runs `npm pack --dry-run --json` per package and compares the reported `size`
// (tarball bytes) against the UI-I2 budget from the plan's §2.
//
// Exit codes:
//   0 — all packages within budget.
//   1 — `npm pack` failed for one or more packages.
//   2 — one or more packages exceeded its budget.
//
// Usage (from Carbide.UI/):
//   node scripts/measure-sizes.mjs

import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { resolve, dirname } from "node:path";

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(HERE, "..");

const PACKAGES = [
    { dir: "packages/refs-avalonia",   budgetBytes:  2 * 1024 * 1024 },
    { dir: "packages/runtime-bundle",  budgetBytes: 35 * 1024 * 1024 },
    { dir: "packages/runner",          budgetBytes: 100 * 1024 },
    { dir: "packages/launcher",        budgetBytes:  50 * 1024 },
];

function formatBytes(n) {
    if (n >= 1024 * 1024) return (n / (1024 * 1024)).toFixed(3) + " MB";
    if (n >= 1024)        return (n / 1024).toFixed(1) + " KB";
    return n + " B";
}

let exitCode = 0;
for (const { dir, budgetBytes } of PACKAGES) {
    const full = resolve(ROOT, dir);
    const result = spawnSync("npm", ["pack", "--dry-run", "--json"], {
        cwd: full,
        encoding: "utf8",
        shell: process.platform === "win32",
    });
    if (result.status !== 0) {
        console.error(`[measure-sizes] npm pack failed in ${dir}:`);
        console.error(result.stderr);
        exitCode = Math.max(exitCode, 1);
        continue;
    }
    const parsed = JSON.parse(result.stdout);
    const entry = Array.isArray(parsed) ? parsed[0] : parsed;
    const sizeBytes = entry?.size ?? 0;
    const within = sizeBytes <= budgetBytes;
    const marker = within ? "OK  " : "OVER";
    const name = entry?.name ?? dir;
    console.log(
        `[${marker}] ${name}  ${formatBytes(sizeBytes)}  (budget ${formatBytes(budgetBytes)})`,
    );
    if (!within) exitCode = Math.max(exitCode, 2);
}
process.exit(exitCode);
