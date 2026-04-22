// Cross-runtime base64 encoder for PE/PDB bytes (UI-M3). The launcher may run in Node
// (tests, Approach C CLI) or in a browser (production); both get the fastest local
// primitive without pulling in a dependency.

export function encodeBase64(bytes: Uint8Array): string {
    const nodeBuffer = (globalThis as {
        Buffer?: { from(b: Uint8Array): { toString(enc: "base64"): string } };
    }).Buffer;
    if (typeof nodeBuffer?.from === "function") {
        return nodeBuffer.from(bytes).toString("base64");
    }
    // Browser fallback: chunked String.fromCharCode + btoa. 0x8000-byte chunks keep the
    // call site under the browser's argument-count limit for apply().
    let binary = "";
    const chunkSize = 0x8000;
    for (let i = 0; i < bytes.length; i += chunkSize) {
        binary += String.fromCharCode(...bytes.subarray(i, Math.min(i + chunkSize, bytes.length)));
    }
    if (typeof btoa !== "function") {
        throw new Error("@carbide-ui/launcher: no base64 encoder available (neither Buffer.from nor btoa).");
    }
    return btoa(binary);
}
