# Third-party notices

The Carbide-authored build orchestration and runner shell in this package are licensed under Apache-2.0. The generated `_framework/` bundle redistributes the following upstream components under their own terms.

## Avalonia 12.0.1

Avalonia framework assemblies are MIT-licensed by AvaloniaUI OÜ. The exact upstream license is reproduced in [`third-party/avalonia/LICENSE.md`](third-party/avalonia/LICENSE.md).

## SkiaSharp and HarfBuzzSharp WebAssembly native assets

Avalonia.Browser 12.0.1 brings in `SkiaSharp.NativeAssets.WebAssembly` 3.119.3-preview.1.1 and `HarfBuzzSharp.NativeAssets.WebAssembly` 8.3.1.3. Their MIT wrapper license is reproduced in [`third-party/skiasharp-harfbuzzsharp/LICENSE.txt`](third-party/skiasharp-harfbuzzsharp/LICENSE.txt).

The native payload contains Skia, HarfBuzz, ANGLE, zlib, and other components under their respective permissive licenses. The complete upstream notice set shipped identically by both NativeAssets packages is reproduced once in [`third-party/skiasharp-harfbuzzsharp/THIRD-PARTY-NOTICES.txt`](third-party/skiasharp-harfbuzzsharp/THIRD-PARTY-NOTICES.txt).

## .NET Runtime / Mono WebAssembly 10.0.6

The .NET runtime payload is MIT-licensed by the .NET Foundation and Contributors. Its exact [`LICENSE.TXT`](third-party/dotnet/LICENSE.TXT) and complete [`THIRD-PARTY-NOTICES.TXT`](third-party/dotnet/THIRD-PARTY-NOTICES.TXT) are included with this package.

## MicroCom.Runtime 0.11.4

Avalonia's runtime dependency graph includes `MicroCom.Runtime` 0.11.4, MIT-licensed by Nikita Tsukanov. The exact license from the [pinned upstream commit](https://github.com/kekekeks/MicroCom/tree/28850f85fb586488828ab7267ed4a87e8c970b51) is included as [`third-party/microcom/LICENSE`](third-party/microcom/LICENSE).

These notices apply only to the identified upstream material and do not alter the Apache-2.0 license of Carbide.UI's own files.
