# Carbide

A C# compile-and-run framework that ships as a single npm package, embeds the .NET runtime and Roslyn, and works identically in a browser tab and a Node.js process. Targeted at environments that cannot install the .NET SDK.

**Status:** M3 — Reference DLL injection + ref-pack (Shape S3). On top of M1/M2's multi-document compile-and-run, a session now exposes `addReference(bytes, name)` / `removeReference(handle)` and projects expose `addReference(handle)`. When `@carbide/refs-net10.0` is installed alongside, Carbide's compile-time metadata set comes from the untrimmed `Microsoft.NETCore.App.Ref` ref-pack rather than the runtime's own (possibly trimmed) BCL, so Roslyn always sees the documented public API surface. NuGet-level resolution is still deferred to M6. See `docs/carbide-M3-detailed-plan__*.md` and `docs/drift/`.

## Layout

- [`docs/`](docs/README.md) — vision, architecture, and per-milestone implementation plans.
- `packages/core/` — the `@carbide/core` npm package root.
  - `src/` — `Carbide.Core.csproj` (the Blazor WASM C# project), plus TypeScript sources under `src/ts/`.
  - `test/` — host-side smoke tests and user-DLL fixtures under `test/fixtures/`.
- `packages/refs-net10.0/` — the `@carbide/refs-net10.0` ref-pack (extracted from `Microsoft.NETCore.App.Ref` at install time; provides the untrimmed compile-time API surface).
- `Directory.Build.props` / `Directory.Build.targets` — shared MSBuild settings for the C# project.

## Build

Prerequisites: .NET 10 SDK with the `wasm-tools` workload (`dotnet workload install wasm-tools`), Node.js ≥ 20, a Playwright browser download for the browser smoke test (`npx playwright install chromium`).

```bash
# One-time: extract the ref-pack so Carbide's compile-time metadata surface is stable.
cd src/Carbide/packages/refs-net10.0
node scripts/build.mjs

cd ../core
dotnet publish -c Release src/Carbide.Core.csproj
npm install
npm run build:ts
npm run build:test-fixtures   # builds MyHelper.dll for user-reference tests
npm test                      # Node acceptance
npm run test:browser          # headless Chromium acceptance via Playwright
```

## Minimal usage

```ts
import { CarbideSession } from "@carbide/core";
import { readFileSync } from "node:fs";

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

### Attaching a user-supplied DLL

```ts
const bytes = new Uint8Array(readFileSync("./MyHelper.dll"));
const handle = session.addReference(bytes, "MyHelper");

const project = session.createProject();
project.addReference(handle);
project.addSource("Program.cs", 'using MyHelper; Console.WriteLine(Thing.Describe(42));');
const result = await project.run();    // "Thing<42>\n"
```

### API surface

- `session.addReference(bytes, name?)` / `session.removeReference(handle)` — session-scoped reference registry.
- `project.addReference(handle)` — attach a session-registered reference to a specific project.
- `project.addSource(path, code)` / `updateSource(path, code)` / `removeSource(path)` — multi-document source management.
- `project.getDiagnostics()` — Roslyn diagnostics with `path`, `spanStart/End`, `lineStart/End` populated.
- `project.run()` — compile + execute; returns `{ success, stdOut, stdErr, exitCode, diagnostics, … }`.

## Origin

Carbide starts as a structural fork of [WasmSharp](https://github.com/JakeYallop/WasmSharp). See [`ATTRIBUTION.md`](packages/core/ATTRIBUTION.md) for the list of files adapted from upstream sources.
