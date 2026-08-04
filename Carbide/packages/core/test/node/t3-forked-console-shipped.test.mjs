// T3 — confirm the forked System.Console.dll is the one shipped in `_framework/`.
//
// The overlay step in Carbide.Core.csproj replaces the stock System.Console.dll at publish
// time with the Carbide fork. If a future refactor breaks the overlay (wrong source path,
// MSBuild target name collision, trimming re-stripping the fork, etc.), user code that
// leans on stock `Console.ForegroundColor` / `Console.SetCursorPosition` / etc. will
// silently regress to PlatformNotSupportedException at runtime.
//
// We guard against that silently regressing by scanning the shipped DLL for a Carbide-
// specific marker constant (`CarbideForkedConsoleMarker.Marker`) that the stock BCL
// assembly cannot possibly contain. The constant is interned in the assembly's #US heap
// as a UTF-16LE string literal.

import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

const __dirname = dirname(fileURLToPath(import.meta.url));

// M8: with Webcil enabled the publish pipeline names every managed assembly `.wasm` and
// emits no `.dll`, so the overlay writes `System.Console.wasm`. The bytes are still a plain
// PE — Mono's loader dispatches on the image magic, not the extension — so the marker scan
// below is unchanged. Resolve whichever asset the current publish produced.
const frameworkDir = resolve(__dirname, "../../src/bin/Release/net10.0/publish/wwwroot/_framework");
const consoleAssetPath = [
    resolve(frameworkDir, "System.Console.wasm"),
    resolve(frameworkDir, "System.Console.dll"),
].find((candidate) => existsSync(candidate));

test("the shipped System.Console asset is the Carbide T3 fork", () => {
    assert.ok(
        consoleAssetPath,
        `Neither System.Console.wasm nor System.Console.dll exists in ${frameworkDir}. ` +
            "Run `dotnet publish -c Release src/Carbide.Core.csproj` from packages/core/.",
    );
    const dllPath = consoleAssetPath;
    const bytes = readFileSync(dllPath);

    // The overlay copies PE bytes regardless of the asset name, so the fork must never be a
    // Webcil image — if it were, Mono would still load it but this marker scan would be
    // scanning a different container than the one the fork was compiled into.
    assert.equal(
        bytes.subarray(0, 2).toString("latin1"),
        "MZ",
        `Expected the overlaid ${dllPath} to be a PE image; got magic ${bytes.subarray(0, 4).toString("hex")}.`,
    );

    // Marker constant declared in ConsolePal.Browser.cs. C#'s string literal compiles to a
    // UTF-16LE BlobHeap entry; scanning for the UTF-16LE encoding of the first 20 chars is
    // enough to distinguish the fork from the stock BCL DLL.
    const marker = "Carbide-forked System.Console.dll (T3)";
    const utf16le = Buffer.alloc(marker.length * 2);
    for (let i = 0; i < marker.length; i++) {
        utf16le.writeUInt16LE(marker.charCodeAt(i), i * 2);
    }

    const index = bytes.indexOf(utf16le);
    assert.notEqual(
        index,
        -1,
        `Expected Carbide fork marker ${JSON.stringify(marker)} not found in ${dllPath}.\n` +
            `DLL size: ${bytes.length} bytes.\n` +
            "Did the Carbide.Core publish overlay step run? Run `dotnet publish -c Release src/Carbide.Core.csproj` from packages/core/ to rebuild the overlay.",
    );
});
