// M8 — read Webcil images so Roslyn can use them as compile-time metadata.
//
// With `<WasmEnableWebcil>true</WasmEnableWebcil>` the publish pipeline no longer emits
// `.dll` files at all: every managed assembly in `_framework/` becomes a `.wasm` file whose
// payload is a Webcil container. Webcil is not a PE — it replaces the PE/COFF headers with a
// compact header of its own — so `PEReader` rejects it and Carbide's runtime-assembly
// fallback (the path taken when no `@carbide/refs-*` pack is installed) would otherwise load
// zero references and fail every user compilation with CS0518.
//
// Roslyn only needs the ECMA-335 metadata root to build a reference; it does not need the
// surrounding PE. This reader locates that root inside a Webcil image and hands it to
// `ModuleMetadata.CreateFromMetadata`.
//
// Two container shapes are handled:
//   * a bare Webcil image, starting with the ASCII magic `WbIL`;
//   * a Webcil image wrapped in a WebAssembly module (what the SDK emits, so the asset is
//     served as `application/wasm` and survives proxies that block `.dll`). The payload sits
//     in a passive data segment; the wrapper also exports `webcilVersion` / `webcilSize` /
//     `getWebcilPayload`, but the segment scan is enough and does not depend on the
//     wrapper's code section staying byte-stable across SDK versions.
//
// Format reference: dotnet/runtime `src/tasks/Microsoft.NET.WebAssembly.Webcil`.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;

namespace Carbide.Core.Services;

internal static class WebcilReader
{
    private static ReadOnlySpan<byte> WasmMagic => "\0asm"u8;
    private static ReadOnlySpan<byte> WebcilMagic => "WbIL"u8;

    /// <summary>WASM section id for the data section (WebAssembly core spec §5.5.15).</summary>
    private const byte WasmDataSectionId = 11;

    /// <summary>`WebcilHeader` is 28 bytes: magic, two version shorts, section count, reserved, and two directory entries.</summary>
    private const int WebcilHeaderSize = 28;

    /// <summary>`WebcilSectionHeader` is four 32-bit fields: virtual size/address, raw size/pointer.</summary>
    private const int WebcilSectionHeaderSize = 16;

    public static bool LooksLikeWebcil(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8 && (bytes[..4].SequenceEqual(WebcilMagic) || bytes[..4].SequenceEqual(WasmMagic));

    /// <summary>
    /// Builds a Roslyn metadata reference from a Webcil image. Returns <c>null</c> when the
    /// bytes are not Webcil, or are Webcil that does not carry usable metadata (a resource-
    /// only or otherwise metadata-less module, which the PE path skips too).
    /// </summary>
    public static MetadataReference? TryCreateReference(byte[] bytes, string filePath)
    {
        var metadata = TryGetMetadata(bytes);
        if (metadata is null)
        {
            return null;
        }

        // Roslyn wants the metadata root at a stable native address for the lifetime of the
        // reference. These references live in the process-wide MetadataReferenceCache and are
        // never evicted, so a single unmanaged copy per assembly is the whole allocation —
        // bounded by the framework's assembly count and freed only at process exit.
        var segment = metadata.Value;
        var buffer = Marshal.AllocHGlobal(segment.Count);
        try
        {
            Marshal.Copy(segment.Array!, segment.Offset, buffer, segment.Count);
            var module = ModuleMetadata.CreateFromMetadata(buffer, segment.Count);
            return AssemblyMetadata.Create(module).GetReference(filePath: filePath);
        }
        catch (BadImageFormatException)
        {
            Marshal.FreeHGlobal(buffer);
            return null;
        }
    }

    /// <summary>Slices the ECMA-335 metadata root out of a Webcil image.</summary>
    private static ArraySegment<byte>? TryGetMetadata(byte[] bytes)
    {
        var webcil = TryGetWebcilPayload(bytes);
        if (webcil is null)
        {
            return null;
        }

        var image = (ReadOnlySpan<byte>)webcil.Value;
        if (image.Length < WebcilHeaderSize)
        {
            return null;
        }

        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(image[8..]);
        uint cliHeaderRva = BinaryPrimitives.ReadUInt32LittleEndian(image[12..]);
        int sectionTableEnd = WebcilHeaderSize + (sectionCount * WebcilSectionHeaderSize);
        if (sectionCount <= 0 || sectionTableEnd > image.Length)
        {
            return null;
        }

        // The CLI (COR20) header's second directory entry is the metadata root: its RVA sits
        // at offset 8 and its size at offset 12 (ECMA-335 II.25.3.3).
        int cliOffset = RvaToOffset(image, sectionCount, cliHeaderRva);
        if (cliOffset < 0 || cliOffset + 16 > image.Length)
        {
            return null;
        }

        uint metadataRva = BinaryPrimitives.ReadUInt32LittleEndian(image[(cliOffset + 8)..]);
        uint metadataSize = BinaryPrimitives.ReadUInt32LittleEndian(image[(cliOffset + 12)..]);
        int metadataOffset = RvaToOffset(image, sectionCount, metadataRva);
        if (metadataOffset < 0 || metadataSize == 0 || metadataOffset + metadataSize > (uint)image.Length)
        {
            return null;
        }

        return webcil.Value.Slice(metadataOffset, (int)metadataSize);
    }

    /// <summary>Maps a relative virtual address to an offset inside the Webcil image.</summary>
    private static int RvaToOffset(ReadOnlySpan<byte> image, int sectionCount, uint rva)
    {
        for (int i = 0; i < sectionCount; i++)
        {
            var entry = image[(WebcilHeaderSize + (i * WebcilSectionHeaderSize))..];
            int virtualSize = BinaryPrimitives.ReadInt32LittleEndian(entry);
            int virtualAddress = BinaryPrimitives.ReadInt32LittleEndian(entry[4..]);
            int rawDataPtr = BinaryPrimitives.ReadInt32LittleEndian(entry[12..]);
            if (rva >= (uint)virtualAddress && rva < (uint)(virtualAddress + virtualSize))
            {
                return rawDataPtr + (int)(rva - (uint)virtualAddress);
            }
        }
        return -1;
    }

    /// <summary>
    /// Returns the Webcil image: the input itself when it is already bare Webcil, or the
    /// payload extracted from the WebAssembly wrapper. <c>null</c> when neither shape matches.
    /// </summary>
    private static ArraySegment<byte>? TryGetWebcilPayload(byte[] bytes)
    {
        if (bytes.Length < 8)
        {
            return null;
        }
        var span = bytes.AsSpan();
        if (span[..4].SequenceEqual(WebcilMagic))
        {
            return new ArraySegment<byte>(bytes);
        }
        if (!span[..4].SequenceEqual(WasmMagic))
        {
            return null;
        }

        // Walk the module's section headers to the data section, then scan its segments for
        // the one whose payload carries the Webcil magic.
        int position = 8; // magic (4) + version (4)
        while (position < bytes.Length)
        {
            byte sectionId = bytes[position++];
            if (!TryReadUleb128(bytes, ref position, out uint sectionSize))
            {
                return null;
            }
            int sectionBody = position;
            if (sectionSize > (uint)(bytes.Length - sectionBody))
            {
                return null;
            }

            if (sectionId == WasmDataSectionId)
            {
                var payload = ScanDataSection(bytes, position, sectionBody + (int)sectionSize);
                if (payload is not null)
                {
                    return payload;
                }
            }

            position = sectionBody + (int)sectionSize;
        }
        return null;
    }

    private static ArraySegment<byte>? ScanDataSection(byte[] bytes, int position, int sectionEnd)
    {
        if (!TryReadUleb128(bytes, ref position, out uint segmentCount))
        {
            return null;
        }

        for (uint i = 0; i < segmentCount && position < sectionEnd; i++)
        {
            if (!TryReadUleb128(bytes, ref position, out uint flags))
            {
                return null;
            }
            // Flag bit 0 marks a passive segment (no offset expression); bit 1 marks an
            // explicit memory index. The SDK's wrapper emits passive segments, but handle the
            // active shapes too rather than assuming.
            if ((flags & 0x02) != 0 && !TryReadUleb128(bytes, ref position, out _))
            {
                return null;
            }
            if ((flags & 0x01) == 0 && !TrySkipConstantExpression(bytes, ref position, sectionEnd))
            {
                return null;
            }
            if (!TryReadUleb128(bytes, ref position, out uint length))
            {
                return null;
            }
            if (length > (uint)(sectionEnd - position))
            {
                return null;
            }

            if (length >= 4 && bytes.AsSpan(position, 4).SequenceEqual(WebcilMagic))
            {
                return new ArraySegment<byte>(bytes, position, (int)length);
            }
            position += (int)length;
        }
        return null;
    }

    /// <summary>Skips an `i32.const N end` initializer, the only constant expression the wrapper uses.</summary>
    private static bool TrySkipConstantExpression(byte[] bytes, ref int position, int limit)
    {
        if (position >= limit)
        {
            return false;
        }
        byte opcode = bytes[position++];
        if (opcode != 0x41 /* i32.const */)
        {
            return false;
        }
        if (!TryReadSleb128(bytes, ref position, limit))
        {
            return false;
        }
        return position < limit && bytes[position++] == 0x0B /* end */;
    }

    private static bool TryReadUleb128(byte[] bytes, ref int position, out uint value)
    {
        value = 0;
        int shift = 0;
        while (position < bytes.Length)
        {
            byte b = bytes[position++];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }
            shift += 7;
            if (shift > 28)
            {
                return false;
            }
        }
        return false;
    }

    private static bool TryReadSleb128(byte[] bytes, ref int position, int limit)
    {
        while (position < limit)
        {
            if ((bytes[position++] & 0x80) == 0)
            {
                return true;
            }
        }
        return false;
    }
}
