# Carbide

A C# compile-and-run framework that ships as a single npm package, embeds the .NET runtime and Roslyn, and works identically in a browser tab and a Node.js process. Targeted at environments that cannot install the .NET SDK.

**Status:** M2 — Multi-document. `CarbideSession` boots the bundled Mono-WASM runtime in Node and in headless Chromium; a `Project` accepts any number of `.cs` sources via `addSource` / `updateSource` / `removeSource`; diagnostics carry the source path; a seed golden-corpus of five Shape-S2 fixtures (records, generics, pattern matching, LINQ, two-file hello) round-trips stdout. User-supplied DLLs and NuGet packages come in later milestones. See `docs/carbide-M2-detailed-plan__*.md` for what M2 covers and `docs/drift/` for known limitations.

## Layout

- [`docs/`](docs/README.md) — vision, architecture, and implementation plan.
- `packages/core/` — the `@carbide/core` npm package root.
  - `src/` — `Carbide.Core.csproj` (the Blazor WASM C# project), plus TypeScript sources under `src/ts/`.
  - `test/` — host-side smoke tests.
- `Directory.Build.props` / `Directory.Build.targets` — shared MSBuild settings for the C# project.

## Build

Prerequisites: .NET 10 SDK with the `wasm-tools` workload (`dotnet workload install wasm-tools`), Node.js ≥ 20, a Playwright browser download for the browser smoke test (`npx playwright install chromium`).

```bash
cd src/Carbide/packages/core
dotnet publish -c Release src/Carbide.Core.csproj
npm install
npm run build:ts
npm test                 # Node acceptance
npm run test:browser     # headless Chromium acceptance via Playwright
```

## Minimal usage

```ts
import { CarbideSession } from "@carbide/core";

const session = await CarbideSession.initializeAsync();
const project = session.createProject();

project.addSource(
    "Greeter.cs",
    `namespace MyApp; public static class Greeter { public static string Greet(string name) => $"hello, {name}"; }`,
);
project.addSource("Program.cs", 'using MyApp; Console.WriteLine(Greeter.Greet("Vladimir"));');

const result = await project.run();
console.log(result.stdOut); // "hello, Vladimir\n"
await session.shutdown();
```

Additional APIs land alongside:

- `project.updateSource(path, code)` — replace an existing file's contents.
- `project.removeSource(path)` — drop a file (no-op if it was never added).
- `project.getDiagnostics()` — Roslyn diagnostics with `path`, `spanStart/End`, `lineStart/End` populated.

## Origin

Carbide starts as a structural fork of [WasmSharp](https://github.com/JakeYallop/WasmSharp). See [`ATTRIBUTION.md`](packages/core/ATTRIBUTION.md) for the list of files adapted from upstream sources.
