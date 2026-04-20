# Carbide — design docs

Created (UTC): 2026-04-17T16:16:47Z
Updated (UTC): 2026-04-20T16:09:06Z
Repository HEAD: 8c224d6b64e38c5ec1edf4792b0ccecb188e1c99

This directory holds the design material for **Carbide** — a working codename for a browser-and-Node C# build-and-run framework that targets environments without the .NET SDK. The name is tentative and can be changed without affecting the technical content.

## Documents

- [Carbide current-state guide](Carbide-Current-State-Guide.md) — the authoritative current-state manual: project goals, scope, package map, architecture, feature matrix, build/test guidance, API and CLI usage, tutorial, limitations, and troubleshooting.
- [Carbide code review](reports/carbide-code-review__2026-04-20__16-09-06-000000__201d0d99c75e.md) — source-level review of `src/Carbide` after reading the current docs, with findings focused on correctness, safety, lifecycle behavior, and semantic drift.
- [Carbide usability report](reports/Carbide-Usability-Report.md) — hands-on scenarios beyond the baseline tests; notes usability, limitations, and follow-up proposals.
- [Feasibility: browser-hosted interactive C# console apps in Carbide via xterm.js](reports/carbide-browser-xterm-console-feasibility__2026-04-19__22-01-41__06bf6d9b78c7.md) — feasibility analysis for an xterm.js-backed browser terminal runner and the runtime work required for substantial `System.Console` parity.
- [Carbide — vision](carbide-vision__2026-04-17__16-16-47-000000.md) — what Carbide is, who it's for, what it does and does not try to do, tiered goals, success criteria.
- [Planning docs index](planning/README.md) — the architecture plan plus detailed milestone implementation plans.
- [Proposal index](proposals/README.md) — forward-looking design proposals built on Carbide's planning and research corpus.
- [Project-local reports index](reports/README.md) — Carbide-specific reports about current behavior and usability.
- [Archived drafts](archived/README.md) — superseded report drafts and earlier internal documentation snapshots retained for context.

## Planning docs

- [Carbide — architecture and implementation plan](planning/carbide-architecture-and-implementation-plan__2026-04-17__16-16-47-000000.md) — layer model, runtime topology, JS/TS interfaces, Webcil handling, milestones, testing, supply chain.
- [Milestone planning index](planning/milestones/README.md) — detailed plans for M1, M2, M3, M4, M5, M6, and M9.

## Research reports

- [Research report index](research/README.md) — Carbide-specific feasibility studies, surveys, and independent verification reports organized by topic.
- [JS↔C# WASM interop libraries survey](research/js-interop/carbide-wasm-js-interop-libraries-survey__2026-04-18__21-43-55-000000__b27d950cd3b9.md) — landscape survey that motivates Carbide's richer JS-facing bridge direction.
- [Independent verification of the Carbide WASM JS interop libraries survey](research/js-interop/carbide-wasm-js-interop-libraries-survey-verification__2026-04-19__00-10-31-940963__62239b6e3b7c.md) — independent audit of the survey's claims and recommendations.
- [Feasibility: integrating `src/Carbide` and Avalonia UI for compiling and running GUI C# apps in a browser](research/avalonia-ui/carbide-avalonia-browser-gui-integration__2026-04-18__21-52-50-185670__57c69d8c45e3.md) — feasibility analysis for an Avalonia browser GUI story next to Carbide.
- [Verification: `carbide-avalonia-browser-gui-integration`](research/avalonia-ui/carbide-avalonia-browser-gui-integration-verification__2026-04-19__00-19-02__73b9556c6bb8.md) — independent verification and corrections for the Avalonia feasibility report.
- [Feasibility: running a useful `lib/pwsh` subset on Carbide in Node.js](research/powershell/carbide-powershell-subset-feasibility__2026-04-19__20-23-22-238572__8b6d83c519ba.md) — feasibility analysis for a PowerShell-derived automation subset on Carbide, with separate conclusions for runtime hosting and source-build feasibility.

## Feature proposals

- [JS↔C# interop bridge proposal](proposals/carbide-js-interop-bridge-proposal__2026-04-18__22-00-00-000000__a2d2955163d1.md) — design for an ES6-Proxy-based object-graph bridge over `[JSExport]` plus a compile-time surface manifest, targeting ClearScript-level ergonomics for user C# compiled in-sandbox. Introduces `@carbide/bridge` (TS) and `Carbide.Core.Bridge` (C#) as a peer of the existing control-plane `CompilationInterop`. Companion to the Carbide-local [JS↔C# WASM interop libraries survey](research/js-interop/carbide-wasm-js-interop-libraries-survey__2026-04-18__21-43-55-000000__b27d950cd3b9.md).

## Companion-project proposals

- [`Carbide.UI` / `@carbide-ui/*` — Avalonia GUI integration proposal](proposals/carbide-ui-avalonia-integration-proposal__2026-04-18__22-04-08-231875__2bc4122b7f3f.md) — three approaches compared (merged runtime / cross-frame runner / offline CLI), commits to cross-frame runner + offline CLI as concurrent delivery, specifies package layout, `postMessage` protocol, XAML strategy, and UI-M0..UI-M8 milestones. Paired with the Carbide-local [feasibility report](research/avalonia-ui/carbide-avalonia-browser-gui-integration__2026-04-18__21-52-50-185670__57c69d8c45e3.md).

## Packages in this repository

- `packages/core/` — `@carbide/core`, the runtime/session/project surface.
- `packages/refs-net10.0/` — `@carbide/refs-net10.0`, the .NET 10 reference pack (`Microsoft.NETCore.App.Ref` → extracted `ref/net10.0/*.dll`). Opt-in sibling; when installed, Carbide's compile-time API surface is stable against runtime trim decisions.
- `packages/cli/` — `@carbide/cli`, the `carbide` binary wrapping `@carbide/core` with `build` / `run` / `validate` subcommands.
- `packages/msbuild-lite/` — `@carbide/msbuild-lite`, a bounded `.csproj` parser (semantic port of `cs_kit.msbuild_lite`). Consumed by the CLI's `--project` path.
- `packages/nuget/` — `@carbide/nuget`, a bounded NuGet v3 resolver (flat-container client, nupkg/nuspec reader, TFM-compat matrix, allow-list, filesystem cache, `carbide.lock.json` read/write). Consumed by the CLI when a `.csproj` declares `<PackageReference>`s.

## Operations

- [Drift tracking](drift/README.md) — periodic upstream-drift reports; also lists documented runtime differences vs. a local .NET CLI.

## Prior context

The feasibility analysis that precedes this work is the repository-level report [*Feasibility: building and running simple C# projects with only Python + Node.js*](../../../docs/reports/csharp-build-run-without-dotnet-sdk-feasibility__2026-04-17__01-02-58-000000.md). Carbide is the first concrete answer to the question that report posed: "what would a framework-shaped version of this look like, and how far up the tier ladder can we credibly go?"

## Status

Project initiated under `src/Carbide/`. These documents are the design contract the code in this project is expected to honour.
