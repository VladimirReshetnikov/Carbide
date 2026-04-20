# Carbide — research reports

- Created (UTC): 2026-04-19T00:47:03Z
- Updated (UTC): 2026-04-20T01:25:40Z
- Repository HEAD: f704d10238980f1fcfbda8577ba51a844919e068

This directory holds Carbide-specific research, feasibility reports, and independent verification reports that directly inform the project-local design documents in [`src/Carbide/docs/`](../README.md).

## Topics

### JS↔C# interop

- [JS↔C# WASM interop libraries survey](js-interop/carbide-wasm-js-interop-libraries-survey__2026-04-18__21-43-55-000000__b27d950cd3b9.md) — landscape survey of browser/Node.js JavaScript interop options for C# compiled by Carbide, with ClearScript-level ergonomics as the benchmark.
- [Independent verification of the Carbide WASM JS interop libraries survey](js-interop/carbide-wasm-js-interop-libraries-survey-verification__2026-04-19__00-10-31-940963__62239b6e3b7c.md) — source-audit and independent research pass over the survey's claims and recommendations.

### Avalonia UI integration

- [Feasibility: integrating `src/Carbide` and Avalonia UI for compiling and running GUI C# apps in a browser](avalonia-ui/carbide-avalonia-browser-gui-integration__2026-04-18__21-52-50-185670__57c69d8c45e3.md) — feasibility analysis of a browser-hosted Avalonia GUI story next to Carbide.
- [Verification: `carbide-avalonia-browser-gui-integration`](avalonia-ui/carbide-avalonia-browser-gui-integration-verification__2026-04-19__00-19-02__73b9556c6bb8.md) — independent verification and correction pass over the feasibility report.

### PowerShell on Carbide

- [Feasibility: running a useful `lib/pwsh` subset on Carbide in Node.js](powershell/carbide-powershell-subset-feasibility__2026-04-19__20-23-22-238572__8b6d83c519ba.md) — feasibility analysis of a PowerShell-derived scripting subset hosted by Carbide, including separate verdicts for runtime-hosting and source-build-on-Carbide.
- [Feasibility: forking a useful PowerShell subset from `lib/pwsh` to run under Carbide on Node.js](pwsh/carbide-pwsh-subset-feasibility__2026-04-19__10-30-00-000000__a7c3d4e9f1b2.md) — scope, dependency analysis, risk assessment, and tiered effort estimate for an in-process PowerShell evaluator built on top of Carbide M9 + M11. **Revised 2026-04-19T21:00Z** after review of the independent report above; see §14 for the deltas (compile-site count, cmdlet locations, threading model, persistent-session host requirement, and a revised effort estimate).

### PE parsing and rewriting libraries on Carbide

- [Feasibility: running major PE parsing and rewriting libraries on `src/Carbide`](pe-libraries/carbide-pe-parsing-rewriting-libraries-feasibility__2026-04-20__01-07-50-000000__272fdc1dd683.md) — evidence-backed evaluation of Mono.Cecil, AsmResolver, dnlib, and the lower-level SRM fallback on Carbide, with separate verdicts for direct DLL references, current `PackageReference` ingestion, and source-tree buildability.
