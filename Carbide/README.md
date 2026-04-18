# Carbide

Carbide is a C# compile-and-run framework for environments that do not have the .NET SDK installed. It packages a Mono-WASM-hosted .NET runtime, Roslyn, a TypeScript session/project API, a Node CLI, a bounded `.csproj` parser, a bounded NuGet resolver, and an optional .NET reference-pack sibling.

**Current implementation status:** the repository has working M1-M6 functionality. That means:

- browser and Node runtime support via `@carbide/core`
- multi-document source management
- user-supplied DLL references
- deterministic PE/PDB emission
- `carbide build`, `carbide run`, and `carbide validate`
- bounded `.csproj` support via `@carbide/msbuild-lite`
- bounded `PackageReference` resolution, cache, and `carbide.lock.json` via `@carbide/nuget`

Still pending are sibling `<ProjectReference>` build orchestration, Webcil mode, source generators/analyzers, and broader MSBuild parity. The authoritative current-state guide is [Carbide Current-State Guide](docs/carbide-current-state-guide__2026-04-18__23-36-06-000000__3002613a289e.md).

## Layout

- [`docs/`](docs/README.md) — vision, architecture, milestone plans, drift notes, and the current-state guide.
- `packages/core/` — the `@carbide/core` npm package root.
  - `src/` — `Carbide.Core.csproj` (the Blazor WASM C# project), plus TypeScript sources under `src/ts/`.
  - `test/` — host-side smoke tests and user-DLL fixtures under `test/fixtures/`.
- `packages/refs-net10.0/` — the `@carbide/refs-net10.0` ref-pack (extracted from `Microsoft.NETCore.App.Ref` at install time; provides the untrimmed compile-time API surface).
- `packages/cli/` — the `@carbide/cli` npm package (the `carbide` command; thin wrapper around `@carbide/core`).
- `packages/msbuild-lite/` — the `@carbide/msbuild-lite` npm package (bounded `.csproj` parser, semantic port of `cs_kit.msbuild_lite`).
- `packages/nuget/` — the `@carbide/nuget` npm package (bounded NuGet v3 resolver with allow-list policy, cache, and lock file support).
- `Directory.Build.props` / `Directory.Build.targets` — shared MSBuild settings for the C# project.

## Build And Test From Source

Prerequisites: .NET 10 SDK with the `wasm-tools` workload (`dotnet workload install wasm-tools`), Node.js ≥ 20, a Playwright browser download for the browser smoke test (`npx playwright install chromium`).

```bash
# One-time: extract the ref-pack so Carbide's compile-time metadata surface is stable.
cd src/Carbide/packages/refs-net10.0
node scripts/build.mjs

# Core runtime package.
cd ../core
dotnet publish -c Release src/Carbide.Core.csproj
npm install
npm run build:ts
npm run build:test-fixtures   # builds MyHelper.dll for user-reference tests
npm test                      # Node acceptance
npm run test:browser          # headless Chromium acceptance via Playwright

# Helper packages that the CLI depends on.
cd ../msbuild-lite
npm install
npm run build
npm test

cd ../nuget
npm install
npm run build
npm test

cd ../cli
npm install
npm run build
npm test
```

`packages/cli/package.json` depends on sibling packages through `file:` references, so `msbuild-lite`, `nuget`, and `core` should already be built before you expect the CLI package to behave like a published install.

## Minimal API Usage

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

### Core API surface

- `session.addReference(bytes, name?)` / `session.removeReference(handle)` — session-scoped reference registry.
- `project.addReference(handle)` — attach a session-registered reference to a specific project.
- `project.addSource(path, code)` / `updateSource(path, code)` / `removeSource(path)` — multi-document source management.
- `project.getDiagnostics()` — Roslyn diagnostics with `path`, `spanStart/End`, `lineStart/End` populated.
- `project.build()` — compile and return `{ success, pe, pdb, diagnostics, durationMs }` without executing.
- `project.run()` — compile + execute; returns `{ success, stdOut, stdErr, exitCode, diagnostics, … }`.

### CLI

```bash
# Build a library → DLL on disk.
npx carbide build --source Thing.cs --assembly-name MyLib --out out/lib/

# Reference it from another source and run.
npx carbide run --source Program.cs --ref out/lib/MyLib.dll --format human
```

The CLI also accepts a `.csproj`:

```bash
npx carbide build --project Foo.csproj --out out/
npx carbide run --project Foo.csproj
npx carbide validate --project Foo.csproj
```

If the project declares `<PackageReference>` elements, the CLI resolves them through `@carbide/nuget` and writes a `carbide.lock.json` next to the project by default:

```bash
npx carbide run --project Foo.csproj
npx carbide run --project Foo.csproj --offline
```

`<ProjectReference>` is still captured-only and surfaces as `MSBLITE014` warnings. For now, build sibling projects separately and feed their DLLs in via `--ref` or the `session.addReference(...)` API.

See:

- [`packages/cli/README.md`](packages/cli/README.md) for command details
- [`packages/msbuild-lite/README.md`](packages/msbuild-lite/README.md) for the supported `.csproj` subset
- [`packages/nuget/README.md`](packages/nuget/README.md) for NuGet resolution policy, cache, and lock behavior
- [Carbide Current-State Guide](docs/carbide-current-state-guide__2026-04-18__23-36-06-000000__3002613a289e.md) for the full project manual, tutorial, limitations, and troubleshooting

## Origin

Carbide starts as a structural fork of [WasmSharp](https://github.com/JakeYallop/WasmSharp). See [`ATTRIBUTION.md`](packages/core/ATTRIBUTION.md) for the list of files adapted from upstream sources.
