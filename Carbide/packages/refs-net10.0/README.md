# @carbide/refs-net10.0

Pre-extracted .NET 10 reference assemblies for Carbide's compile-time API surface. This package is the untrimmed "what the BCL looks like on the outside" companion to `@carbide/core`'s trimmed WASM runtime.

## What's inside

After `npm install` (or `npm run build`), the package contains:

```
ref/net10.0/             # The ref-pack DLLs, metadata-only.
ref-manifest.json        # { packageVersion, sourceNupkg, dlls: [{name, sha256, sizeBytes}] }
```

The DLLs come from Microsoft's `Microsoft.NETCore.App.Ref` nupkg on nuget.org. The build script verifies the nupkg and emits a per-DLL SHA256 so later loads can detect tampering.

## Why it exists

Carbide's WASM runtime ships trimmed BCL binaries for size. Trimming strips member signatures Carbide's own C# code didn't happen to use, which sometimes surfaces as CS0117 ("`Console.Write` missing") when user code compiles against the runtime DLLs. Shipping the untrimmed reference pack decouples the compile-time API surface from the runtime's trim decisions — Roslyn always sees the full documented API.

## Pinning

`scripts/build.mjs` pins a specific nupkg version (`10.0.0` initially). Upgrading is a deliberate PR:

1. Change `NUPKG_VERSION` in `build.mjs`.
2. Run `npm run build` to generate a fresh `ref-manifest.json`.
3. Compare the new hashes; commit the new manifest alongside the version bump.

## Licence and redistribution

`Microsoft.NETCore.App.Ref` is redistributed under the Microsoft .NET Library licence. The relevant notice is reproduced in [`THIRD_PARTY_NOTICES.md`](./THIRD_PARTY_NOTICES.md). Carbide itself is Apache-2.0; the terms of the reference-pack contents are governed by Microsoft's licence as reproduced.
