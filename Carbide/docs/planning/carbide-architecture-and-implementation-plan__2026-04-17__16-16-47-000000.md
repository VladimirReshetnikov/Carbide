# Carbide — architecture and implementation plan

Created (UTC): 2026-04-17T16:16:47Z
Repository HEAD: 39ff89aaa2868ccabeff078e3293c992bb57fb26

Status: first-pass architectural proposal paired with a tiered implementation plan.
Audience: repository owner and future contributors; intended to be buildable from without ambiguity on critical seams.
Scope: the *how*. The *what* and *why* are in [carbide-vision](../carbide-vision__2026-04-17__16-16-47-000000.md).

This document explicitly takes positions on every open decision the vision document flagged. Where a decision is ambiguous, the document picks one and states the alternative alongside it with a short rationale. Re-opening any decision requires editing §13 (Open questions) rather than silently reshaping the architecture.

## 1. Architectural overview

Carbide is a **layered client-only framework**. No Carbide component is a long-running server, a daemon, or a network service. All execution happens inside the consumer's host process (a browser tab, a Node script, or a test harness), driven by the consumer's code. The architecture is:

```text
 ┌────────────────────────────────────────────────────────────────────┐
 │ L9  Consumer                                                       │
 │     - browser page / worker, Node.js script, test harness, CLI     │
 │     - cs-agent-tools (Python) as a first-class consumer            │
 └────────────────┬────────────────────────────────┬──────────────────┘
                  │                                │
                  ▼                                ▼
 ┌────────────────────────┐      ┌────────────────────────────────────┐
 │ L8  @carbide/cli       │      │ L8  @carbide/core (JS/TS)          │
 │     thin CLI over core │      │     - Session, Project, Build, Run │
 └────────────┬───────────┘      │     - Environment adapter          │
              │                  │       (browser / Node)             │
              │                  └────────────┬───────────────────────┘
              └─────────┬────────── uses ─────┘
                        ▼
       ┌────────────────────────────────────────┐
       │ L7  Host adapter                       │
       │     - fetch shim, asset server (Node)  │
       │     - resource loader (browser)        │
       │     - stdout/stderr plumbing           │
       └────────────────┬───────────────────────┘
                        ▼
       ┌────────────────────────────────────────┐
       │ L6  .NET-WASM boot (dotnet.js)         │
       │     pinned .NET 10 + trimmed BCL       │
       └────────────────┬───────────────────────┘
                        ▼
       ┌────────────────────────────────────────┐
       │ L5  Carbide.Core (C#, compiled to WASM)│
       │     - JSExport surface                 │
       │     - Workspace / Solution manager     │
       │     - Reference registry               │
       │     - Emit service                     │
       │     - Execution service                │
       └────────────────┬───────────────────────┘
                        ▼
       ┌────────────────────────────────────────┐
       │ L4  Roslyn (Microsoft.CodeAnalysis.*)  │
       │     consumed as a library, pinned ver. │
       └────────────────────────────────────────┘

   Separately:
       ┌────────────────────────────────────────┐
       │ L3  @carbide/refs-<tfm>                │
       │     pre-extracted reference assemblies │
       └────────────────────────────────────────┘

       ┌────────────────────────────────────────┐
       │ L2  @carbide/nuget                     │
       │     Node-side NuGet resolver + cache   │
       │     (optional; also usable from L9)    │
       └────────────────────────────────────────┘

       ┌────────────────────────────────────────┐
       │ L1  @carbide/msbuild-lite              │
       │     project-file parser (TS)           │
       │     (optional; can consume cs_kit's)   │
       └────────────────────────────────────────┘
```

Layers 5 and 6 together form the WASM blob shipped inside `@carbide/core`. Layer 3 ships as a sibling package (or packages, one per TFM). Layers 1 and 2 are independent TypeScript packages the consumer may use or bypass. Layers 7–9 are the public surface.

## 2. Host topologies

Carbide supports three host topologies. They share the same Layer 5 WASM blob; the differences are in Layer 7.

### 2.1 Browser host

- ES-module import of `@carbide/core` from a `<script type="module">` or a bundler-emitted chunk.
- Optional Web Worker mode: Carbide posts messages to a worker that owns the WASM runtime, so the main thread stays responsive. Requires HTTPS or `localhost` (standard browser requirement for SharedArrayBuffer).
- Layer 7 fetches `_framework/` assets (`.wasm`, `.dll` or `.webcil`, `.dat`) over HTTPS from the bundle origin. Reference packs and NuGet DLLs are fetched the same way (from CDN, same-origin static hosting, or a `Blob`-backed `URL` the consumer supplied).
- No asset server needed; `fetch()` works natively.

### 2.2 Node.js host

- `import { CarbideSession } from '@carbide/core'` from Node ≥ 20.
- Layer 7 starts a localhost HTTP server on a random port, serves the package-local `_framework/` directory, and hands the base URL to the .NET runtime via the existing `assembliesUrl` / `boot.json` mechanism. This mirrors cs-agent-tools' `wasmsharp_compile.mjs` but is wrapped as a reusable primitive.
- Node's built-in `fetch` is lightly patched to resolve `file://` URLs against the local filesystem, so Carbide can still load some resources directly.
- Stdin/stdout/stderr of the host Node process are available for the inner program's I/O (captured by default, passed through on request).

### 2.3 WASI host (Band C, stretch)

- `@carbide/core-wasi` — a sibling package that ships a WASI-targeted .NET runtime build.
- Layer 7 becomes a thin WASI loader (wasmtime-based CLI wrapper, or a Node library that uses the `wasi` API).
- Runs the built program against the policy the host defines (file-system directories allowed, environment variables allowed). Trade-off: slightly more I/O than the browser sandbox, significantly less than "real" .NET.

The WASI path is optional and explicitly out of the critical path until Band C. It is called out here so L5's API is designed to not preclude it (e.g., execution service should not assume stdout is a `MemoryStream`).

## 3. Layer details

### 3.1 L5 — Carbide.Core (C#, compiled to WASM)

The C# project structurally mirrors `WasmSharp.Core`: `Microsoft.NET.Sdk.WebAssembly`, `RuntimeIdentifier=browser-wasm`, pinned `TargetFramework=net10.0` initially. Differences from WasmSharp:

- **Multiple documents per Project.** `Carbide.Core` maintains `Dictionary<string, DocumentId>` keyed by logical source path. The JSExport surface exposes `AddSource(path, code)`, `UpdateSource(path, code)`, `RemoveSource(path)`. Under the hood, these call `Solution.AddDocument` / `WithDocumentText` / `RemoveDocument`.
- **Registrable references.** Besides the built-in metadata reference set produced by `MetadataReferenceCache`, there is a user-reference registry. `AddReference(bytes, name)` wraps a PE image in a `MetadataReference.CreateFromImage(...)`. Refs are flushed on `Reset`.
- **First-class Emit surface.** `EmitPe()` returns the PE and PDB bytes (or a diagnostic set) without running the program. `Run()` continues to `Emit` + `Assembly.Load` + invoke `EntryPoint`, the WasmSharp path, but is now a separable step.
- **Session vs Project separation.** A `Session` owns the Workspace, the reference registry, and the Roslyn configuration. A `Project` is a named subset of sources within a session (future: multiple projects with `ProjectReference` edges, for Shape S5).
- **Cold compilation primitives.** `CompileOnce(sources, references, options)` is exposed for cases where no state needs to survive; cheaper than building a full Project when the answer is "compile this blob once".

Pinned NuGet versions at day one: `Microsoft.CodeAnalysis.CSharp` 4.14.0, `.CSharp.Features` 4.14.0, `.CSharp.Workspaces` 4.14.0, `.Features` 4.14.0, `.Workspaces` 4.14.0 — same set WasmSharp uses, since their compatibility with WASM is already proven. Advancing these requires a conscious upgrade, not a silent transitive bump.

Preserved from WasmSharp and inherited:

- `roots.xml` trimming roots for Roslyn internal types that the linker otherwise strips.
- `WasmMetadataReferenceResolver` and `MetadataReferenceCache` pattern for fetching pre-bundled BCL refs over HTTP.
- The workaround for the Roslyn-scripting `Assembly.Location` bug (use `CSharpCompilation.CreateScriptCompilation`; never `CSharpScript.RunAsync`).

Not inherited:

- Single-document `CodeSession`. Replaced outright.
- Fixed reference resolver with no registration hooks. Replaced with a registry-backed resolver.
- The current Console.Out redirection pattern in `RunAsync`. Moved into a reusable `IExecutionOutput` helper that also supports streaming.

### 3.2 L6 — .NET WASM boot

A pinned copy of the `dotnet.js` / `dotnet.wasm` pair produced by `dotnet publish` of the Carbide.Core project, plus the trimmed BCL it depends on. Shipped verbatim inside `@carbide/core/_framework/`. The filenames and directory shape match Blazor WASM's convention, which keeps tooling (and Webcil migration, §7) on standard tracks.

Build reproducibility: a CI job runs `dotnet publish -c Release` on a fixed image with a fixed SDK version, emits the `_framework/` directory, and commits hashes of every file into the release notes. Any delta between runs is a release-gating issue.

### 3.3 L7 — Host adapter

Two implementations, one interface.

Interface (TypeScript):

```ts
export interface HostAdapter {
  resolveFrameworkAssetsBaseUrl(): Promise<string>;
  fetchBinary(url: string): Promise<Uint8Array>;
  writeFile(path: string, bytes: Uint8Array): Promise<void> | never;  // throws in browser
  readFile(path: string): Promise<Uint8Array> | never;                // throws in browser
  captureStdout(): { onData(cb: (chunk: string) => void): void; drain(): string };
  captureStderr(): { onData(cb: (chunk: string) => void): void; drain(): string };
}
```

`captureStdout` is the surface through which the running C# program's output is streamed to the consumer. Both line-buffered (default) and byte-streamed modes are supported.

Browser adapter: `resolveFrameworkAssetsBaseUrl` returns the URL derived from `import.meta.url`; `fetchBinary` is `fetch` + `arrayBuffer`; `writeFile`/`readFile` throw `NotSupportedInBrowser`.

Node adapter: `resolveFrameworkAssetsBaseUrl` starts an HTTP server over the package-local `_framework/` directory (reusing the asset-server pattern from `cs-agent-tools/cs-validate/src/wasmsharp/asset-server.ts`); `fetchBinary` supports both HTTP and `file://`; `writeFile`/`readFile` use Node's `fs/promises`.

### 3.4 L8 — @carbide/core public TypeScript API

```ts
export interface CarbideOptions {
  hostAdapter?: HostAdapter;             // injectable; defaults to auto-detected
  disableWebWorker?: boolean;             // browser only
  debugLevel?: -1 | 0 | 1;
  onDownloadProgress?(loaded: number, total: number): void;
  enableDiagnosticTracing?: boolean;
}

export class CarbideSession {
  static initializeAsync(options?: CarbideOptions): Promise<CarbideSession>;
  createProject(options?: ProjectOptions): Project;
  addReference(bytes: Uint8Array, name?: string): ReferenceHandle;
  removeReference(handle: ReferenceHandle): void;
  reset(): void;
  shutdown(): Promise<void>;
}

export interface ProjectOptions {
  targetFramework?: "net8.0" | "net10.0";   // more later
  languageVersion?: string;                  // Roslyn LangVersion
  nullable?: boolean;
  implicitUsings?: boolean;
  defineConstants?: string[];
  assemblyName?: string;
  rootNamespace?: string;
}

export class Project {
  addSource(path: string, code: string): void;
  updateSource(path: string, code: string): void;
  removeSource(path: string): void;
  addReference(handle: ReferenceHandle): void;
  build(): Promise<BuildResult>;             // diagnostics + pe+pdb (or null on failure)
  run(args?: string[], stdin?: string): Promise<RunResult>;
  getDiagnostics(): Promise<Diagnostic[]>;   // no Emit
  getCompletions(path: string, offset: number): Promise<Completion[]>;  // Band B+
}

export interface BuildResult {
  success: boolean;
  diagnostics: Diagnostic[];
  pe?: Uint8Array;
  pdb?: Uint8Array;
  durationMs: number;
}

export interface RunResult {
  success: boolean;
  exitCode?: number;
  stdOut: string;
  stdErr: string;
  uncaughtException?: string;
  durationMs: number;
  diagnostics?: Diagnostic[];     // only populated on compile-during-run failures
}

export interface Diagnostic {
  id: string;
  severity: "error" | "warning" | "info" | "hidden";
  message: string;
  path?: string;
  spanStart: number;
  spanEnd: number;
  lineStart?: number;
  lineEnd?: number;
  columnStart?: number;
  columnEnd?: number;
}
```

All method signatures are designed so calls flow naturally from Python code as well (via a JSON protocol — see §3.7).

### 3.5 L8 — @carbide/cli

A thin Node CLI (`carbide` / `npx carbide`) that wraps `@carbide/core` for out-of-process use. Mirrors the `cs-kit validate` / `cs-kit wasmsharp-compile` surface:

```text
carbide build [--project <path>.csproj] [--source <file.cs>]... [--ref <dll>]... [--tfm net10.0] [--out <dir>]
carbide run   [--project <path>.csproj | --source <file.cs>]  [args...]
carbide validate [--project <path>.csproj | --source <file.cs>]...
```

Output is JSON on stdout by default, with optional `--pretty` and `--format human`. Diagnostics stream to stderr on `--format human` and merge into the JSON under `--format json`.

### 3.6 L3 — @carbide/refs-<tfm>

A small npm package per target framework that ships the reference DLLs. Example: `@carbide/refs-net10.0` contains the extracted contents of `Microsoft.NETCore.App.Ref.10.0.x.nupkg`'s `ref/net10.0/` directory. Consumers who only target one TFM install one package; power users install several.

Alternative considered: include refs inside `@carbide/core`. Rejected because it couples size to TFM choice; users should be able to trim to one TFM.

Optional: a meta-package `@carbide/refs-common` that depends on `refs-net8.0` + `refs-net10.0` for users who don't want to think about it.

### 3.7 L2 — @carbide/nuget

NuGet dependency resolver in TypeScript (decision; see §13.Q.1). Component layout:

```text
@carbide/nuget/
  src/
    flat-container.ts    // HTTP client: versions, download
    registration.ts      // HTTP client: dependency graph info
    nuspec.ts            // zip reader for .nupkg → parse <dependencies>
    version-range.ts     // NuGet version range semantics
    tfm-compat.ts        // TFM compatibility matrix
    resolver.ts          // graph walk, nearest-wins conflict resolution
    cache.ts             // local filesystem cache (Node) / IndexedDB cache (browser)
    allowlist.ts         // published set of supported-managed-only packages
    safety.ts            // detect native assets, source generators, .targets — refuse with reason
  test/
    ...                  // golden cases per package
```

Public API:

```ts
export async function resolve(
  packages: PackageReference[],
  opts: ResolveOptions
): Promise<ResolvedGraph>;

export interface PackageReference { id: string; version: string; }
export interface ResolveOptions {
  targetFramework: string;       // e.g. "net10.0"
  runtimeIdentifier?: string;    // "browser-wasm" by default
  allowListMode: "strict" | "advisory" | "off";
  source?: string;               // defaults to https://api.nuget.org/v3/index.json
  cacheDir?: string;             // Node: filesystem path; browser: IndexedDB DB name
}
export interface ResolvedGraph {
  packages: ResolvedPackage[];
  references: ResolvedReference[];  // flat list of (name, bytes) for L5
  warnings: Warning[];
  lock: ResolveLock;                // reproducible pin across runs
}
```

**Allow-list.** Starts empty and grows by CI-gated additions. Each allowed package is catalogued with: last-verified version, upstream URL, dependency closure, test fixture that builds and runs a program using it. This is the mechanism by which Carbide keeps correctness bounded.

**Safety refusals.** Packages with `runtimes/<rid>/native/` contents, with `build/` transitives that inject MSBuild `.targets`, with `analyzers/` that expect MSBuild project context, or with `ref/` + `lib/` combinations that rely on reference-assembly substitution at runtime — these are *detected at resolve time and refused with a specific reason*, not silently allowed with a warning that nobody reads.

### 3.8 L1 — @carbide/msbuild-lite

A TypeScript port of `cs_kit.msbuild_lite`, usable from the browser host. Purely declarative: given a `.csproj` text and a property-evaluation context, produce a project model. Reuses `cs_kit.msbuild_lite`'s semantics (first-listed TFM selection, simple `Condition` evaluation, glob expansion) so Python and TypeScript callers get the same answer from the same `.csproj`.

In Band C (stretch), `@carbide/msbuild` (no `-lite`) extends this with Directory.Build.props layering and a documented subset of import/property-function handling.

## 4. Data flow: a build call end-to-end

```text
1. Consumer code calls
     session = await CarbideSession.initializeAsync({ targetFramework: "net10.0" });

2. CarbideSession (L8) picks a HostAdapter based on host detection.

3. HostAdapter (L7) readies asset delivery:
   - Browser: records the module base URL.
   - Node: starts an HTTP server and records the base URL.

4. L8 imports dotnet.js from _framework/ and boots the .NET runtime (L6).

5. dotnet.js fetches dotnet.wasm, BCL DLLs (or .webcil files), reference DLLs from the
   URL the host adapter exposes. These come from the package's bundled _framework/.

6. L5 (Carbide.Core) initialises: builds the base MetadataReferenceCache from the
   bundled refs, wires JSExport bindings.

7. Consumer builds up a Project:
     const project = session.createProject();
     project.addSource("Program.cs", "...");
     project.addSource("Helpers.cs", "...");

8. For each source, L8 calls into L5's JSExport AddSource(path, code), which adds a
   Roslyn Document to the Solution.

9. Consumer optionally attaches references:
     const handle = session.addReference(newtonsoftJsonBytes, "Newtonsoft.Json");
     project.addReference(handle);

   If the consumer uses @carbide/nuget, that component resolves the package graph
   offline using the HTTP client and cache, then returns a ResolvedGraph whose
   'references' field feeds session.addReference(...) calls.

10. Consumer calls project.build().
    L8 assembles a JSON request:
      {
        "sources": [{"path":"Program.cs","code":"..."}, ...],
        "references": ["handle-id-1","handle-id-2"],
        "options": {...}
      }
    Calls into L5's JSExport Build(requestJson).

11. L5 picks the Project's Solution, runs Roslyn's GetDiagnosticsAsync, then
    Compilation.Emit(peStream, pdbStream).
    Returns a JSON response:
      {
        "success": true,
        "diagnostics": [...],
        "peBase64": "...",
        "pdbBase64": "...",
        "durationMs": 123
      }

12. L8 decodes the base64 back to Uint8Array and returns the BuildResult to the consumer.

13. Consumer optionally calls project.run(args, stdin). L5 Assembly.Load()s the last
    emitted PE, redirects Console.Out/Err to the HostAdapter's stream captures, invokes
    EntryPoint.Invoke(args), gathers the RunResult.
```

Everything above happens in the consumer's host process. No Carbide-hosted server participates.

## 5. JSExport surface (L5 ↔ L8 boundary)

The JSExport methods Carbide defines, by name and signature. This is the interop contract. Adding or changing a method is a breaking change.

```csharp
public static partial class CarbideInterop
{
    [JSExport] public static Task InitAsync(string[] frameworkAssemblyPaths);

    // Session-level
    [JSExport] public static string CreateSession(string optionsJson);       // returns sessionId
    [JSExport] public static void   DisposeSession(string sessionId);

    // Reference registry
    [JSExport] public static string AddReference(string sessionId, string base64Bytes, string name);
    [JSExport] public static void   RemoveReference(string sessionId, string referenceId);

    // Project-level
    [JSExport] public static string CreateProject(string sessionId, string optionsJson);
    [JSExport] public static void   AddSource(string projectId, string path, string code);
    [JSExport] public static void   UpdateSource(string projectId, string path, string code);
    [JSExport] public static void   RemoveSource(string projectId, string path);
    [JSExport] public static void   AttachReference(string projectId, string referenceId);

    // Build / run
    [JSExport] public static Task<string> GetDiagnosticsAsync(string projectId);   // JSON
    [JSExport] public static Task<string> BuildAsync(string projectId);            // JSON
    [JSExport] public static Task<string> RunAsync(string projectId, string argsJson, string stdinText);

    // Completions (Band B)
    [JSExport] public static Task<string> GetCompletionsAsync(string projectId, string path, int caret);
    [JSExport] public static Task<bool>   ShouldTriggerCompletionsAsync(string projectId, string path, int caret);
}
```

Returning strings rather than structured objects keeps the JSExport boundary narrow and debuggable. JSON payloads have versioned schemas in `@carbide/core`'s TypeScript source; mismatched schema versions throw at the boundary with a clear error.

## 6. The execution service in L5

`RunAsync`'s contract with the outer world:

- **Entry point discovery.** Use Roslyn's `Compilation.GetEntryPoint(CancellationToken)` rather than assembly-level reflection. This fixes the top-level-statements-vs-explicit-Main bug surface observed in WasmSharp (see [WasmSharp#5](https://github.com/JakeYallop/WasmSharp/issues/5)), because Roslyn is the authority on entry-point binding.
- **Argument passing.** Accept a JSON array of strings, convert to `string[]`, and invoke `EntryPoint.Invoke(null, new object[] { args })`. If the entry point takes `Task<int>` or `int`, await/return the result; if it takes `void` or `Task`, use exit code 0 on success.
- **stdin.** Carbide wraps a `StringReader` over the supplied stdin text and sets `Console.In`. No interactive stdin support in v1 (streaming stdin in Band C).
- **stdout/stderr capture.** Stream to the HostAdapter's capture helpers via wrapper `TextWriter`s that forward each `Write`/`WriteLine`. Buffer the final text for the consumer's `RunResult`.
- **Timeout.** Each `RunAsync` call carries a deadline; if the program exceeds it, `Carbide.Core` returns a `RunResult` with `success: false, uncaughtException: "timeout"`. (Mono-WASM is single-threaded, so cooperative termination only; a tight infinite loop cannot be preempted. Document this as a known limit.)
- **Uncaught exceptions.** `ex.ToString()` into `uncaughtException`, mirrored into stderr, exit code non-zero. No process-level crash.
- **Assembly loading.** `Assembly.Load(byte[])` each call. No AppDomain isolation (Mono-WASM doesn't support multiple AppDomains). A `Reset` on the session drops the cached compilations, but loaded assemblies linger until the process tears down. Document this; users who need isolation per-run can reinit the session.

## 7. Webcil packaging

Webcil is .NET's wire format for managed assemblies served over HTTP; it avoids the `.dll` MIME-type and antivirus issues some hosting providers impose. .NET 9+ defaults to Webcil in Blazor WebAssembly (`<WasmEnableWebcil>true</WasmEnableWebcil>`). WasmSharp disables it with a TODO because DLL loading paths break.

Carbide's position: Webcil support is Band B, not v1. Implementation plan:

- **Runtime side**: Leave `<WasmEnableWebcil>false</WasmEnableWebcil>` for v1 (like WasmSharp). For Band B, flip it and absorb the downstream changes; the .NET tooling is the source of the Webcil bytes, so this is more a deployment-pipeline change than a C# code change.
- **Reference side**: When we ship `@carbide/refs-<tfm>` packages, provide them in both forms (`ref.webcil`, `ref.dll`) and let the host adapter pick based on `content-type` tolerance. In Node, always use `.dll` (no MIME problem); in browser, prefer `.webcil` if the current host environment suggests it.
- **Loader side**: Build `WasmMetadataReferenceResolver` to recognise both extensions and load either. Roslyn's metadata reader accepts PE bytes regardless of container format, so the loader's job is just "fetch the right URL and pass the inner PE stream".

Webcil compatibility is flagged as "optional, on the roadmap" in v1 documentation, "supported" in v2.

## 8. Python consumer path

Carbide does not need a Python layer; the Node CLI is sufficient for scripted callers. However, `cs-agent-tools` (Python) will remain an important consumer. The proposal for `cs_kit`:

- Add two new commands: `cs-kit build` and `cs-kit run`.
- Each spawns `npx carbide build` / `npx carbide run` as a subprocess, reads JSON from stdout, forwards structured output as the command's result.
- Auto-bootstrap: the first invocation runs `npm install @carbide/core @carbide/cli` (and optionally `@carbide/refs-net10.0`) inside a vendored Node project at `src/cs-agent-tools/carbide/` — mirroring how `cs-validate` is bootstrapped today.
- The existing `wasmsharp-compile` command stays; optionally switch it to delegate to `carbide` once parity is proven.

No Python-specific code lives inside the Carbide project. All Python-side work is in `cs-agent-tools`.

## 9. Implementation plan

Milestones are ordered by dependency. Each milestone has a crisp acceptance test; a milestone is "done" when its test is green on both browser and Node hosts.

### M0 — Fork skeleton

- Create `src/Carbide/` with a Blazor WASM core project, `Microsoft.NET.Sdk.WebAssembly`, pinned to the same package versions WasmSharp uses.
- Bring across WasmSharp's `MetadataReferenceCache`, `WasmMetadataReferenceResolver`, `roots.xml`, and `Host.cs` DI scaffold. Rename namespaces to `Carbide.Core`.
- Create `@carbide/core` npm package skeleton with TypeScript build and a placeholder exports list.
- Publish-candidate build produces a working `_framework/` directory.

**Acceptance.** `dotnet publish -c Release` emits `_framework/`; npm pack produces a valid tarball; a sanity TypeScript test imports the package and prints "Carbide initialised" in both Node and a headless-browser (Playwright) harness.

### M1 — Single-file parity with WasmSharp

- Port `CodeSession` into `Carbide.Core.Services.ProjectCompiler` but keep it single-document for now.
- Wire up JSExport `InitAsync`, `CreateSession`, `CreateProject`, `AddSource`, `GetDiagnosticsAsync`, `RunAsync`.
- Write host adapters for browser and Node (Layer 7).
- Port (from `cs-agent-tools/wasmsharp/wasmsharp_compile.mjs`) the Node asset server as a reusable module.
- Capture Console.Out in run.

**Acceptance.** A test calls `createProject(); addSource("Program.cs", "Console.WriteLine(\"hello\");"); build(); run()`, gets `success: true, stdOut: "hello\n"`. Runs identically in Node and headless Chromium.

### M2 — Multi-document

- Replace single-`DocumentId` storage in `ProjectCompiler` with a path-keyed dictionary.
- Implement `UpdateSource`, `RemoveSource` via `Solution.WithDocumentText` / `.RemoveDocument`.
- Update JSExport schemas to carry arbitrary source paths.
- Ensure diagnostics carry the source path in their `path` field.

**Acceptance.** A multi-file fixture (Program.cs + Helper.cs with a class used by Program) builds and runs; introducing a type error in Helper.cs yields a diagnostic whose path equals `Helper.cs`.

### M3 — Reference DLL injection

- Add the reference registry in `Carbide.Core` (`AddReference(bytes, name)` → id).
- Extend `WasmMetadataReferenceResolver` to look up both the bundled cache and the registry when resolving references for a compilation.
- Expose `AddReference` / `AttachReference` on the TS API.
- Publish `@carbide/refs-net10.0` npm package with extracted reference DLLs from the pinned `Microsoft.NETCore.App.Ref` version. Build step of Carbide fetches the `.nupkg` and extracts refs.
- Wire Node CLI `carbide build --ref` flag.

**Acceptance.** A test that references a vendored DLL (e.g. a built helper library produced ahead of time) compiles and runs successfully.

### M4 — PE emission & artefact output

- JSExport `BuildAsync` returns PE + PDB bytes separately from Run.
- Node CLI `carbide build --out <dir>` writes `Project.Assembly.dll` and `Project.Assembly.pdb`.
- Browser `Project.build()` returns `Uint8Array` to the consumer, who can wrap in a `Blob` for download.

**Acceptance.** CLI `carbide build --source Program.cs --out out/` produces `out/Program.dll` whose bytes load successfully when fed back as a reference to a subsequent `carbide build`.

### M5 — Project-file input

- Port relevant parts of `cs_kit.msbuild_lite` to TypeScript as `@carbide/msbuild-lite`.
- Node CLI `carbide build --project Foo.csproj` uses it to parse, glob-expand compile items, derive options.
- Hook `.csproj`-derived options through to `ProjectOptions` and then to Roslyn's `CSharpCompilationOptions` / `CSharpParseOptions`.

**Acceptance.** Given `Foo.csproj` + its referenced `.cs` files, `carbide build --project Foo.csproj` produces the same PE bytes as `carbide build --source ...` with equivalent options flattened.

### M6 — NuGet resolver

- Implement `@carbide/nuget`: flat-container client, registration-v3 client, nuspec reader, version-range evaluator, TFM-compat matrix, nearest-wins resolver.
- Define the allow-list; seed with 10 managed-only packages (see vision §7 for the seed list).
- Implement safety refusals for native assets, `.targets`, analyzers, source generators.
- Add fixture tests for each allow-listed package: resolve → build a program that uses it → run → check expected output.
- Generate a `carbide.lock.json` recording the resolved graph.

**Acceptance.** For each allow-listed package, `carbide build --project` succeeds and the built program runs. Lock-file round-trip: replay `carbide build --lock carbide.lock.json` without network and gets the same result.

### M7 — Published API + stability lock

- Public TypeScript types get semver-stable exports; JSON schemas get version fields and compat tests.
- CHANGELOG automation; API-diff report on every PR.
- Release `0.1.0` as "Band A + partial Band B" (S1–S3 firm, S4 with allow-list, S5 pending).

**Acceptance.** A compatibility-freeze test harness pins the JSON schemas and TypeScript types at 0.1.0; subsequent PRs that break them are caught by the harness.

### M8 — Webcil mode (optional)

- Flip `<WasmEnableWebcil>true</WasmEnableWebcil>` in `Carbide.Core.csproj`; rebuild the `_framework/` pipeline.
- Teach the reference resolver to load `.webcil` alongside `.dll`.
- Update `@carbide/refs-<tfm>` packages to include both forms; let the browser adapter pick based on content-type.

**Acceptance.** Both browser (served from a Webcil-preferring CDN fixture) and Node builds pass the M1–M4 acceptance tests.

### M9 — Shape S5: project-to-project references

- Extend `CreateProject` to accept a list of sibling-project references.
- In the resolver, build a compile order based on `ProjectReference` edges; compile each project; emit PEs into a per-session temp cache; feed sibling-project PEs as references to the root project.

**Acceptance.** A two-project fixture (lib + app) builds via a single top-level `.csproj` that references the lib project.

### M10 — WASI execution target (Band C, stretch)

- Package `Carbide.Core` for `wasi-wasm` runtime in addition to `browser-wasm`.
- Add `@carbide/core-wasi` sibling package.
- New `carbide run --via wasi` flag that invokes wasmtime with the WASI build.

**Acceptance.** A program that uses `System.IO.File` (mounted to a host directory by wasmtime policy) builds in `@carbide/core` and runs in `@carbide/core-wasi`.

### M11 — Partial MSBuild evaluator (Band C, stretch)

- Grow `@carbide/msbuild-lite` into `@carbide/msbuild` with Directory.Build.props layering and a documented subset of imports.
- Explicit refusal surface for unsupported constructs.

**Acceptance.** A fixture `.csproj` tree that uses `Directory.Build.props` to set `Nullable` centrally builds identically to the equivalent flattened `.csproj`.

### M12 — Source-generator subset (Band C, stretch)

- Accept analyzer/generator DLLs as references with a `kind: "analyzer"` flag.
- Plumb them into Roslyn's `CSharpGeneratorDriver`.
- Refuse generators that load user-assembly types at generate time (reflection-into-target-project generators) and generators that touch I/O.

**Acceptance.** A program using a simple source generator (e.g. a minimal `[GenerateToString]`-style generator) builds, and the emitted program runs.

### M13 — Custom compiler driver (Band C, last resort)

Only if upstream Roslyn changes in a way that makes its packages incompatible with the Mono-WASM build we target. Implementation: a Carbide-owned thin driver that calls `CSharpCompilation.Create` directly from a hand-picked subset of Roslyn's public API, against a trimmed profile. Effort is medium; risk is long-tail correctness. Defer until forced.

## 10. Testing strategy

Tests are mandatory gates on every acceptance milestone and on every release.

**Unit tests (C# side).**

- Roslyn options round-trip: `CSharpCompilationOptions` built from `ProjectOptions` JSON reproduces the expected configuration.
- Reference registry: add / remove / compile invariants.
- Emit + reload: emitted PE bytes can be loaded as a `MetadataReference` and used in a subsequent compile.

**Unit tests (TS side).**

- Host adapter selection: Node host produces an asset server URL; browser host produces a module-URL-derived base.
- JSON schema version checks.
- CLI argument parsing; end-to-end CLI tests via a vendored small fixture set.

**Integration tests (both hosts).**

- Golden corpus of ≥ 50 small C# programs (see vision §9). Each has an expected stdout. The build-and-run path produces matching stdout on Carbide in both host topologies.
- Allow-list NuGet tests: one test per allow-listed package; builds and runs a program that exercises the package's headline API.
- Diagnostics tests: a corpus of known-erroneous programs with expected `(id, message, path, line)` tuples.
- Performance tests: gate that cold Node init ≤ 5 s, warm build ≤ 500 ms on reference hardware.
- Size regression: a CI job that fails if `@carbide/core`'s compressed size exceeds the budget.

**Browser-host tests** run against Playwright-launched headless Chromium and Firefox.

**Reproducibility tests.**

- Build-reproducibility: two builds of Carbide on the same commit produce byte-identical `_framework/` contents.
- Lock-reproducibility: resolving the same `PackageReference` graph twice, once online and once offline (from cache + lock), yields identical resolved-reference bytes.

## 11. Supply chain and maintenance

**Inbound.** Carbide pins its .NET SDK, Roslyn NuGets, and reference-pack `Microsoft.NETCore.App.Ref` version explicitly; upgrades are visible PRs. No transitive floating versions. `package-lock.json` committed for all npm packages.

**Outbound.** Carbide produces npm packages with integrity hashes, published from a release pipeline that also emits an SBOM listing every bundled NuGet package and BCL assembly. The SBOM attaches to the GitHub release.

**Upstream tracking.** A periodic upstream-drift report in `src/Carbide/docs/drift/` tracks: Roslyn newest version vs. Carbide-pinned, .NET runtime newest LTS/STS vs. Carbide-targeted, any new issues in `dotnet/runtime` or `dotnet/roslyn` labelled against WASM/browser.

**Fork of WasmSharp.** Day-one relationship: Carbide *uses* WasmSharp as inspiration and may *vendor code* from it. It does not *depend* on the `@wasmsharp/core` npm package in its shipped artefact. An internal ATTRIBUTION file records each vendored piece with its upstream path and commit. WasmSharp's Apache-2.0 license is compatible.

## 12. Risks and mitigations

| # | Risk | Likelihood | Impact | Mitigation |
|---|------|-----------|-------:|-----------|
| R1 | Bundling Roslyn 4.14 into Carbide's WASM hits a trimming bug that WasmSharp has already worked around silently. | Medium | Medium | Reuse `roots.xml` verbatim from WasmSharp at M0; add our own test harness early; expect to grow the trim-roots list. |
| R2 | The NuGet resolver's long-tail correctness is hard to bound. | High | High | Allow-list gating; refuse unknown packages in strict mode; commit to an advisory mode only for v2+. |
| R3 | Webcil migration breaks reference loading late in the roadmap. | Medium | Medium | Keep `.dll` fallback indefinitely; don't remove it. |
| R4 | Browser-host memory pressure with realistic multi-file projects. | Medium | Medium | Monitor with size/perf gates; allow WebWorker-backed runtime to isolate from main thread; consider `_framework/` pruning. |
| R5 | Single-maintainer `@wasmsharp/core` upstream stalls on .NET N+1. | High | Low (after fork) | Fork at M0; do not depend on the published npm package in Carbide's shipped artefact. |
| R6 | `dotnet publish` output format changes in a future SDK (file renames in `_framework/`). | Medium | Medium | Pin SDK version; surface the file list in CI as a generated artefact; compare against golden list on every build. |
| R7 | Mono-WASM single-thread makes some programs behave differently than on `dotnet run` on the host. | High | Low (documented) | Explicit documentation of the threading model; a clearly-labelled "documented differences" page. |
| R8 | Source generators / analyzers in Band C surface Roslyn internals that aren't stable. | Medium | Medium | Defer; if accepted, sandbox generator execution to a capability list. |
| R9 | npm namespace `@carbide` may already be taken. | Low | Low | Check before publishing; choose a different namespace (or the renamed project's namespace) if occupied. |
| R10 | `Microsoft.NETCore.App.Ref` licensing terms require redistribution-specific notices. | Low | Medium | Include the MS-EULA notice text in `THIRD_PARTY_NOTICES.md`; verify with owner before first public publish. |

## 13. Open questions (architecture positions)

The vision document flagged five open questions. The architecture takes a position on each:

- **Q.1 Resolver language.** *Position:* TypeScript (`@carbide/nuget`), because it runs in both hosts and avoids an out-of-band Python step in the browser path. *Alternative:* Python for velocity of first implementation, then port. *Revisit* when the resolver's shape is proven on the allow-list.
- **Q.2 Reference passing.** *Position:* Bytes, always. Consumers who have URLs fetch first and then pass bytes. Keeps the JSExport surface trivial and the browser-vs-Node behaviour identical.
- **Q.3 Trim profile.** *Position:* Trimmed runtime assemblies (inherit WasmSharp's profile + the `roots.xml` extensions Carbide grows), untrimmed reference-pack refs (they're small already). If trimming the reference pack saves meaningful size (>10 MB) in practice, revisit.
- **Q.4 Stream vs buffer console output.** *Position:* Both. Default is buffered (simpler contract). Passing an `onStdout(chunk)` callback to `project.run()` opts into streaming.
- **Q.5 Capability discovery.** *Position:* Out of v1 entirely. Programs fail with a documented exception when they use a capability that isn't supported. Revisit in Band C.

Further open questions these documents don't take a position on yet (deliberately):

- **Identity / versioning of diagnostics that Carbide synthesises outside Roslyn.** Carbide's own diagnostic IDs (e.g., the NuGet resolver's refusal messages) need an ID namespace that doesn't collide with Roslyn `CSxxxx`. Proposal `CRBxxxx`, but this is editorial.
- **Signing of `@carbide/core` npm package.** npm provenance (sigstore) support is recent; adopting it is low cost and worth doing, but orthogonal to the architecture.
- **Hosting of reference packs.** Published via npm is easy but adds npm install cost. Alternative: CDN-hosted and cached in IndexedDB / Node fs. The current position is "npm for simplicity", but a CDN path is a straight extension.

## 14. Appendix A — Delta against WasmSharp

For reviewers familiar with `lib/WasmSharp/packages/core/src/`. Every Carbide deviation from the upstream reference has a rationale.

| WasmSharp component | Carbide treatment | Why |
|---|---|---|
| `CodeSession.cs` (single-document) | Replaced with `ProjectCompiler` (multi-document) | Gap #7.1 in the feasibility report. |
| `CompilationInterop.cs` JSExport surface (3 methods) | Extended to ~12 methods covering sessions, projects, references, build, run, completions | Required for the expanded shape. |
| `MetadataReferenceCache.cs` (fixed cache) | Kept for the BCL refs; extended with a sibling `ReferenceRegistry` for user refs | Gap #7.2. |
| `WasmMetadataReferenceResolver.cs` | Extended to consult the registry as well as the cache | Reference injection. |
| `WasmSolution.cs` | Renamed + expanded to `SessionSolutions`; owns multiple compilations | Multi-project-per-session path for S5. |
| `roots.xml` trim roots | Inherited; Carbide expects to add its own entries over time | Likely new types used by our additional code. |
| `Host.cs` DI (Jab) | Inherited, extended | Good bones. |
| `<WasmEnableWebcil>false</WasmEnableWebcil>` | Flip to `true` at M8 | Band B goal. |
| `CompilationInterop.ts` (3-method consumer API) | Replaced by structured `CarbideSession` / `Project` classes | Gap #7.3, §3.4. |
| Web Worker (`worker.ts`) | Inherit; extend to also work as a Node Worker Thread | Dual-host parity. |
| `CompletionService` | Preserve for Band B | Worth keeping; users may grow into it. |
| Top-level-statements vs Main bug ([WasmSharp#5](https://github.com/JakeYallop/WasmSharp/issues/5)) | Avoid by using `Compilation.GetEntryPoint` instead of assembly reflection | Vision §6 "documented differences" exits. |

## 15. Appendix B — What Carbide is explicitly *not* going to re-implement

To honour the vision's non-goal #6 ("not a Roslyn reimplementation") and to keep scope finite, Carbide will consume, not rewrite, the following. Any future proposal to change this needs a decision record.

- **The C# parser and binder** — use `Microsoft.CodeAnalysis.CSharp`. Pin the version; upgrade deliberately.
- **The C# metadata reader** — use Roslyn's built-in reader. Do not parse PE format ourselves.
- **The PE writer** — use `Compilation.Emit`. Do not try to emit PE bytes by hand.
- **The .NET runtime** — use the official Mono-WASM runtime produced by `dotnet publish`.
- **The trimmer** — use `illink` via `dotnet publish`. Do not attempt alternative trimming.
- **The BCL** — use what the pinned runtime provides. Do not write our own `System.IO`, `System.Text`, or `System.Linq`.
- **The NuGet v3 protocol** — consume the documented endpoints. Do not invent our own package format.
- **The portable PDB format** — use what Roslyn emits. Do not invent debug formats.

Carbide's implementation surface is: session + project management, multi-document orchestration, reference registry, JSExport interop, host adapters, resolver, CLI. That is a bounded, human-sized codebase by design.
