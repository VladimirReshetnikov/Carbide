# Carbide — planning docs

Documentation in this directory is licensed under the repository's [Apache License 2.0](../../../LICENSE), with copyright held collectively by Carbide Contributors.

- Created (UTC): 2026-04-19T01:36:09Z
- Updated (UTC): 2026-04-23T01:18:24Z
- Repository HEAD: c1e734191d4326d3f6501084f30cd30226374804

This directory holds Carbide's planning corpus: the project-wide architecture plan plus the milestone-by-milestone implementation plans that refine it.

## Documents

- [Carbide — architecture and implementation plan](carbide-architecture-and-implementation-plan__2026-04-17__16-16-47-000000.md) — the main architecture and roadmap document for Carbide.
- [Milestone planning index](milestones/README.md) — detailed plans for M1, M2, M3, M4, M5, M6, M9, M11, U1, U2, and U3.
- [Post-M9 usability remediation plan (U1–U3)](carbide-post-m9-usability-remediation-plan__2026-04-19__05-30-00-000000.md) — parallel phased work clearing the P0/P1/P2 backlog from the Usability Report. U-phases run alongside (not instead of) the architecture doc's M10–M13 Band C stretch milestones.
- [xterm.js interactive console plan (T1–T4)](carbide-xterm-interactive-console-plan__2026-04-19__23-34-41-000000.md) — implementation plan for the browser-hosted interactive terminal feature. Builds on the two feasibility reports in `../reports/`.
- [Carbide-pwsh Phase 1 plan](carbide-pwsh-phase1-detailed-plan__2026-04-21__21-45-00-000000__a5f8c3d192e0.md) — expression evaluator.
- [Carbide-pwsh Phase 2 plan](carbide-pwsh-phase2-detailed-plan__2026-04-21__22-30-00-000000__b7e2c4a9d018.md) — pipelines, VFS, curated cmdlet catalog.
- [Carbide-pwsh Phase 3 plan](carbide-pwsh-phase3-detailed-plan__2026-04-21__23-00-00-000000__f8c3e2a9b471.md) — control flow, functions, errors, scripts, apps, classes, enums, regex/format/join/split/containment operators.
- [Carbide-pwsh prompt-editor follow-up plan](carbide-pwsh-prompt-editor-follow-up-plan__2026-04-23__17-34-44__af24c9f7fbd6.md) — next-stage interactive prompt work after the lightweight editor landed, covering multi-line-aware history ownership, token-aware completion, richer history/search, edit-command expansion, and browser regression coverage.
- [Multishell virtual executable stubs plan](carbide-multishell-vfs-executable-stubs-detailed-plan__2026-04-23__01-18-24-060735__6d4f2a9b1c7e.md) — implementation plan and execution record for the path-based VFS executable catalog in `carbide-multishell`, covering shell-core dispatch, stub installation, command-family handlers, and validation.
- [Carbide.UI — Approach B implementation plan (UI-M0..UI-M6)](carbide-ui-avalonia-approach-b-plan__2026-04-21__23-40-46-000000__d3b1a638db2c.md) — cross-frame Avalonia runner plan for `@carbide-ui/*`. Implements the recommendation of the [Avalonia integration proposal](../proposals/carbide-ui-avalonia-integration-proposal__2026-04-18__22-04-08-231875__2bc4122b7f3f.md) §4.2 / §5.
- [Carbide.UI — Avalonia Playground plan (PG-P1..PG-P5)](carbide-ui-avalonia-playground-plan__2026-04-22__04-09-26-000000__ed144c5f75cf.md) — marquee demo plan built on the approach-B tree: Monaco editor + live Avalonia preview iframe, URL-shareable state, static-host deployment.
