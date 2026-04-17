# Proposal: Carbide

Created (UTC): 2026-04-17T16:16:47Z
Repository HEAD: 39ff89aaa2868ccabeff078e3293c992bb57fb26

This directory holds the early design material for **Carbide** — a working codename for a browser-and-Node C# build-and-run framework that targets environments without the .NET SDK. The name is tentative and can be changed without affecting the technical content.

## Documents

- [Carbide — vision](carbide-vision__2026-04-17__16-16-47-000000.md) — what Carbide is, who it's for, what it does and does not try to do, tiered goals, success criteria.
- [Carbide — architecture and implementation plan](carbide-architecture-and-plan__2026-04-17__16-16-47-000000.md) — layer model, runtime topology, JS/TS interfaces, Webcil handling, milestones, testing, supply chain.

## Prior context

The feasibility analysis that precedes this proposal is the repository-level report [*Feasibility: building and running simple C# projects with only Python + Node.js*](../../reports/csharp-build-run-without-dotnet-sdk-feasibility__2026-04-17__01-02-58-000000.md). Carbide is the first concrete answer to the question that report posed: "what would a framework-shaped version of this look like, and how far up the tier ladder can we credibly go?"

## Status

Proposal stage. No code committed. No project exists yet under `src/`. These documents are the design contract that the eventual `src/Carbide/` (or renamed equivalent) will honour.
