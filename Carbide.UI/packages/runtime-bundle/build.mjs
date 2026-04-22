// @carbide-ui/avalonia-runtime-bundle build script — UI-M2.
//
// Publishes ../runner-dotnet/ in Release, copies the resulting _framework/
// tree into ./_framework/, and writes bundle-manifest.json recording the
// triple-pin (Avalonia, Carbide, .NET runtime) and per-file SHA256 roster.
//
// Runs at `npm run build`. Not wired to postinstall — the dotnet publish step
// requires the .NET 10 SDK + wasm-tools workload; publishing at install time
// would fail on consumer machines without them. Consumers install the committed
// _framework/ tree; the publish-from-source path is a maintainer workflow.

import { createHash } from "node:crypto";
import { spawnSync } from "node:child_process";
import {
    readFile,
    writeFile,
    mkdir,
    readdir,
    rm,
    stat,
    copyFile,
} from "node:fs/promises";
import { existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const AVALONIA_VERSION = "12.0.1";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const RUNNER_CSPROJ_DIR = path.resolve(HERE, "..", "runner-dotnet");
const OUT_FRAMEWORK_DIR = path.join(HERE, "_framework");
const OUT_SHELL_DIR = path.join(HERE, "shell");
const MANIFEST_PATH = path.join(HERE, "bundle-manifest.json");

function sha256(buf) {
    return createHash("sha256").update(buf).digest("hex");
}

function dotnetPublish() {
    console.log(`[runtime-bundle] dotnet publish ${RUNNER_CSPROJ_DIR}`);
    const result = spawnSync(
        "dotnet",
        ["publish", "-c", "Release", "--nologo", "-v", "minimal", RUNNER_CSPROJ_DIR],
        { stdio: "inherit", shell: process.platform === "win32" },
    );
    if (result.status !== 0) {
        throw new Error(`dotnet publish failed with exit code ${result.status}`);
    }
}

function publishFrameworkDir() {
    return path.join(
        RUNNER_CSPROJ_DIR,
        "bin",
        "Release",
        "net10.0-browser",
        "publish",
        "wwwroot",
        "_framework",
    );
}

function publishShellFiles() {
    const wwwroot = path.join(
        RUNNER_CSPROJ_DIR,
        "bin",
        "Release",
        "net10.0-browser",
        "publish",
        "wwwroot",
    );
    return [
        { src: path.join(wwwroot, "index.html"), dest: "index.html" },
        { src: path.join(wwwroot, "main.js"),    dest: "main.js"    },
    ];
}

async function readRuntimeVersion() {
    // Read from the runner's runtimeconfig.json, which records the included
    // Microsoft.NETCore.App version the project was published against.
    const runtimeconfig = path.join(
        RUNNER_CSPROJ_DIR,
        "bin",
        "Release",
        "net10.0-browser",
        "publish",
        "Avalonia.UI.Runner.runtimeconfig.json",
    );
    if (!existsSync(runtimeconfig)) return null;
    try {
        const parsed = JSON.parse(await readFile(runtimeconfig, "utf8"));
        const frameworks = parsed?.runtimeOptions?.includedFrameworks ?? [];
        const core = frameworks.find((f) => f.name === "Microsoft.NETCore.App");
        return core?.version ?? null;
    } catch {
        return null;
    }
}

async function copyTree(srcDir, destDir) {
    await mkdir(destDir, { recursive: true });
    const entries = await readdir(srcDir, { withFileTypes: true });
    for (const entry of entries) {
        const srcPath = path.join(srcDir, entry.name);
        const destPath = path.join(destDir, entry.name);
        if (entry.isDirectory()) {
            await copyTree(srcPath, destPath);
        } else if (entry.isFile()) {
            // Drop .gz siblings: modern browsers (99%+) accept Brotli, so .gz is redundant
            // precompression inside the tarball. Shipping raw + .br only keeps the npm
            // tarball under UI-I2 budget while still serving naive static hosts without
            // runtime compression middleware. Any host that genuinely needs gzip can
            // recompress from raw at deploy time.
            if (entry.name.endsWith(".gz")) continue;
            await copyFile(srcPath, destPath);
        }
    }
}

async function hashTree(dir) {
    const files = [];
    async function walk(subdir, prefix) {
        const entries = await readdir(subdir, { withFileTypes: true });
        for (const entry of entries) {
            const full = path.join(subdir, entry.name);
            const rel = prefix ? `${prefix}/${entry.name}` : entry.name;
            if (entry.isDirectory()) {
                await walk(full, rel);
            } else if (entry.isFile()) {
                const bytes = await readFile(full);
                files.push({
                    path: rel,
                    sizeBytes: bytes.length,
                    sha256: sha256(bytes),
                });
            }
        }
    }
    await walk(dir, "");
    files.sort((a, b) => a.path.localeCompare(b.path));
    return files;
}

async function buildBundle() {
    dotnetPublish();

    const srcFramework = publishFrameworkDir();
    if (!existsSync(srcFramework)) {
        throw new Error(
            `[runtime-bundle] expected publish output at ${srcFramework} but it is missing.`,
        );
    }

    // Wipe prior outputs to keep the manifest in sync with a clean copy.
    await rm(OUT_FRAMEWORK_DIR, { recursive: true, force: true });
    await rm(OUT_SHELL_DIR, { recursive: true, force: true });

    await copyTree(srcFramework, OUT_FRAMEWORK_DIR);

    await mkdir(OUT_SHELL_DIR, { recursive: true });
    for (const { src, dest } of publishShellFiles()) {
        if (!existsSync(src)) continue;
        await copyFile(src, path.join(OUT_SHELL_DIR, dest));
    }

    const frameworkFiles = await hashTree(OUT_FRAMEWORK_DIR);
    const shellFiles = existsSync(OUT_SHELL_DIR) ? await hashTree(OUT_SHELL_DIR) : [];

    const totalBytes = frameworkFiles.reduce((sum, f) => sum + f.sizeBytes, 0)
                     + shellFiles.reduce((sum, f) => sum + f.sizeBytes, 0);

    // Sum only preferred runtime transport (Brotli) + non-compressed assets.
    // This is what a modern browser downloads in a single cold load.
    const brotliBytes = frameworkFiles
        .filter((f) => f.path.endsWith(".br"))
        .reduce((sum, f) => sum + f.sizeBytes, 0);
    const brotliSources = new Set(
        frameworkFiles
            .filter((f) => f.path.endsWith(".br"))
            .map((f) => f.path.slice(0, -3)),
    );
    const nonPrecompressedBytes = frameworkFiles
        .filter((f) => !f.path.endsWith(".br") && !f.path.endsWith(".gz") && !brotliSources.has(f.path))
        .reduce((sum, f) => sum + f.sizeBytes, 0);
    const effectiveColdLoadBytes = brotliBytes + nonPrecompressedBytes;

    const runtimeVersion = await readRuntimeVersion();

    const manifest = {
        schemaVersion: 1,
        pinned: {
            avalonia: AVALONIA_VERSION,
            dotnet: runtimeVersion ?? "10.0.x",
            carbide: null, // Paired with @carbide/core at UI-M3 integration; null until wired.
        },
        sizeBytes: {
            totalOnDisk: totalBytes,
            effectiveColdLoadBrotli: effectiveColdLoadBytes,
        },
        framework: frameworkFiles,
        shell: shellFiles,
    };
    await writeFile(MANIFEST_PATH, JSON.stringify(manifest, null, 2) + "\n");

    const totalMB = (totalBytes / (1024 * 1024)).toFixed(2);
    const coldMB = (effectiveColdLoadBytes / (1024 * 1024)).toFixed(2);
    console.log(
        `[runtime-bundle] bundled ${frameworkFiles.length + shellFiles.length} files (${totalMB} MB on-disk; ${coldMB} MB effective cold-load)`,
    );
}

buildBundle().catch((err) => {
    console.error("[runtime-bundle] build failed:", err);
    process.exit(1);
});
