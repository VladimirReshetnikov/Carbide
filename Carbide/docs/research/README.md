# Carbide — research reports

- Created (UTC): 2026-04-19T00:47:03Z
- Updated (UTC): 2026-04-19T00:47:03Z
- Repository HEAD: d2f6eb2b29127011a7f7d713607bdfb4861c2b5f

This directory holds Carbide-specific research, feasibility reports, and independent verification reports that directly inform the project-local design documents in [`src/Carbide/docs/`](../README.md).

## Topics

### JS↔C# interop

- [JS↔C# WASM interop libraries survey](js-interop/carbide-wasm-js-interop-libraries-survey__2026-04-18__21-43-55-000000__b27d950cd3b9.md) — landscape survey of browser/Node.js JavaScript interop options for C# compiled by Carbide, with ClearScript-level ergonomics as the benchmark.
- [Independent verification of the Carbide WASM JS interop libraries survey](js-interop/carbide-wasm-js-interop-libraries-survey-verification__2026-04-19__00-10-31-940963__62239b6e3b7c.md) — source-audit and independent research pass over the survey's claims and recommendations.

### Avalonia UI integration

- [Feasibility: integrating `src/Carbide` and Avalonia UI for compiling and running GUI C# apps in a browser](avalonia-ui/carbide-avalonia-browser-gui-integration__2026-04-18__21-52-50-185670__98c4ace801fb.md) — feasibility analysis of a browser-hosted Avalonia GUI story next to Carbide.
- [Verification: `carbide-avalonia-browser-gui-integration`](avalonia-ui/carbide-avalonia-browser-gui-integration-verification__2026-04-19__00-19-02__6821d1cc24d2.md) — independent verification and correction pass over the feasibility report.
