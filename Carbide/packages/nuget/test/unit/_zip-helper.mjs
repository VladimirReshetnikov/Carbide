// Shared test helper: build a minimal store-only zip from a list of { name, content }.
// Matches the layout src/zip.ts's `listEntries` / `readEntry` expect:
//   local file header (30b + name + data) * N
//   central directory (46b + name) * N
//   end-of-central-directory (22b)
//
// Stored compression only (method 0) is sufficient — our tests produce tiny payloads
// where deflate would be net-pessimal anyway, and skipping deflate keeps the builder
// synchronous.

import { crc32 } from "node:zlib";
import { Buffer } from "node:buffer";

/**
 * @typedef {{ name: string; content: Uint8Array | Buffer | string }} ZipFile
 * @param {readonly ZipFile[]} files
 * @returns {Uint8Array}
 */
export function buildZip(files) {
    const localParts = [];
    const centralParts = [];
    let offset = 0;
    for (const file of files) {
        const nameBytes = Buffer.from(file.name, "utf-8");
        const contentBytes = Buffer.isBuffer(file.content)
            ? file.content
            : file.content instanceof Uint8Array
              ? Buffer.from(file.content)
              : Buffer.from(file.content, "utf-8");
        const crc = crc32(contentBytes);
        const size = contentBytes.length;

        // Local file header.
        const lh = Buffer.alloc(30 + nameBytes.length + size);
        lh.writeUInt32LE(0x04034b50, 0);   // signature
        lh.writeUInt16LE(20, 4);           // version needed to extract
        lh.writeUInt16LE(0, 6);            // general-purpose flags
        lh.writeUInt16LE(0, 8);            // compression method: stored
        lh.writeUInt16LE(0, 10);           // last-mod time
        lh.writeUInt16LE(0, 12);           // last-mod date
        lh.writeUInt32LE(crc, 14);         // crc-32
        lh.writeUInt32LE(size, 18);        // compressed size
        lh.writeUInt32LE(size, 22);        // uncompressed size
        lh.writeUInt16LE(nameBytes.length, 26);
        lh.writeUInt16LE(0, 28);           // extra-field length
        nameBytes.copy(lh, 30);
        contentBytes.copy(lh, 30 + nameBytes.length);

        // Central directory header.
        const ch = Buffer.alloc(46 + nameBytes.length);
        ch.writeUInt32LE(0x02014b50, 0);
        ch.writeUInt16LE(20, 4);           // version made by
        ch.writeUInt16LE(20, 6);           // version needed
        ch.writeUInt16LE(0, 8);
        ch.writeUInt16LE(0, 10);
        ch.writeUInt16LE(0, 12);
        ch.writeUInt16LE(0, 14);
        ch.writeUInt32LE(crc, 16);
        ch.writeUInt32LE(size, 20);
        ch.writeUInt32LE(size, 24);
        ch.writeUInt16LE(nameBytes.length, 28);
        ch.writeUInt16LE(0, 30);
        ch.writeUInt16LE(0, 32);           // file-comment length
        ch.writeUInt16LE(0, 34);           // disk number
        ch.writeUInt16LE(0, 36);           // internal attrs
        ch.writeUInt32LE(0, 38);           // external attrs
        ch.writeUInt32LE(offset, 42);      // local-header offset
        nameBytes.copy(ch, 46);

        localParts.push(lh);
        centralParts.push(ch);
        offset += lh.length;
    }

    const cdOffset = offset;
    const centralBuf = Buffer.concat(centralParts);
    const eocd = Buffer.alloc(22);
    eocd.writeUInt32LE(0x06054b50, 0);
    eocd.writeUInt16LE(0, 4);              // disk number
    eocd.writeUInt16LE(0, 6);              // disk with central dir
    eocd.writeUInt16LE(files.length, 8);   // entries on this disk
    eocd.writeUInt16LE(files.length, 10);  // total entries
    eocd.writeUInt32LE(centralBuf.length, 12);
    eocd.writeUInt32LE(cdOffset, 16);
    eocd.writeUInt16LE(0, 20);             // zip-comment length

    return new Uint8Array(Buffer.concat([...localParts, centralBuf, eocd]));
}
