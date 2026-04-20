# Carbide — reports

- Created (UTC): 2026-04-19T01:36:09Z
- Updated (UTC): 2026-04-19T23:40:00Z
- Repository HEAD: f704d10238980f1fcfbda8577ba51a844919e068

This directory holds project-local reports about Carbide's current behavior and usability.

## Reports

- [Carbide usability report](Carbide-Usability-Report.md) — hands-on scenarios beyond the baseline tests, with limitations and follow-up proposals.
- [Feasibility: browser-hosted interactive C# console apps in Carbide via xterm.js](carbide-browser-xterm-console-feasibility__2026-04-19__22-01-41__06bf6d9b78c7.md) — feasibility analysis for an xterm.js-backed browser terminal runner, including the gap between current Carbide and broad `System.Console` compatibility.
- [Carbide xterm.js interactive console feasibility](carbide-xterm-interactive-console-feasibility__2026-04-19__21-55-15-000000.md) — what it would take to run C# console apps in the browser with an embedded xterm.js and conhost-like `System.Console` / ANSI behavior.
- [Feasibility: running major PE parsing/re-writing libraries on Carbide](carbide-pe-parsing-rewriting-library-feasibility__2026-04-19__23-40-00__eb35676f7575.md) — per-library analysis (Mono.Cecil, AsmResolver, dnlib, `System.Reflection.Metadata`), NuGet allow-list / safety-filter impact, Mono-WASM-dangerous API paths to route around, and the binary-output interop gap.
