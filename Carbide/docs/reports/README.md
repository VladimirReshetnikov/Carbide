# Carbide — reports

- Created (UTC): 2026-04-19T01:36:09Z
- Updated (UTC): 2026-04-20T18:23:55Z
- Repository HEAD: e4e53b7638153472a3db0e2115b9345cb6e84283

This directory holds project-local reports about Carbide's current behavior and usability.

## Reports

- [Carbide T2.1 — investigation report ("Cannot wait on monitors" on awaits that suspend)](carbide-T21-investigation-report__2026-04-20__17-11-37-000000.md) — what T2.1 actually is (a Mono-WASM single-threaded runtime limitation, not a Carbide bug), which theories were falsified, and five real options for a fix. Accompanied by [`artifacts/carbide-gh-T21-artifact/`](artifacts/carbide-gh-T21-artifact/README.md).
- [Carbide T2.1 follow-up research report](carbide-T21-follow-up-research-report__2026-04-20__18-20-09__6554172dc064.md) — reassessment of the original T2.1 conclusion after code review, git-history review, fresh local reproduction, and upstream .NET prior-art research; includes a separate git-history section and revised recommendations.
- [Carbide code review](carbide-code-review__2026-04-20__16-09-06-000000__201d0d99c75e.md) — source-level review of `src/Carbide`, grounded in the current docs and focused on correctness, safety, lifecycle contracts, and semantic drift.
- [Carbide usability report](Carbide-Usability-Report.md) — hands-on scenarios beyond the baseline tests, with limitations and follow-up proposals.
- [Feasibility: browser-hosted interactive C# console apps in Carbide via xterm.js](carbide-browser-xterm-console-feasibility__2026-04-19__22-01-41__06bf6d9b78c7.md) — feasibility analysis for an xterm.js-backed browser terminal runner, including the gap between current Carbide and broad `System.Console` compatibility.
- [Carbide xterm.js interactive console feasibility](carbide-xterm-interactive-console-feasibility__2026-04-19__21-55-15-000000.md) — what it would take to run C# console apps in the browser with an embedded xterm.js and conhost-like `System.Console` / ANSI behavior.
- [Feasibility: running major PE parsing/re-writing libraries on Carbide](carbide-pe-parsing-rewriting-library-feasibility__2026-04-19__23-40-00__eb35676f7575.md) — per-library analysis (Mono.Cecil, AsmResolver, dnlib, `System.Reflection.Metadata`), NuGet allow-list / safety-filter impact, Mono-WASM-dangerous API paths to route around, and the binary-output interop gap.

## Artifacts

- [`artifacts/carbide-gh-T21-artifact/`](artifacts/carbide-gh-T21-artifact/README.md) — the in-browser Spectre.Console + GitHub REPL that motivated the T2.1 investigation. Not a working demo; preserved as reference material for the T2.1 report.
