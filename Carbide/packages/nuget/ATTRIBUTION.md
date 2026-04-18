# Attribution

`@carbide/nuget` is an original implementation written against the public NuGet v3 HTTP
API documentation and the M6 architecture plan in `src/Carbide/docs/`. No third-party
source code has been copied into this package.

## Protocol reference

The flat-container endpoints consumed by `src/flat-container.ts` are defined in NuGet's
public v3 protocol documentation:

- Service index: `GET /v3/index.json` (not used directly — Carbide hard-codes the
  flat-container base URL).
- Package content list: `GET {flat-container}/{id-lower}/index.json`.
- Package download:     `GET {flat-container}/{id-lower}/{version}/{id-lower}.{version}.nupkg`.

See <https://learn.microsoft.com/en-us/nuget/api/overview> for the authoritative protocol
description.

## Design derivation

Per-file derivation from `src/Carbide/docs/carbide-M6-detailed-plan__*.md`:

| File                         | Plan section                                       |
|------------------------------|----------------------------------------------------|
| `src/tfm-compat.ts`          | §3 M6.2 — TFM ladder (net10.0 → netstandard2.0).   |
| `src/version-range.ts`       | §3 M6.3 — NuGet version-range subset, bare = ≥.    |
| `src/zip.ts`                 | §3 M6.5 — minimal zip reader, DEFLATE + stored.    |
| `src/nuspec.ts`              | §3 M6.5 — nuspec walker + `NuspecDependencyGroup`. |
| `src/safety.ts`              | §3 M6.8 + §5 D74 — native/targets/analyzers gates. |
| `src/allowlist.ts`           | §3 M6.9 + §5 D75 — 10 seed packages.               |
| `src/cache.ts`               | §3 M6.10 + §5 D72 — SHA-256 at write + read.       |
| `src/flat-container.ts`      | §3 M6.4 — flat-container client with cache.        |
| `src/lock.ts`                | §3 M6.11 — `carbide.lock.json`, schemaVersion=1.   |
| `src/resolver.ts`            | §3 M6.7 + §5 D67 — nearest-wins BFS walker.        |
| `src/warnings.ts`            | §5 D76 — MSNUGET0NN registry.                      |

## Licenses

This package is Apache-2.0, matching the rest of Carbide. It depends on no third-party
runtime packages (only Node's built-in `node:zlib`, `node:crypto`, `node:fs`, etc.).
