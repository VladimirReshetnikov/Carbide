# Carbide Current-State Guide

Created (UTC): 2026-04-18T23:36:06Z
Updated (UTC): 2026-04-19T00:50:13Z
Repository HEAD: bec44f9bfc7cc2ed581066ddd2339487f6f8c685

- Status: Informational
- Audience: Users, maintainers, reviewers, and future contributors
- Scope: The current implemented behavior of `src/Carbide`, including what Carbide is for, how it is built and used today, what is intentionally out of scope, and where the important seams live in code
- Related code:
  - `../packages/core/`
  - `../packages/cli/`
  - `../packages/msbuild-lite/`
  - `../packages/nuget/`
  - `../packages/refs-net10.0/`
- Related docs:
  - [Carbide README](../README.md)
  - [Carbide docs index](README.md)
  - [@carbide/core README](../packages/core/README.md)
  - [Carbide usability report](Carbide-Usability-Report.md)
  - [Carbide vision](carbide-vision__2026-04-17__16-16-47-000000.md)
  - [Carbide architecture and implementation plan](carbide-architecture-and-implementation-plan__2026-04-17__16-16-47-000000.md)
  - [Drift tracking and documented runtime differences](drift/README.md)

## Summary

Carbide is a client-only C# compile-and-run framework for environments that do not have the .NET SDK installed. It packages a Mono-WASM-hosted .NET runtime, Roslyn, a TypeScript session/project API, a thin Node CLI, a bounded `.csproj` parser, a bounded NuGet resolver, and an optional .NET reference-pack sibling. The current implementation is best understood as an M6-era system: the core dual-host runtime, multi-document editing, user DLL injection, deterministic PE/PDB emission, CLI build/run/validate commands, `.csproj` ingestion, and bounded `PackageReference` resolution all exist; sibling `ProjectReference` build orchestration, Webcil mode, source generators, and general MSBuild parity do not.

This guide is the current-state companion to the planning documents. The vision and architecture pages explain why Carbide exists and the intended shape of the system; this page explains what is actually present in the repository now, how to use it successfully, where the sharp edges are, and which apparent capabilities are still only roadmap material.

## Status At A Glance

- Implemented today:
  - `@carbide/core` for browser and Node
  - multi-document source management
  - session-scoped user DLL references
  - deterministic PE and portable-PDB emission
  - `carbide build`, `carbide run`, and `carbide validate`
  - bounded `.csproj` parsing through `@carbide/msbuild-lite`
  - bounded NuGet v3 resolution, caching, allow-list policy, and `carbide.lock.json`
  - `@carbide/refs-net10.0` for a stable compile-time API surface
- Deliberately not implemented yet:
  - project-to-project build graphs for `<ProjectReference>`
  - Webcil mode
  - source generators and analyzers
  - `Directory.Build.props`, `<Import>`, `<Target>`, `<Task>`, or general MSBuild execution
  - live program-output streaming, program-stdin forwarding, and program-argv forwarding
  - SDK-level workload parity or general-purpose `dotnet` compatibility
- Automated verification in-repo:
  - Node acceptance tests for core and CLI behavior
  - Playwright browser smoke tests for hello-world, multi-document editing, emitted-PE round-trips, and user-DLL references
  - optional live NuGet smoke tests gated by `CARBIDE_NUGET_LIVE=1`

## What Carbide Is For

Carbide is aimed at a narrow but useful problem space:

- running small to medium C# programs in environments where installing the .NET SDK is impossible or undesirable
- embedding a C# compilation engine inside Node-based tools, browser playgrounds, or agent workflows
- validating generated C# from tooling without depending on a system SDK
- building bounded, mostly console-oriented C# projects that fit Carbide's supported shapes

Carbide is not trying to be:

- a drop-in replacement for `dotnet build` or `dotnet run`
- a production hosting platform for end-user applications
- a safe sandbox for adversarial code
- a full MSBuild implementation
- a GUI, server, or desktop app host

## Project Goals

The design documents define the larger goal set, but the current codebase is already optimized around a few stable goals:

1. Make C# compilation and execution available from npm packages, not from a machine-wide SDK install.
2. Keep browser and Node behavior close enough that the same conceptual session/project workflow applies to both.
3. Support more than the single-file demo case: multi-file projects, user-supplied DLLs, deterministic PE output, and a useful subset of `.csproj`.
4. Keep the supported surface bounded and explicit, especially around NuGet and MSBuild semantics.
5. Preserve a maintainable architecture where host concerns, TypeScript API concerns, C# Roslyn concerns, `.csproj` parsing, and NuGet resolution stay separated.

## Package Map

| Package / area | Responsibility today | Notes |
|---|---|---|
| `packages/core/` | `@carbide/core`, the main runtime/session/project API | Boots .NET WASM, exposes `CarbideSession` and `Project`, and contains the C# compiler host |
| `packages/cli/` | `@carbide/cli`, the `carbide` executable | Thin Node wrapper over `@carbide/core`, `@carbide/msbuild-lite`, and `@carbide/nuget` |
| `packages/msbuild-lite/` | Bounded `.csproj` parser | Captures compile items, properties, `PackageReference`, and `ProjectReference`; does not execute MSBuild |
| `packages/nuget/` | Bounded NuGet v3 resolver | Allow-list enforced by default; emits `carbide.lock.json`; rejects native/MSBuild/analyzer hazards |
| `packages/refs-net10.0/` | Untrimmed .NET reference assemblies | Stabilizes compile-time metadata against runtime trimming |
| `docs/` | Design, plans, drift notes, and current-state docs | The planning corpus is richer than the landing README and is worth reading before nontrivial changes |

## Tech Stack

### Runtime and compiler stack

- `.NET 10` via `Microsoft.NET.Sdk.WebAssembly`
- Mono-WASM runtime produced by `dotnet publish`
- Roslyn `Microsoft.CodeAnalysis.CSharp` and `Microsoft.CodeAnalysis.CSharp.Features` `4.14.0`
- `Microsoft.Extensions.Logging.Abstractions` for runtime logging
- `Jab` for DI inside the C# host

### JavaScript / TypeScript stack

- TypeScript `5.6.x`
- ESM-first package layout
- Node `20+` for the CLI and Node host path
- Playwright for browser smoke tests

### Supporting implementation choices

- hand-rolled JSON boundary versioning (`schemaVersion = 2` on the TS/C# interop boundary)
- hand-rolled XML parser for `@carbide/msbuild-lite`
- hand-rolled zip, nuspec, version-range, and TFM-compat logic for `@carbide/nuget`
- zero runtime dependencies in the `msbuild-lite` and `nuget` packages

## Architecture

### Mental model

Carbide is a layered client-side system:

1. A TypeScript consumer creates a `CarbideSession`.
2. The host adapter decides how `_framework/` assets are served.
3. `@carbide/core` boots `dotnet.js` and the packaged WASM runtime.
4. C# `CompilationInterop` exposes a JS-exported control plane into `SessionSolutions` and `ProjectCompiler`.
5. Roslyn compiles user sources against either the ref-pack DLLs or, if the ref-pack is unavailable, the runtime DLLs.
6. Optional layers above the core provide `.csproj` parsing, NuGet resolution, and a CLI.

### Core runtime path

`@carbide/core` is the heart of the system. The important files are:

- [`packages/core/src/ts/session.ts`](../packages/core/src/ts/session.ts) for `CarbideSession`
- [`packages/core/src/ts/project.ts`](../packages/core/src/ts/project.ts) for the user-facing `Project` API
- [`packages/core/src/ts/runtime/boot.ts`](../packages/core/src/ts/runtime/boot.ts) for WASM boot and ref-pack selection
- [`packages/core/src/CompilationInterop.cs`](../packages/core/src/CompilationInterop.cs) for the JSExport boundary
- [`packages/core/src/Services/SessionSolutions.cs`](../packages/core/src/Services/SessionSolutions.cs) for session/project lifetime
- [`packages/core/src/Services/ProjectCompiler.cs`](../packages/core/src/Services/ProjectCompiler.cs) for source management, compilation, emit, and execution
- [`packages/core/src/Services/ReferenceRegistry.cs`](../packages/core/src/Services/ReferenceRegistry.cs) for session-scoped DLL references

The API surface is intentionally small:

- `CarbideSession.initializeAsync()` boots the runtime.
- `session.createProject()` creates a mutable project inside that runtime.
- `project.addSource/updateSource/removeSource()` changes the source set.
- `session.addReference()` and `project.addReference()` attach user DLLs.
- `project.getDiagnostics()`, `project.build()`, and `project.run()` perform the useful work.

### Host adapters

The host adapter boundary lives under [`packages/core/src/ts/host/`](../packages/core/src/ts/host/). The two important paths are:

- [`browser/browser-adapter.ts`](../packages/core/src/ts/host/browser/browser-adapter.ts)
- [`node/node-adapter.ts`](../packages/core/src/ts/host/node/node-adapter.ts)

Current behavior is worth calling out:

- Browser usage loads `dotnet.js` and the other `_framework/` assets from module-relative URLs.
- Node usage starts a localhost HTTP asset server for the runtime DLLs because Mono-WASM's `file://` stream path is still unreliable for this workload.
- Node still imports `dotnet.js` over `file://`, because Node's ESM loader does not import `http(s)://` modules directly.

### Compile-time reference strategy

Carbide has two compile-time metadata modes:

- Preferred mode: use `@carbide/refs-net10.0`.
- Fallback mode: use the runtime's packaged DLLs.

The ref-pack wins completely when present. Carbide does not merge ref-pack and runtime DLLs, because Roslyn would then see duplicate type definitions and produce failures such as CS0518.

### `.csproj` and NuGet pipeline

The CLI's project-file path is orchestrated in [`packages/cli/src/project-file.ts`](../packages/cli/src/project-file.ts):

1. Parse the `.csproj` with `@carbide/msbuild-lite`.
2. Convert parsed properties into `ProjectOptions`.
3. Add source files to a `Project`.
4. Add any explicit `--ref` DLLs.
5. If `<PackageReference>` elements exist, resolve them through `@carbide/nuget`.
6. Attach resolved DLLs as Carbide references.
7. Write or replay `carbide.lock.json`.

This is the key architectural distinction:

- `@carbide/msbuild-lite` parses.
- `@carbide/nuget` resolves packages.
- `@carbide/core` compiles and runs.
- `@carbide/cli` composes those three without hiding their boundaries.

## Feature Set

| Capability | Current state | Notes |
|---|---|---|
| In-process TypeScript API | Supported | `CarbideSession` + `Project` |
| Node host | Supported | Primary CLI host; smoke and acceptance tested |
| Browser host | Supported | Browser smoke tests exist; current coverage is strongest in Chromium |
| Multi-document source editing | Supported | Add, update, and remove by caller-supplied logical path |
| User DLL injection | Supported | Session-scoped handles, validated synchronously |
| Build PE and PDB bytes | Supported | `project.build()` and `carbide build` |
| Execute console programs | Supported | Top-level statements, `Main()`, `Task`, `Task<int>`, `ValueTask`, `ValueTask<int>` |
| Deterministic builds | Supported | Enabled by default |
| `.csproj` input | Supported, bounded | `TargetFramework`, `Nullable`, `LangVersion`, `ImplicitUsings`, `DefineConstants`, `AssemblyName`, `RootNamespace`, compile globs, simple `Condition` |
| `PackageReference` | Supported in CLI path | Parsed by `msbuild-lite`, resolved by `@carbide/nuget` |
| `ProjectReference` | Not built | Captured and warned as `MSBLITE014`; no sibling-project orchestration yet |
| NuGet cache and lock file | Supported | `~/.carbide/nuget-cache` plus `carbide.lock.json` |
| Offline replay | Supported | `--offline` plus cache/lock |
| Webcil | Not implemented | `<WasmEnableWebcil>false</WasmEnableWebcil>` |
| Source generators / analyzers | Not implemented | Analyzer-bearing packages are refused |
| Program argv/stdin | Not wired through yet | The CLI parser understands `--`, but the runtime run path still invokes `Main` with an empty string array |

## Notable Implementation Details

### Session and project lifetimes

- References belong to a session, not to a project.
- A project can only attach handles created by the same session.
- Removing a reference invalidates the handle and detaches it from every project in that session.
- Shutting a session down invalidates all of its handles.

### Source-path identity is exact

Carbide treats the `path` string passed to `addSource`, `updateSource`, and `removeSource` as opaque identity:

- no slash normalization
- no case folding
- no trimming

`"Helper.cs"` and `"helper.cs"` are different documents. This is true even on Windows.

### Implicit usings are implemented as a hidden generated document

When `implicitUsings` are enabled, `ProjectCompiler` creates a hidden `Carbide.GlobalUsings.g.cs` document. That path is reserved; user code cannot add, update, or remove it.

### Build output kind is inferred

`project.build()` chooses:

- `ConsoleApplication` when top-level statements are present
- `DynamicallyLinkedLibrary` otherwise

This lets Carbide build both executable snippets and library-like source sets without an extra public knob.

### Binary payloads cross the JS boundary as base64

Reference DLL uploads, PE bytes, and PDB bytes are marshalled as base64 strings across the JSExport boundary. The public API still uses `Uint8Array`, but the actual C#/JS handoff is string-based for reliability.

### Execution uses `Assembly.Load(byte[])`

When a project runs:

1. Carbide emits the user's assembly to memory.
2. Any attached references are pre-loaded.
3. An `AssemblyResolve` handler helps the runtime satisfy later JIT-time reference loads.
4. The emitted assembly is loaded and invoked in-process.

This is why repeated runs in one session do not provide AppDomain-style isolation.

## Known Limitations And Differences

### Deliberate scope limits

- Carbide is a console-oriented build-and-run engine, not a general-purpose SDK replacement.
- GUI frameworks, ASP.NET Core, WPF, WinForms, MAUI, Avalonia, and similar workloads are out of scope.
- Unsafe or platform-dependent code may compile but is not part of the supported runtime story.
- Native NuGet packages, packages with `.targets`/`.props`, analyzers, and source generators are refused rather than partially supported.

### Current implementation gaps

- `<ProjectReference>` is parsed but not built.
- `Directory.Build.props`, `<Import>`, `<Target>`, `<Task>`, property functions, and broad MSBuild evaluation are not implemented.
- Webcil mode is off.
- The public runtime path does not expose program-stdin or program-argv yet.
- Output capture is `Console.SetOut`-based. Writes that bypass it (notably `Console.OpenStandardOutput`) can surface as raw bytes on stdout and may prefix the CLI's JSON output; consumers should treat the last non-empty stdout line as the JSON trailer.
- CLI invocations are cold-start oriented; there is no background daemon or pooled runtime process.

### Runtime differences from a local `dotnet` CLI

- Newlines from `Console.WriteLine` are `\n`, not Windows `\r\n`.
- Mono-WASM is single-threaded.
- Assembly state can linger within a session because `Assembly.Load(byte[])` has no AppDomain isolation.
- Trimming is disabled in `Carbide.Core.csproj` today to preserve runtime completeness.

### Target-framework reality

The public types mention `net8.0` and `net10.0`, but the repository currently ships and documents only `@carbide/refs-net10.0`, and the build/test corpus is centered on `net10.0`. Treat `net10.0` as the supported path unless you are deliberately experimenting with the fallback runtime-DLL metadata path.

## Build And Test From Source

### Prerequisites

- .NET 10 SDK
- `wasm-tools` workload:

```bash
dotnet workload install wasm-tools
```

- Node.js 20 or newer
- Playwright browser download for browser smoke tests:

```bash
npx playwright install chromium
```

### Monorepo build order

There is no root package-workspace orchestrator under `src/Carbide/`; build the packages explicitly.

```bash
# 1. Build or install the ref-pack so Carbide has stable compile-time refs.
cd src/Carbide/packages/refs-net10.0
npm install

# 2. Build the core runtime package.
cd ../core
npm install
dotnet publish -c Release src/Carbide.Core.csproj
npm run build:ts
npm run build:test-fixtures
npm test
npm run test:browser

# 3. Build the helper packages the CLI depends on.
cd ../msbuild-lite
npm install
npm run build
npm test

cd ../nuget
npm install
npm run build
npm test

# 4. Build and test the CLI.
cd ../cli
npm install
npm run build
npm test
```

Notes:

- `packages/cli/package.json` depends on sibling packages through `file:` references, so those siblings must already be built.
- `packages/core/package.json` publishes the `_framework/` output from `dotnet publish`, so a TypeScript-only build is not enough.
- Optional live NuGet integration tests are not part of the default `npm test`; they require network access and `CARBIDE_NUGET_LIVE=1`.

## Usage Manual

### When to use `@carbide/core` directly

Prefer the in-process API when you:

- are building a long-lived Node tool
- want to compile multiple projects without paying one process boot per operation
- need direct access to emitted PE/PDB bytes
- want tighter control over session lifetime and reference reuse

### When to use `@carbide/cli`

Prefer the CLI when you:

- want a simple subprocess-friendly entry point
- are wiring Carbide into shell scripts, CI steps, or other language runtimes
- are working from `.csproj` files and want built-in project-file and NuGet composition

### Core API options (quick reference)

This guide focuses on Carbide's behavior, but a few option points are worth having on one page.

Session boot:

- `CarbideSession.initializeAsync({ hostAdapter?, debugLevel?, enableDiagnosticTracing? })`
  - `hostAdapter` overrides auto-detection and controls where `_framework/` (and ref-pack) assets are served from.
  - `debugLevel` and `enableDiagnosticTracing` are passed through to the `dotnet.js` host config.
  - When omitted, Carbide auto-detects and picks a Node or browser adapter.

Project creation:

- `session.createProject({ assemblyName?, languageVersion?, nullable?, implicitUsings?, rootNamespace?, defineConstants?, targetFramework? })`
  - `implicitUsings` defaults to true and injects a hidden `Carbide.GlobalUsings.g.cs` document.
  - `rootNamespace` and `targetFramework` are currently informational rather than full selectors.

Host-adapter seam:

- Browser uses `BrowserHostAdapter` with a `frameworkAssetsBaseUrl` that must serve `_framework/` over HTTP(S).
- Node uses `NodeHostAdapter` (available via `@carbide/core/node`) and serves runtime assets over localhost HTTP by default.

### In-process Node example

```ts
import { CarbideSession } from "@carbide/core";

const session = await CarbideSession.initializeAsync();
const project = session.createProject({ assemblyName: "HelloApp" });

project.addSource(
    "Program.cs",
    `Console.WriteLine("hello from Carbide");`,
);

const run = await project.run();
console.log(run.stdOut);

await session.shutdown();
```

### Multi-document project example

```ts
const session = await CarbideSession.initializeAsync();
const project = session.createProject({ assemblyName: "GreeterApp" });

project.addSource(
    "Greeter.cs",
    `namespace Demo; public static class Greeter { public static string Greet(string name) => $"hello, {name}"; }`,
);
project.addSource(
    "Program.cs",
    `using Demo; Console.WriteLine(Greeter.Greet("Vladimir"));`,
);

const first = await project.run();
project.updateSource("Greeter.cs", `namespace Demo; public static class Greeter { public static string Greet(string name) => $"hi, {name}"; }`);
const second = await project.run();

await session.shutdown();
```

### User DLL reference example

```ts
import { readFileSync } from "node:fs";
import { CarbideSession } from "@carbide/core";

const session = await CarbideSession.initializeAsync();
const helperBytes = new Uint8Array(readFileSync("./MyHelper.dll"));
const helperRef = session.addReference(helperBytes, "MyHelper");

const project = session.createProject({ assemblyName: "UseHelper" });
project.addReference(helperRef);
project.addSource("Program.cs", `using MyHelper; Console.WriteLine(Thing.Describe(42));`);

const result = await project.run();
await session.shutdown();
```

### Build-only example

```ts
const build = await project.build();

if (build.success) {
    await fs.writeFile("out/MyLib.dll", build.pe);
    await fs.writeFile("out/MyLib.pdb", build.pdb);
}
```

### CLI direct-source mode

```bash
# Validate one or more files.
npx carbide validate --source Program.cs --format human

# Build a library.
npx carbide build --source Thing.cs --assembly-name MyLib --out out/

# Run an app with an explicit DLL reference.
npx carbide run --source Program.cs --ref out/MyLib.dll --format human
```

CLI facts worth knowing:

- `--format json` is the default.
- `build` exits `1` for compile errors, `2` for unexpected or I/O errors, and `3` for bad flag combinations.
- `build --out -` writes raw PE bytes to stdout and omits the PDB.
- `validate` only reports diagnostics; it never emits or runs.
- `--` is parsed as a program-argument separator, but program arguments are not forwarded yet (they are currently ignored).

### CLI `.csproj` mode

```bash
npx carbide build --project Foo.csproj --out out/
npx carbide run --project Foo.csproj --format human
npx carbide validate --project Foo.csproj --format json
```

Supported `.csproj` features currently include:

- `TargetFramework` and `TargetFrameworks` with first-listed selection
- `Nullable`
- `LangVersion`
- `ImplicitUsings`
- `DefineConstants`
- `AssemblyName`
- `RootNamespace`
- `EnableDefaultCompileItems`
- `<Compile Include="...">` and `<Compile Remove="...">`
- simple `Condition="..."` expressions using `==`, `!=`, `and`, and `or`
- `<PackageReference Include="..." Version="..."/>`
- `<ProjectReference Include="..."/>` capture only

Unsupported constructs produce warnings where possible and are otherwise ignored.

### NuGet and lock-file workflow

When a `.csproj` contains `<PackageReference>` entries and the CLI is used, Carbide will:

1. resolve those packages through `@carbide/nuget`
2. add the selected managed DLLs as Carbide references
3. write `carbide.lock.json` next to the project unless a lock already exists or `--no-lock-write` is set

Typical workflow:

```bash
# First run: resolve, download, write a lock.
npx carbide run --project Foo.csproj

# Later run: require cache/lock only.
npx carbide run --project Foo.csproj --offline
```

Useful NuGet flags:

- `--offline`
- `--lock <path>`
- `--no-lock-write`
- `--nuget-source <url>`
- `--allow-list-mode strict|advisory|off`
- `CARBIDE_NUGET_CACHE_DIR` (env var) overrides the default cache location (`~/.carbide/nuget-cache`) used by `@carbide/nuget`.

Default allow-list entries at the time of this document:

- `Newtonsoft.Json`
- `YamlDotNet`
- `CsvHelper`
- `Humanizer.Core`
- `NodaTime`
- `Scriban`
- `Handlebars.Net`
- `Serilog`
- `Serilog.Sinks.Console`
- `FluentAssertions`

### Browser usage

There is no browser CLI. In the browser, use `@carbide/core` directly. The best working examples in the repository today are the Playwright fixtures under [`packages/core/test/browser/`](../packages/core/test/browser/):

- `hello.html`
- `multi-document.html`
- `build-roundtrip.html`
- `user-reference.html`

These are useful both as smoke tests and as minimal browser-host reference implementations.

## Guided Tutorial

This is the fastest way to learn Carbide's intended workflow.

### Step 1: compile and run one file

Create `Program.cs`:

```csharp
Console.WriteLine("hello");
```

Run it:

```bash
npx carbide run --source Program.cs --format human
```

What to notice:

- the program runs without a machine-wide .NET SDK install at runtime
- output is buffered and printed after execution completes
- newline behavior is `\n`

### Step 2: split the program into two files

Create `Greeter.cs`:

```csharp
namespace Demo;
public static class Greeter
{
    public static string Greet(string name) => $"hello, {name}";
}
```

Update `Program.cs`:

```csharp
using Demo;
Console.WriteLine(Greeter.Greet("world"));
```

Run it:

```bash
npx carbide run --source Program.cs --source Greeter.cs --format human
```

### Step 3: emit a DLL and reuse it

Build a library:

```bash
npx carbide build --source Greeter.cs --assembly-name Demo.Greeter --out out/
```

Consume it from another program:

```bash
npx carbide run --source Program.cs --ref out/Demo.Greeter.dll --format human
```

This is the manual alternative to the future `<ProjectReference>` story.

### Step 4: move to a `.csproj`

Create `Foo.csproj`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>Foo</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

Now run:

```bash
npx carbide run --project Foo.csproj --format human
```

### Step 5: add an allow-listed package

Add a package reference:

```xml
<ItemGroup>
  <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
</ItemGroup>
```

Use it:

```csharp
using Newtonsoft.Json;

var payload = new { greeting = "hello", count = 3 };
Console.Write(JsonConvert.SerializeObject(payload));
```

Run once online:

```bash
npx carbide run --project Foo.csproj --format human
```

Run again offline:

```bash
npx carbide run --project Foo.csproj --offline --format human
```

If this works, you have exercised the full currently implemented project-file path: `.csproj` parsing, package resolution, cache use, and lock replay.

## Troubleshooting

| Symptom | Likely cause | What to do |
|---|---|---|
| `NodeHostAdapter could not locate the Carbide _framework/ directory` | `packages/core` has not been published/built yet, or the runtime layout is not where the adapter expects it | Run `dotnet publish -c Release src/Carbide.Core.csproj` in `packages/core`, then rebuild the TS side |
| Missing BCL members such as `Console.Write` or surprising compile-time API gaps | Carbide is compiling against trimmed runtime DLLs because the ref-pack is absent | Install/build `@carbide/refs-net10.0` so the ref-pack path is available |
| `MSNUGET021` allow-list refusal | The requested package is not in Carbide's allow-list | Use `--allow-list-mode advisory` for experiments, `off` for fully unbounded tests, or add the package properly to the allow-list and fixture corpus |
| `MSNUGET015`, `MSNUGET016`, or `MSNUGET017` | The package carries native assets, MSBuild targets/props, or analyzers | Pick a different package or accept that this package shape is intentionally unsupported |
| `MSNUGET030` under `--offline` | The package bytes are not in the local cache and/or the lock cannot be replayed | Run once without `--offline` to populate the cache and lock |
| A newly added `<PackageReference>` seems to be ignored | Existing `carbide.lock.json` is being replayed | Delete or regenerate the lock file, then rerun online |
| `MSBLITE014` warnings | The project uses `<ProjectReference>` | Build the referenced project separately and feed its DLL in via `--ref` or the session API |
| `Document path 'X' is already in the project` | `addSource` is being used for an existing logical path | Use `updateSource` instead |
| `'Carbide.GlobalUsings.g.cs' is a reserved path` | User code is trying to manipulate Carbide's hidden implicit-usings document | Pick another logical path; if you want strict behavior, disable implicit usings |
| Re-running within one session seems to observe stale loaded code or state | Assembly loads persist for the life of the process/session | Recreate the session between runs that need hard isolation |
| Browser smoke tests fail immediately | Playwright browser download missing or static assets not reachable | Run `npx playwright install chromium` and verify the `_framework/` assets are being served |

## Maintainer Guideposts

If you need to modify Carbide, start here:

- API and runtime boot:
  - [`packages/core/src/ts/session.ts`](../packages/core/src/ts/session.ts)
  - [`packages/core/src/ts/project.ts`](../packages/core/src/ts/project.ts)
  - [`packages/core/src/ts/runtime/boot.ts`](../packages/core/src/ts/runtime/boot.ts)
- Host-specific boot behavior:
  - [`packages/core/src/ts/host/node/node-adapter.ts`](../packages/core/src/ts/host/node/node-adapter.ts)
  - [`packages/core/src/ts/host/node/asset-server.ts`](../packages/core/src/ts/host/node/asset-server.ts)
- C# compiler/runtime behavior:
  - [`packages/core/src/CompilationInterop.cs`](../packages/core/src/CompilationInterop.cs)
  - [`packages/core/src/Services/ProjectCompiler.cs`](../packages/core/src/Services/ProjectCompiler.cs)
  - [`packages/core/src/Services/SessionSolutions.cs`](../packages/core/src/Services/SessionSolutions.cs)
  - [`packages/core/src/Services/ReferenceRegistry.cs`](../packages/core/src/Services/ReferenceRegistry.cs)
- `.csproj` behavior:
  - [`packages/msbuild-lite/src/index.ts`](../packages/msbuild-lite/src/index.ts)
  - [`packages/msbuild-lite/src/xml.ts`](../packages/msbuild-lite/src/xml.ts)
- NuGet behavior:
  - [`packages/nuget/src/resolver.ts`](../packages/nuget/src/resolver.ts)
  - [`packages/nuget/src/safety.ts`](../packages/nuget/src/safety.ts)
  - [`packages/nuget/src/allowlist.ts`](../packages/nuget/src/allowlist.ts)
  - [`packages/nuget/src/lock.ts`](../packages/nuget/src/lock.ts)
- CLI orchestration:
  - [`packages/cli/src/project-file.ts`](../packages/cli/src/project-file.ts)
  - [`packages/cli/src/commands/`](../packages/cli/src/commands/)

The planning documents remain relevant because they describe intended future seams. The drift notes are equally important because they record the user-visible deltas between that design and today's implementation reality.

## Short Roadmap Reading

For contributors, the main future-facing topics are:

- M7: public API hardening and stability freeze
- M8: Webcil mode
- M9: `<ProjectReference>` build graphs
- bridge/UI proposals in this docs directory, which are exploratory and not part of the current shipping surface

Treat roadmap docs as plans, not as claims about present behavior.
