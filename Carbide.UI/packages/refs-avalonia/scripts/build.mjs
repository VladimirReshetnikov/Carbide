// @carbide-ui/refs-avalonia build script — UI-M1.
// Downloads the pinned Avalonia nupkgs, verifies them, extracts the reference DLLs
// needed to compile Avalonia-referencing C# code, and writes refpack.json.
//
// Design mirrors src/Carbide/packages/refs-net10.0/scripts/build.mjs, extended to
// handle multiple source packages. Avalonia does not ship a separate `ref/` tree for
// most of its packages (only a tiny `ref/net10.0/Avalonia.dll` stub in the meta
// package), so this script pulls from `lib/net10.0/` and `lib/net10.0-browser1.0/`
// — the lib DLLs are the canonical compile-time metadata surface for Avalonia.
//
// Idempotent: if the ref tree and manifest already match the pinned version, the
// script exits early.

import { createHash } from "node:crypto";
import { readFile, writeFile, mkdir, readdir } from "node:fs/promises";
import { createWriteStream, existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { Readable } from "node:stream";
import { finished } from "node:stream/promises";

// Pinned Avalonia version. Any bump is a deliberate PR.
// Decided 2026-04-21 (proposal §13 item 2): Avalonia 12.x latest stable → 12.0.1.
const AVALONIA_VERSION = "12.0.1";

// Target TFM folder inside the ref tree. Avalonia.Browser itself lives under
// `lib/net10.0-browser1.0/`; cross-platform Avalonia DLLs under `lib/net10.0/` are
// byte-identical to their `net10.0-browser1.0/` variants for our purposes. We emit
// everything into `ref/net10.0-browser/` — the TFM a Carbide consumer compiles
// against when targeting the runner.
const REF_TFM = "net10.0-browser";

// Packages to extract from, in manifest order. Each entry lists the specific DLLs
// to pull from the package's `lib/<tfm>/` tree; anything else (analyzers, build
// tasks, designers, native runtimes) is filtered out per plan UI-I8.
const PACKAGES = [
    {
        id: "avalonia",
        sourceTfm: "net10.0",
        dlls: [
            "Avalonia.dll",
            "Avalonia.Base.dll",
            "Avalonia.Controls.dll",
            "Avalonia.Markup.dll",
            "Avalonia.Markup.Xaml.dll",
        ],
    },
    {
        id: "avalonia.browser",
        sourceTfm: "net10.0-browser1.0",
        dlls: ["Avalonia.Browser.dll"],
    },
    {
        id: "avalonia.markup.xaml.loader",
        sourceTfm: "net10.0",
        dlls: ["Avalonia.Markup.Xaml.Loader.dll"],
    },
    {
        id: "avalonia.themes.fluent",
        sourceTfm: "net10.0",
        dlls: ["Avalonia.Themes.Fluent.dll"],
    },
];

const HERE = path.dirname(fileURLToPath(import.meta.url));
const PKG_ROOT = path.resolve(HERE, "..");
const CACHE_DIR = path.join(PKG_ROOT, ".cache");
const REF_DIR = path.join(PKG_ROOT, "ref", REF_TFM);
const MANIFEST_PATH = path.join(PKG_ROOT, "refpack.json");

function sha256(buf) {
    return createHash("sha256").update(buf).digest("hex");
}

function nupkgUrl(id, version) {
    return `https://api.nuget.org/v3-flatcontainer/${id}/${version}/${id}.${version}.nupkg`;
}

function nupkgCachePath(id, version) {
    return path.join(CACHE_DIR, `${id}.${version}.nupkg`);
}

async function downloadIfMissing(id, version) {
    const cachePath = nupkgCachePath(id, version);
    if (existsSync(cachePath)) return cachePath;
    await mkdir(CACHE_DIR, { recursive: true });
    const url = nupkgUrl(id, version);
    console.log(`[refs-avalonia] downloading ${url}`);
    const resp = await fetch(url);
    if (!resp.ok) {
        throw new Error(`Failed to download ${url}: ${resp.status} ${resp.statusText}`);
    }
    const out = createWriteStream(cachePath);
    await finished(Readable.fromWeb(resp.body).pipe(out));
    return cachePath;
}

async function extractPackageDlls(pkg) {
    const cachePath = await downloadIfMissing(pkg.id, AVALONIA_VERSION);
    const nupkgBytes = await readFile(cachePath);
    const nupkgSha = sha256(nupkgBytes);
    const entries = parseZipEntries(nupkgBytes);
    const wanted = new Set(pkg.dlls.map((name) => `lib/${pkg.sourceTfm}/${name}`));
    const found = entries.filter((e) => wanted.has(e.name));
    if (found.length !== pkg.dlls.length) {
        const missing = pkg.dlls.filter(
            (name) => !found.some((e) => e.name === `lib/${pkg.sourceTfm}/${name}`),
        );
        throw new Error(
            `[refs-avalonia] ${pkg.id} ${AVALONIA_VERSION}: missing DLLs in lib/${pkg.sourceTfm}/: ${missing.join(", ")}`,
        );
    }
    const extracted = [];
    for (const entry of found) {
        const data = await extractZipEntry(nupkgBytes, entry);
        const dllName = path.posix.basename(entry.name);
        const destPath = path.join(REF_DIR, dllName);
        await writeFile(destPath, data);
        extracted.push({
            name: dllName,
            sha256: sha256(data),
            sizeBytes: data.length,
            sourceId: pkg.id,
        });
    }
    return {
        source: {
            id: pkg.id,
            version: AVALONIA_VERSION,
            url: nupkgUrl(pkg.id, AVALONIA_VERSION),
            sha256: nupkgSha,
            sourceTfm: pkg.sourceTfm,
        },
        dlls: extracted,
    };
}

async function buildRefPack() {
    await mkdir(REF_DIR, { recursive: true });
    const sources = [];
    const dlls = [];
    for (const pkg of PACKAGES) {
        const { source, dlls: pkgDlls } = await extractPackageDlls(pkg);
        sources.push(source);
        dlls.push(...pkgDlls);
    }
    dlls.sort((a, b) => a.name.localeCompare(b.name));
    const manifest = {
        schemaVersion: 1,
        avaloniaVersion: AVALONIA_VERSION,
        refDirectory: `ref/${REF_TFM}`,
        sources,
        dlls,
    };
    await writeFile(MANIFEST_PATH, JSON.stringify(manifest, null, 2) + "\n");
    const totalBytes = dlls.reduce((sum, d) => sum + d.sizeBytes, 0);
    const totalMB = (totalBytes / (1024 * 1024)).toFixed(2);
    console.log(
        `[refs-avalonia] extracted ${dlls.length} DLLs (${totalMB} MB); manifest at ${MANIFEST_PATH}`,
    );
}

// Minimal zip parser — same shape as @carbide/refs-net10.0's build.mjs.
// Reads the End-of-Central-Directory record, walks the central directory, extracts
// each file record's payload, and inflates when the compression method is Deflate.
function parseZipEntries(buf) {
    const view = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
    let eocdOffset = -1;
    const maxEocdOffset = Math.max(0, buf.length - 65557);
    for (let i = buf.length - 22; i >= maxEocdOffset; i--) {
        if (view.getUint32(i, true) === 0x06054b50) {
            eocdOffset = i;
            break;
        }
    }
    if (eocdOffset < 0) throw new Error("EOCD record not found in zip.");
    const cdEntries = view.getUint16(eocdOffset + 10, true);
    const cdOffset = view.getUint32(eocdOffset + 16, true);
    const entries = [];
    let p = cdOffset;
    for (let i = 0; i < cdEntries; i++) {
        if (view.getUint32(p, true) !== 0x02014b50) {
            throw new Error(`Central directory entry signature wrong at offset ${p}.`);
        }
        const method = view.getUint16(p + 10, true);
        const compressedSize = view.getUint32(p + 20, true);
        const uncompressedSize = view.getUint32(p + 24, true);
        const nameLen = view.getUint16(p + 28, true);
        const extraLen = view.getUint16(p + 30, true);
        const commentLen = view.getUint16(p + 32, true);
        const localHeaderOffset = view.getUint32(p + 42, true);
        const nameStart = p + 46;
        const name = new TextDecoder().decode(buf.subarray(nameStart, nameStart + nameLen));
        entries.push({ name, method, compressedSize, uncompressedSize, localHeaderOffset });
        p = nameStart + nameLen + extraLen + commentLen;
    }
    return entries;
}

async function extractZipEntry(buf, entry) {
    const view = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
    const lh = entry.localHeaderOffset;
    if (view.getUint32(lh, true) !== 0x04034b50) {
        throw new Error(`Local header signature wrong for ${entry.name}.`);
    }
    const method = view.getUint16(lh + 8, true);
    const nameLen = view.getUint16(lh + 26, true);
    const extraLen = view.getUint16(lh + 28, true);
    const dataStart = lh + 30 + nameLen + extraLen;
    const compressed = buf.subarray(dataStart, dataStart + entry.compressedSize);
    if (method === 0) return Buffer.from(compressed);
    if (method === 8) {
        const { inflateRaw } = await import("node:zlib");
        return await new Promise((resolve, reject) => {
            inflateRaw(compressed, (err, out) => (err ? reject(err) : resolve(out)));
        });
    }
    throw new Error(`Unsupported zip compression method ${method} for ${entry.name}.`);
}

async function manifestIsCurrent() {
    if (!existsSync(MANIFEST_PATH) || !existsSync(REF_DIR)) return false;
    try {
        const manifest = JSON.parse(await readFile(MANIFEST_PATH, "utf8"));
        if (manifest.avaloniaVersion !== AVALONIA_VERSION) return false;
        const files = new Set(await readdir(REF_DIR));
        for (const dll of manifest.dlls ?? []) {
            if (!files.has(dll.name)) return false;
        }
        return true;
    } catch {
        return false;
    }
}

async function main() {
    if (await manifestIsCurrent()) {
        console.log("[refs-avalonia] up to date");
        return;
    }
    await buildRefPack();
}

main().catch((err) => {
    console.error("[refs-avalonia] build failed:", err);
    process.exit(1);
});
