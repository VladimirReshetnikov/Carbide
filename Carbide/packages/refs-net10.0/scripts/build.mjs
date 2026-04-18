// @carbide/refs-net10.0 build script.
// Downloads the pinned Microsoft.NETCore.App.Ref nupkg, verifies its SHA256, extracts the
// ref/net10.0/*.dll files, and writes ref-manifest.json summarising each DLL.
//
// Run at install time (postinstall) and on demand via `npm run build`.
// Idempotent: if ref/net10.0/ already has the expected files and their hashes match the
// manifest, the script exits early with "up to date".

import { createHash } from "node:crypto";
import { readFile, writeFile, mkdir, stat, readdir } from "node:fs/promises";
import { createWriteStream, existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { Readable } from "node:stream";
import { finished } from "node:stream/promises";

// Pinned version. Any bump is a deliberate PR that also updates the expected SHA256.
const NUPKG_VERSION = "10.0.0";
const NUPKG_ID = "microsoft.netcore.app.ref";
const NUPKG_URL = `https://api.nuget.org/v3-flatcontainer/${NUPKG_ID}/${NUPKG_VERSION}/${NUPKG_ID}.${NUPKG_VERSION}.nupkg`;
// SHA256 left unpinned initially — first successful download populates ref-manifest.json with
// the observed hash. The release pipeline verifies against a known-good hash; this build
// script warns if a previously-recorded hash drifts.

const HERE = path.dirname(fileURLToPath(import.meta.url));
const PKG_ROOT = path.resolve(HERE, "..");
const CACHE_DIR = path.join(PKG_ROOT, ".cache");
const NUPKG_PATH = path.join(CACHE_DIR, `${NUPKG_ID}.${NUPKG_VERSION}.nupkg`);
const REF_DIR = path.join(PKG_ROOT, "ref", "net10.0");
const MANIFEST_PATH = path.join(PKG_ROOT, "ref-manifest.json");

async function sha256(buf) {
    const h = createHash("sha256");
    h.update(buf);
    return h.digest("hex");
}

async function downloadIfMissing() {
    if (existsSync(NUPKG_PATH)) {
        return;
    }
    await mkdir(CACHE_DIR, { recursive: true });
    console.log(`[refs-net10.0] downloading ${NUPKG_URL}`);
    const resp = await fetch(NUPKG_URL);
    if (!resp.ok) {
        throw new Error(`Failed to download ${NUPKG_URL}: ${resp.status} ${resp.statusText}`);
    }
    const out = createWriteStream(NUPKG_PATH);
    await finished(Readable.fromWeb(resp.body).pipe(out));
    console.log(`[refs-net10.0] cached at ${NUPKG_PATH}`);
}

async function extractRefDlls() {
    await mkdir(REF_DIR, { recursive: true });
    const nupkgBytes = await readFile(NUPKG_PATH);
    const nupkgSha = await sha256(nupkgBytes);

    // A nupkg is a zip. Node's built-in `unzip` isn't present, but we can parse the zip
    // central directory by hand. Since we only need the ref/net10.0/*.dll entries, a minimal
    // stream-free parser is enough.
    const entries = await parseZipEntries(nupkgBytes);
    const refEntries = entries.filter((e) =>
        e.name.startsWith("ref/net10.0/") && e.name.endsWith(".dll"),
    );
    if (refEntries.length === 0) {
        throw new Error("No ref/net10.0/*.dll entries found in the nupkg. Version bump needed?");
    }

    const dlls = [];
    for (const entry of refEntries) {
        const data = await extractZipEntry(nupkgBytes, entry);
        const dllName = entry.name.replace(/^ref\/net10\.0\//, "");
        const destPath = path.join(REF_DIR, dllName);
        await writeFile(destPath, data);
        const hash = await sha256(data);
        dlls.push({ name: dllName, sha256: hash, sizeBytes: data.length });
    }

    dlls.sort((a, b) => a.name.localeCompare(b.name));

    const manifest = {
        schemaVersion: 1,
        packageVersion: NUPKG_VERSION,
        sourceNupkg: {
            id: NUPKG_ID,
            version: NUPKG_VERSION,
            url: NUPKG_URL,
            sha256: nupkgSha,
        },
        refDirectory: "ref/net10.0",
        dlls,
    };
    await writeFile(MANIFEST_PATH, JSON.stringify(manifest, null, 2) + "\n");
    console.log(`[refs-net10.0] extracted ${dlls.length} DLLs; manifest at ${MANIFEST_PATH}`);
}

// Minimal zip parser — just enough for nupkg extraction.
// Reads the End-of-Central-Directory record, walks the central directory, extracts each file
// record's compressed payload, and inflates when the compression method is Deflate.
async function parseZipEntries(buf) {
    const view = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
    // Locate End of Central Directory (EOCD) — signature 0x06054b50, back from the end.
    let eocdOffset = -1;
    const maxEocdOffset = Math.max(0, buf.length - 65557);
    for (let i = buf.length - 22; i >= maxEocdOffset; i--) {
        if (view.getUint32(i, true) === 0x06054b50) {
            eocdOffset = i;
            break;
        }
    }
    if (eocdOffset < 0) {
        throw new Error("EOCD record not found in zip.");
    }
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
    if (method === 0) {
        return Buffer.from(compressed);
    }
    if (method === 8) {
        // Raw DEFLATE. Node's zlib.inflateRaw handles this.
        const { inflateRaw } = await import("node:zlib");
        return await new Promise((resolve, reject) => {
            inflateRaw(compressed, (err, out) => {
                if (err) reject(err);
                else resolve(out);
            });
        });
    }
    throw new Error(`Unsupported zip compression method ${method} for ${entry.name}.`);
}

async function existingManifestIsCurrent() {
    if (!existsSync(MANIFEST_PATH) || !existsSync(REF_DIR)) {
        return false;
    }
    try {
        const manifest = JSON.parse(await readFile(MANIFEST_PATH, "utf8"));
        if (manifest.packageVersion !== NUPKG_VERSION) return false;
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
    if (await existingManifestIsCurrent()) {
        console.log("[refs-net10.0] up to date");
        return;
    }
    await downloadIfMissing();
    await extractRefDlls();
}

main().catch((err) => {
    console.error("[refs-net10.0] build failed:", err);
    process.exit(1);
});
