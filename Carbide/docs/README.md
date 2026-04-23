# Carbide — design docs

Created (UTC): 2026-04-17T16:16:47Z
Updated (UTC): 2026-04-23T21:12:38Z
Repository HEAD: e5c1e2b48eea1534033dbf6bcd549b2059db91e7

This directory holds the design material for **Carbide** — a working codename for a browser-and-Node C# build-and-run framework that targets environments without the .NET SDK. The name is tentative and can be changed without affecting the technical content.

## Documents

- [Carbide current-state guide](Carbide-Current-State-Guide.md) — the authoritative current-state manual: project goals, scope, package map, architecture, feature matrix, build/test guidance, API and CLI usage, tutorial, limitations, and troubleshooting.
- [Carbide code review](reports/carbide-code-review__2026-04-20__16-09-06-000000__201d0d99c75e.md) — source-level review of `src/Carbide` after reading the current docs, with findings focused on correctness, safety, lifecycle behavior, and semantic drift.
- [carbide-pwsh repo PowerShell parse audit](reports/carbide-pwsh-repo-script-parse-audit__2026-04-23__02-59-32-0527852__9d3a7c1e4b62.md) — authoritative tracked-file audit of all repo PowerShell scripts against `pwsh.exe 7.6`, plus the mismatch catalog, the `carbide-pwsh` fixes that closed each gap, and the final `111 / 111` result.
- [carbide-pwsh external GitHub corpus parse audit](reports/carbide-pwsh-external-github-corpus-parse-audit__2026-04-23__06-20-22__c2d0b7a8f4e1.md) — large-scale audit against a `6070`-file GitHub corpus downloaded under `C:\TestData`, documenting the final corpus-driven parser fixes and the `0` remaining `pwsh-ok / carbide-fail` files.
- [Scope report: unifying Carbide shell endpoints around one shared session](reports/carbide-shell-endpoint-unification-scope-report__2026-04-23__21-12-38__9c7d7f44715e.md) — evaluates the work required to retire `carbide-multishell` as a separate browser endpoint while making `carbide-pwsh`, `carbide-cmd`, and `carbide-bash` all boot the same shared session, with emphasis on dependency-loading, prompt-UX, browser-manifest drift, and project-graph traps.
- [Content-identified virtual executable stubs for `carbide-multishell`](reports/carbide-multishell-content-identified-stubs-report__2026-04-23__00-08-04__f85c83692721.md) — evaluates the path-vs-content identity trade for multishell executable stubs, compares raw GUID, text-manifest, and pseudo-PE designs, and grounds the recommendation in shebang, `binfmt_misc`, BusyBox, Windows App Execution Aliases, and npm shim prior art.
- [Carbide T2.1 follow-up research report](reports/carbide-T21-follow-up-research-report__2026-04-20__18-20-09__6554172dc064.md) — reassessment of the browser interactive-async roadblock, including fresh local repros, upstream web research, git history, and updated recommendations.
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
- [Carbide-pwsh prompt-editor follow-up plan](planning/carbide-pwsh-prompt-editor-follow-up-plan__2026-04-23__17-34-44__af24c9f7fbd6.md) — next-stage plan for moving from the newly shipped lightweight prompt editor toward multiline-aware history, token-aware completion, richer search, and stronger browser validation.
- [Multishell virtual executable stubs plan](planning/carbide-multishell-vfs-executable-stubs-detailed-plan__2026-04-23__01-18-24-060735__6d4f2a9b1c7e.md) — implementation plan and execution record for the path-based executable catalog in `carbide-multishell`.

## Research reports

- [Research report index](research/README.md) — Carbide-specific feasibility studies, surveys, and independent verification reports organized by topic.
- [JS↔C# WASM interop libraries survey](research/js-interop/carbide-wasm-js-interop-libraries-survey__2026-04-18__21-43-55-000000__b27d950cd3b9.md) — landscape survey that motivates Carbide's richer JS-facing bridge direction.
- [Independent verification of the Carbide WASM JS interop libraries survey](research/js-interop/carbide-wasm-js-interop-libraries-survey-verification__2026-04-19__00-10-31-940963__62239b6e3b7c.md) — independent audit of the survey's claims and recommendations.
- [Feasibility: integrating `src/Carbide` and Avalonia UI for compiling and running GUI C# apps in a browser](research/avalonia-ui/carbide-avalonia-browser-gui-integration__2026-04-18__21-52-50-185670__57c69d8c45e3.md) — feasibility analysis for an Avalonia browser GUI story next to Carbide.
- [Verification: `carbide-avalonia-browser-gui-integration`](research/avalonia-ui/carbide-avalonia-browser-gui-integration-verification__2026-04-19__00-19-02__73b9556c6bb8.md) — independent verification and corrections for the Avalonia feasibility report.
- [Feasibility: running a useful `lib/pwsh` subset on Carbide in Node.js](research/powershell/carbide-powershell-subset-feasibility__2026-04-19__20-23-22-238572__8b6d83c519ba.md) — feasibility analysis for a PowerShell-derived automation subset on Carbide, with separate conclusions for runtime hosting and source-build feasibility.

## Feature proposals

- [JS↔C# interop bridge proposal](proposals/carbide-js-interop-bridge-proposal__2026-04-18__22-00-00-000000__a2d2955163d1.md) — design for an ES6-Proxy-based object-graph bridge over `[JSExport]` plus a compile-time surface manifest, targeting ClearScript-level ergonomics for user C# compiled in-sandbox. Introduces `@carbide/bridge` (TS) and `Carbide.Core.Bridge` (C#) as a peer of the existing control-plane `CompilationInterop`. Companion to the Carbide-local [JS↔C# WASM interop libraries survey](research/js-interop/carbide-wasm-js-interop-libraries-survey__2026-04-18__21-43-55-000000__b27d950cd3b9.md).
- [PowerShell-subset shell for Carbide + xterm.js](proposals/carbide-pwsh-subset-shell-proposal__2026-04-21__21-30-00-000000__e9c4b27a8f13.md) — proposal for a clean-room PowerShell-flavored shell with a VFS, cmdlet catalog, script execution, and Carbide-app invocation, hosted directly inside Carbide's browser/Node runtime.
- [Multi-shell (cmd + bash alongside pwsh) with cross-shell invocation](proposals/carbide-multi-shell-proposal__2026-04-21__23-30-00-000000__d9a71f3c5b68.md) — proposal for `carbide-cmd`, `carbide-bash`, and a shared `carbide-shell-core`, giving one Carbide session a pwsh/cmd/bash stack with shared VFS, env, and exit-code flow.
- [Virtual executable stubs for common `System32` and Git `usr/bin` tools in `carbide-multishell`](proposals/carbide-multishell-vfs-executable-stubs-proposal__2026-04-22__23-10-39-000000__6827e976e1d5.md) — proposal for a broader multishell tool catalog beyond shell stubs, including `robocopy.exe`, `grep.exe`, `sed.exe`, `awk.exe`, `findstr.exe`, `tar.exe`, search-path rules, command-name collision handling, and per-tool feature commitments.

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
