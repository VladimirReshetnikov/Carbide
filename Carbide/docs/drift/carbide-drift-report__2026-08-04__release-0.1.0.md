# Carbide drift report — 2026-08-04 (release 0.1.0)

Documentation in this directory is licensed under the repository's [Apache License 2.0](../../../LICENSE), with copyright held collectively by Carbide Contributors.

- Created (UTC): 2026-08-04
- Trigger: first report, filed at the 0.1.0 release per [architecture §11](../planning/carbide-architecture-and-implementation-plan__2026-04-17__16-16-47-000000.md#11-supply-chain-and-maintenance)
- Scope: the pinned versions Carbide compiles and ships against, checked against what upstream has released

## Summary

One finding was release-blocking and is fixed: Carbide shipped **preview** builds of two
`Microsoft.Extensions.*` packages inside `@carbide/core`'s `_framework` payload. They are now
pinned to stable `10.0.6`.

The remaining findings are informational. Roslyn has moved to a new major version (`5.x`)
while Carbide pins `4.14.0`; that is a deliberate hold, not an oversight, and evaluating the
jump is post-0.1.0 work.

## Pinned versions vs. upstream

| Component | Carbide pin | Newest upstream (2026-08-04) | Status |
| --- | --- | --- | --- |
| .NET SDK | `10.0.201` (`global.json`, `rollForward: disable`) | — | **Held deliberately.** The pin fixes the Mono WebAssembly runtime at `10.0.6` so the bundled upstream notices stay version-accurate. Changing it means re-vendoring the runtime notices. |
| `Microsoft.CodeAnalysis.CSharp` / `.Features` | `4.14.0` | `5.6.0` | **Drift, held.** `4.14.0` is the last `4.x`. See "Roslyn 5.x" below. |
| `Microsoft.Extensions.Logging.Abstractions` | `10.0.0-preview.5.25277.114` → **`10.0.6`** | `10.0.10` | **Fixed in this release.** See below. |
| `Microsoft.Extensions.DependencyInjection.Abstractions` (transitive) | preview → **`10.0.6`** | `10.0.10` | **Fixed in this release.** |
| `Jab` | `0.11.0` | — | Compile-time only (`PrivateAssets="all"`); nothing ships. |
| `wasm-tools` workload | `10.0.200-manifests.*` on SDK `10.0.201` | — | Follows the SDK pin. |

## Finding 1 — preview packages were shipping (fixed)

`Carbide.Core.csproj` pinned `Microsoft.Extensions.Logging.Abstractions` to
`10.0.0-preview.5.25277.114`, dating from before .NET 10 went GA. Both it and the
`Microsoft.Extensions.DependencyInjection.Abstractions` it pulls in are emitted as
assemblies into `bin/Release/net10.0/publish/wwwroot/_framework/`, which is part of
`@carbide/core`'s published tarball — so a `0.1.0` install would have carried preview builds
of a released library.

**Resolution.** Pinned to `10.0.6`, matching the servicing band of the Mono-WASM runtime the
SDK pin fixes, rather than the newest `10.0.10`, so every vendored notice in the tarball
describes the same band. The stable packages declare MIT as an SPDX expression and no longer
bundle a license file; MIT still requires the license text to travel with redistributed
binaries, so the canonical .NET MIT text is retained alongside the packages' verbatim
`THIRD-PARTY-NOTICES.TXT`. Both packages ship byte-identical notices, so one vendored copy
covers both, as before.

The vendored payload moved from `third-party/dotnet-extensions-preview/` to
`third-party/dotnet-extensions/`, and the hash pins in `scripts/check-licenses.mjs` were
updated (`cde1f578…` → `6d15e10a…` for the notices; the MIT text is unchanged at
`d7a68596…`).

**Regression guard.** `scripts/check-publish.mjs` now fails on any prerelease
`PackageReference` in a project whose output ships. This class of problem is invisible to
every npm-level check — the version lives in a `.csproj`, and the evidence is a DLL buried in
a 292-file payload.

## Finding 2 — Roslyn 5.x is available; Carbide holds at 4.14.0

`Microsoft.CodeAnalysis.CSharp` has released a `5.x` line (`5.6.0` as of 2026-07-02).
Carbide pins `4.14.0`, which is the newest `4.x`.

This is a hold, not an oversight. A Roslyn major-version bump touches the exact surfaces
Carbide is most exposed on — `MetadataReference` resolution, the `AdhocWorkspace` lifecycle,
emit options, and diagnostic identity — and Carbide runs Roslyn on Mono-WASM, a
configuration upstream does not test. Doing it in the same change as the first published
release would put the release's credibility on an untested combination.

**Recommendation for 0.2.0.** Evaluate the jump as its own workstream: read the `5.x`
breaking-change notes, bump in a branch, and require the full matrix (core Node, core
browser, CLI, launcher) plus the API-surface and wire-contract gates to be green. The gates
added in M7 make this materially safer than it would have been before — a Roslyn upgrade
that changes diagnostic shapes or the emitted surface now shows up as a reviewable diff
rather than as a silent behaviour change.

## Not observed

- No new `dotnet/runtime` or `dotnet/roslyn` WASM/browser issue was found that affects the
  paths Carbide depends on beyond the ones already tracked in this directory (notably the
  T2.1 async-scheduler limitation and the fd-layer capture bypass recorded under P0.1).
- No change to the `wasm-tools` workload manifest beyond what the SDK pin selects.

## Next report

Due when any of the pinned versions above is changed, or at the next release, whichever
comes first.
