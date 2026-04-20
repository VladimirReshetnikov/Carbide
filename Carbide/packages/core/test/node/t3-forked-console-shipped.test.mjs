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
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

const __dirname = dirname(fileURLToPath(import.meta.url));

test("shipped _framework/System.Console.dll is the Carbide T3 fork", () => {
    const dllPath = resolve(
        __dirname,
        "../../src/bin/Release/net10.0/publish/wwwroot/_framework/System.Console.dll",
    );
    const bytes = readFileSync(dllPath);

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
