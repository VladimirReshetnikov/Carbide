# Carbide — milestone plans

Documentation in this directory is licensed under the repository's [Apache License 2.0](../../../../LICENSE), with copyright held collectively by Carbide Contributors.

- Created (UTC): 2026-04-19T01:36:09Z
- Updated (UTC): 2026-04-20T14:45:00Z
- Repository HEAD: 43db73bda

This directory groups the detailed implementation plans for individual Carbide milestones.

## Milestones

- [M1 — detailed plan (single-file parity with WasmSharp)](carbide-M1-detailed-plan__2026-04-17__23-14-49-240376.md)
- [M2 — detailed plan (multi-document)](carbide-M2-detailed-plan__2026-04-18__00-58-47-753934.md)
- [M3 — detailed plan (reference DLL injection + ref-pack)](carbide-M3-detailed-plan__2026-04-18__05-18-02-170097.md)
- [M4 — detailed plan (PE emission & CLI)](carbide-M4-detailed-plan__2026-04-18__19-45-17-979644.md)
- [M5 — detailed plan (project-file input)](carbide-M5-detailed-plan__2026-04-18__21-23-32-734397.md)
- [M6 — detailed plan (NuGet resolver)](carbide-M6-detailed-plan__2026-04-18__22-19-10-231651.md)
- [M9 — detailed plan (Shape S5: project-to-project references)](carbide-M9-detailed-plan__2026-04-18__23-18-54-749142.md)
- [U1 — detailed plan (CLI UX sharpening)](carbide-U1-detailed-plan__2026-04-19__06-00-00-000000.md)
- [U2 — detailed plan (program I/O forwarding)](carbide-U2-detailed-plan__2026-04-19__07-00-00-000000.md)
- [U3 — detailed plan (introspection + csproj polish)](carbide-U3-detailed-plan__2026-04-19__08-00-00-000000.md)
- [M11 — detailed plan (Partial MSBuild evaluator)](carbide-M11-detailed-plan__2026-04-19__09-00-00-000000.md)
- [T1 — detailed plan (streaming output + terminal session API)](carbide-T1-detailed-plan__2026-04-20__00-16-33-000000.md) — first phase of the [xterm.js interactive console plan](../carbide-xterm-interactive-console-plan__2026-04-19__23-34-41-000000.md).
- [T2 — detailed plan (cooperative async input + `CarbideConsole`)](carbide-T2-detailed-plan__2026-04-20__03-09-57-000000.md) — second phase of the [xterm.js interactive console plan](../carbide-xterm-interactive-console-plan__2026-04-19__23-34-41-000000.md).
- [T3 — detailed plan (forked `System.Console.dll`)](carbide-T3-detailed-plan__2026-04-20__13-56-27-000000.md) — third phase of the [xterm.js interactive console plan](../carbide-xterm-interactive-console-plan__2026-04-19__23-34-41-000000.md). **Implemented (base surface).** Replaces the stock `System.Console.dll` with a Carbide fork so pre-compiled NuGet libraries call stock `Console.ForegroundColor` / `Console.WindowWidth` / `Console.CancelKeyPress` etc. without code changes. Sync-block APIs (`Console.ReadKey`, `Console.GetCursorPosition`) remain PNS pending T3.1 (worker + SAB).
