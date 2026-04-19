# Carbide — research reports

- Created (UTC): 2026-04-19T00:47:03Z
- Updated (UTC): 2026-04-19T01:46:55Z
- Repository HEAD: 826d84ca3a3a21c1b146dfaa851c962738631f75

This directory holds Carbide-specific research, feasibility reports, and independent verification reports that directly inform the project-local design documents in [`src/Carbide/docs/`](../README.md).

## Topics

### JS↔C# interop

- [JS↔C# WASM interop libraries survey](js-interop/carbide-wasm-js-interop-libraries-survey__2026-04-18__21-43-55-000000__b27d950cd3b9.md) — landscape survey of browser/Node.js JavaScript interop options for C# compiled by Carbide, with ClearScript-level ergonomics as the benchmark.
- [Independent verification of the Carbide WASM JS interop libraries survey](js-interop/carbide-wasm-js-interop-libraries-survey-verification__2026-04-19__00-10-31-940963__62239b6e3b7c.md) — source-audit and independent research pass over the survey's claims and recommendations.

### Avalonia UI integration

- [Feasibility: integrating `src/Carbide` and Avalonia UI for compiling and running GUI C# apps in a browser](avalonia-ui/carbide-avalonia-browser-gui-integration__2026-04-18__21-52-50-185670__57c69d8c45e3.md) — feasibility analysis of a browser-hosted Avalonia GUI story next to Carbide.
- [Verification: `carbide-avalonia-browser-gui-integration`](avalonia-ui/carbide-avalonia-browser-gui-integration-verification__2026-04-19__00-19-02__73b9556c6bb8.md) — independent verification and correction pass over the feasibility report.
