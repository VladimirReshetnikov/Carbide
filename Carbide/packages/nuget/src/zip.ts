// Minimal zip reader — just enough to walk a .nupkg's central directory and extract the
// DEFLATE-compressed entries we care about. Same shape as packages/refs-net10.0/scripts/
// build.mjs's internal walker; kept here so @carbide/nuget has zero runtime deps.
//
// Limitations (intentional, see M6 R55):
//   * ZIP64 (files > 4 GB, offsets > 4 GB, directory > 65535 entries) is not supported.
//   * Only stored (method 0) and DEFLATE (method 8) compression is decoded.

import { inflateRaw } from "node:zlib";
import { promisify } from "node:util";

const inflateRawAsync = promisify(inflateRaw);

export interface ZipEntry {
    name: string;
    compressedSize: number;
    uncompressedSize: number;
    localHeaderOffset: number;
    compressionMethod: number;
}

export class ZipParseError extends Error {
    constructor(message: string) {
        super(message);
        this.name = "ZipParseError";
    }
}

/** Parse the central directory of a zip buffer and return its entries. */
export function listEntries(buf: Uint8Array): ZipEntry[] {
    const view = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);

    // Locate End of Central Directory (EOCD) record. Signature: 0x06054b50.
    let eocdOffset = -1;
    const maxEocdOffset = Math.max(0, buf.length - 65557);
    for (let i = buf.length - 22; i >= maxEocdOffset; i--) {
        if (view.getUint32(i, true) === 0x06054b50) {
            eocdOffset = i;
            break;
        }
    }
    if (eocdOffset < 0) {
        throw new ZipParseError("EOCD record not found — not a zip file or ZIP64.");
    }
    const entryCount = view.getUint16(eocdOffset + 10, true);
    const cdOffset = view.getUint32(eocdOffset + 16, true);

    const entries: ZipEntry[] = [];
    let p = cdOffset;
    const decoder = new TextDecoder();
    for (let i = 0; i < entryCount; i++) {
        if (view.getUint32(p, true) !== 0x02014b50) {
            throw new ZipParseError(`Central directory entry signature mismatch at offset ${p}`);
        }
        const compressionMethod = view.getUint16(p + 10, true);
        const compressedSize = view.getUint32(p + 20, true);
        const uncompressedSize = view.getUint32(p + 24, true);
        const nameLen = view.getUint16(p + 28, true);
        const extraLen = view.getUint16(p + 30, true);
        const commentLen = view.getUint16(p + 32, true);
        const localHeaderOffset = view.getUint32(p + 42, true);
        const nameBytes = buf.subarray(p + 46, p + 46 + nameLen);
        const name = decoder.decode(nameBytes);
        entries.push({
            name,
            compressedSize,
            uncompressedSize,
            localHeaderOffset,
            compressionMethod,
        });
        p = p + 46 + nameLen + extraLen + commentLen;
    }
    return entries;
}

/** Extract one entry's decompressed bytes. */
export async function readEntry(buf: Uint8Array, entry: ZipEntry): Promise<Uint8Array> {
    const view = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
    const base = entry.localHeaderOffset;
    if (view.getUint32(base, true) !== 0x04034b50) {
        throw new ZipParseError(`Local file header signature mismatch for ${entry.name}`);
    }
    const nameLen = view.getUint16(base + 26, true);
    const extraLen = view.getUint16(base + 28, true);
    const dataStart = base + 30 + nameLen + extraLen;
    const compressed = buf.subarray(dataStart, dataStart + entry.compressedSize);

    if (entry.compressionMethod === 0) {
        // Stored — no compression.
        return Buffer.from(compressed);
    }
    if (entry.compressionMethod === 8) {
        const out = (await inflateRawAsync(Buffer.from(compressed))) as Buffer;
        return new Uint8Array(out);
    }
    throw new ZipParseError(
        `Unsupported zip compression method ${entry.compressionMethod} for ${entry.name}`,
    );
}
