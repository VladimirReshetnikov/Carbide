# Carbide — reports

- Created (UTC): 2026-04-19T01:36:09Z
- Updated (UTC): 2026-04-20T16:09:06Z
- Repository HEAD: 8c224d6b64e38c5ec1edf4792b0ccecb188e1c99

This directory holds project-local reports about Carbide's current behavior and usability.

## Reports

- [Carbide code review](carbide-code-review__2026-04-20__16-09-06-000000__201d0d99c75e.md) — source-level review of `src/Carbide`, grounded in the current docs and focused on correctness, safety, lifecycle contracts, and semantic drift.
- [Carbide usability report](Carbide-Usability-Report.md) — hands-on scenarios beyond the baseline tests, with limitations and follow-up proposals.
- [Feasibility: browser-hosted interactive C# console apps in Carbide via xterm.js](carbide-browser-xterm-console-feasibility__2026-04-19__22-01-41__06bf6d9b78c7.md) — feasibility analysis for an xterm.js-backed browser terminal runner, including the gap between current Carbide and broad `System.Console` compatibility.
- [Carbide xterm.js interactive console feasibility](carbide-xterm-interactive-console-feasibility__2026-04-19__21-55-15-000000.md) — what it would take to run C# console apps in the browser with an embedded xterm.js and conhost-like `System.Console` / ANSI behavior.
- [Feasibility: running major PE parsing/re-writing libraries on Carbide](carbide-pe-parsing-rewriting-library-feasibility__2026-04-19__23-40-00__eb35676f7575.md) — per-library analysis (Mono.Cecil, AsmResolver, dnlib, `System.Reflection.Metadata`), NuGet allow-list / safety-filter impact, Mono-WASM-dangerous API paths to route around, and the binary-output interop gap.
