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
- [M4 — detailed plan (PE emission & CLI)](carbide-M4-detailed-plan__2026-04-18__19-45-17-979644.md) — sub-milestones, `project.build()` returning `{pe, pdb}`, `@carbide/cli` package with `build` / `run` / `validate` commands, round-trip acceptance, and M4 non-goals.
- [M5 — detailed plan (project-file input)](carbide-M5-detailed-plan__2026-04-18__21-23-32-734397.md) — sub-milestones, `@carbide/msbuild-lite` port of `cs_kit.msbuild_lite`, `carbide build --project Foo.csproj`, extended `ProjectOptions`, deterministic builds for byte-identical PE, parity fixtures, and M5 non-goals.
- [M6 — detailed plan (NuGet resolver)](carbide-M6-detailed-plan__2026-04-18__22-19-10-231651.md) — sub-milestones, `@carbide/nuget` (flat-container + registration clients, nuspec/zip reader, version-range evaluator, TFM-compat matrix, nearest-wins resolver), 10-package allow-list, safety refusals, `carbide.lock.json` for offline replay, and M6 non-goals.
- [M9 — detailed plan (Shape S5: project-to-project references)](carbide-M9-detailed-plan__2026-04-18__23-18-54-749142.md) — sub-milestones, project-graph orchestration inside `@carbide/cli`, transitive `<ProjectReference>` walk with cycle / assembly-name-collision detection, per-sub-project Carbide `Project` + NuGet lock, sibling-PE attachment in topological order, diagnostic attribution by csproj, and M9 non-goals. M7 (stability lock) and M8 (Webcil mode) are deferred; M9 takes the slot directly after M6.

## Feature proposals

- [JS↔C# interop bridge proposal](carbide-js-interop-bridge-proposal__2026-04-18__22-00-00-000000__515d12a34be3.md) — design for an ES6-Proxy-based object-graph bridge over `[JSExport]` plus a compile-time surface manifest, targeting ClearScript-level ergonomics for user C# compiled in-sandbox. Introduces `@carbide/bridge` (TS) and `Carbide.Core.Bridge` (C#) as a peer of the existing control-plane `CompilationInterop`. Companion to the repository-level [JS↔C# WASM interop libraries survey](../../../docs/reports/carbide-wasm-js-interop-libraries-survey__2026-04-18__21-43-55-000000__b27d950cd3b9.md).

## Companion-project proposals

- [`Carbide.UI` / `@carbide-ui/*` — Avalonia GUI integration proposal](carbide-ui-avalonia-integration-proposal__2026-04-18__22-04-08-231875__a5988020103c.md) — three approaches compared (merged runtime / cross-frame runner / offline CLI), commits to cross-frame runner + offline CLI as concurrent delivery, specifies package layout, `postMessage` protocol, XAML strategy, and UI-M0..UI-M8 milestones. Paired with the [feasibility report](../../../docs/reports/carbide-avalonia-browser-gui-integration__2026-04-18__21-52-50-185670__ebf5a870d7ad.md).

## Packages in this repository

- `packages/core/` — `@carbide/core`, the runtime/session/project surface.
- `packages/refs-net10.0/` — `@carbide/refs-net10.0`, the .NET 10 reference pack (`Microsoft.NETCore.App.Ref` → extracted `ref/net10.0/*.dll`). Opt-in sibling; when installed, Carbide's compile-time API surface is stable against runtime trim decisions.
- `packages/cli/` — `@carbide/cli`, the `carbide` binary wrapping `@carbide/core` with `build` / `run` / `validate` subcommands.
- `packages/msbuild-lite/` — `@carbide/msbuild-lite`, a bounded `.csproj` parser (semantic port of `cs_kit.msbuild_lite`). Consumed by the CLI's `--project` path.
- `packages/nuget/` — `@carbide/nuget`, a bounded NuGet v3 resolver (flat-container client, nupkg/nuspec reader, TFM-compat matrix, allow-list, filesystem cache, `carbide.lock.json` read/write). Consumed by the CLI when a `.csproj` declares `<PackageReference>`s.

## Operations

- [Drift tracking](drift/README.md) — periodic upstream-drift reports; also lists documented runtime differences vs. a local .NET CLI.

## Prior context

The feasibility analysis that precedes this work is the repository-level report [*Feasibility: building and running simple C# projects with only Python + Node.js*](../../../docs/reports/csharp-build-run-without-dotnet-sdk-feasibility__2026-04-17__01-02-58-000000.md). Carbide is the first concrete answer to the question that report posed: "what would a framework-shaped version of this look like, and how far up the tier ladder can we credibly go?"

## Status

Project initiated under `src/Carbide/`. These documents are the design contract the code in this project is expected to honour.
