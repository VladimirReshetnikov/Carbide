# Carbide — design docs

Created (UTC): 2026-04-17T16:16:47Z
Repository HEAD: 39ff89aaa2868ccabeff078e3293c992bb57fb26

This directory holds the design material for **Carbide** — a working codename for a browser-and-Node C# build-and-run framework that targets environments without the .NET SDK. The name is tentative and can be changed without affecting the technical content.

## Documents

- [Carbide — vision](carbide-vision__2026-04-17__16-16-47-000000.md) — what Carbide is, who it's for, what it does and does not try to do, tiered goals, success criteria.
- [Carbide — architecture and implementation plan](carbide-architecture-and-implementation-plan__2026-04-17__16-16-47-000000.md) — layer model, runtime topology, JS/TS interfaces, Webcil handling, milestones, testing, supply chain.

## Milestone plans

- [M1 — detailed plan (single-file parity with WasmSharp)](carbide-M1-detailed-plan__2026-04-17__23-14-49-240376.md) — sub-milestones, file-by-file deliverables, explicit decisions, risks, and M1 non-goals.
- [M2 — detailed plan (multi-document)](carbide-M2-detailed-plan__2026-04-18__00-58-47-753934.md) — sub-milestones, public-API additions (`updateSource`, `removeSource`), diagnostic-attribution rules, fixture seed, and M2 non-goals.
- [M3 — detailed plan (reference DLL injection + ref-pack)](carbide-M3-detailed-plan__2026-04-18__05-18-02-170097.md) — sub-milestones, `ReferenceRegistry`, `session.addReference` / `project.addReference`, `@carbide/refs-net10.0` package, best-effort trim restoration, and M3 non-goals.

## Packages in this repository

- `packages/core/` — `@carbide/core`, the runtime/session/project surface.
- `packages/refs-net10.0/` — `@carbide/refs-net10.0`, the .NET 10 reference pack (`Microsoft.NETCore.App.Ref` → extracted `ref/net10.0/*.dll`). Opt-in sibling; when installed, Carbide's compile-time API surface is stable against runtime trim decisions.

## Operations

- [Drift tracking](drift/README.md) — periodic upstream-drift reports; also lists documented runtime differences vs. a local .NET CLI.

## Prior context

The feasibility analysis that precedes this work is the repository-level report [*Feasibility: building and running simple C# projects with only Python + Node.js*](../../../docs/reports/csharp-build-run-without-dotnet-sdk-feasibility__2026-04-17__01-02-58-000000.md). Carbide is the first concrete answer to the question that report posed: "what would a framework-shaped version of this look like, and how far up the tier ladder can we credibly go?"

## Status

Project initiated under `src/Carbide/`. These documents are the design contract the code in this project is expected to honour.
