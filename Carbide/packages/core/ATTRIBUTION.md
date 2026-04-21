# Attribution

Files in this package adapted from upstream sources. Carbide retains the original licenses and surfaces changes here so the derivation is traceable.

## From WasmSharp (Apache-2.0)

Upstream: https://github.com/JakeYallop/WasmSharp.

| Carbide file | WasmSharp origin |
|---|---|
| `src/Carbide.Core.csproj` | `packages/core/src/WasmSharp.Core.csproj` — pinned package versions kept, TypeScript build targets stripped (npm scripts own TS build). |
| `src/Program.cs` | `packages/core/src/Program.cs` — namespace rename only. |
| `src/roots.xml` | `packages/core/src/roots.xml` — verbatim. |
| `src/runtimeconfig.template.json` | `packages/core/src/runtimeconfig.template.json` — verbatim. |
| `src/Properties/launchSettings.json` | `packages/core/src/Properties/launchSettings.json` — launch profile renamed. |
| `src/Hosting/Host.cs` | `packages/core/src/Hosting/Host.cs` — M1 restored the session-dispatch helpers and registers `SessionSolutions`. |
| `src/Hosting/WebAssemblyConsoleLogger.cs` | `packages/core/src/Hosting/WebAssemblyConsoleLogger.cs` — namespace moved to `Carbide.Core.Hosting`. |
| `src/Services/MetadataReferenceCache.cs` | `packages/core/src/Services/MetadataReferenceCache.cs` — namespace rename only. |
| `src/Services/MetadataReferenceResolver.cs` | `packages/core/src/Services/MetadataReferenceResolver.cs` — namespace rename only. |
| `src/Services/WasmMetadataReferenceResolver.cs` | `packages/core/src/Services/WasmMetadataReferenceResolver.cs` — M1 extended: switched to `CreateFromImage` with full-stream copy, added `PEReader.HasMetadata` pre-check so trimmed-facade DLLs don't surface as CS0009. |
| `src/Services/ProjectCompiler.cs` | `packages/core/src/Services/CodeSession.cs` — M1 rename + extensions: use `Compilation.GetEntryPoint` (fixes WasmSharp#5), handle `Task`/`Task<int>`/`int`/`ValueTask` return types, capture stderr alongside stdout, ship a hidden implicit-usings document so bare `Console.WriteLine` compiles. M2 extensions: path-keyed `Dictionary<string, DocumentId>` for multi-document, `AddSource` / `UpdateSource` / `RemoveSource` with reserved-path guard, `OpenDocument` dropped to stop workspace leaks. M3 extensions: per-project attached-reference set, `AttachReference` / `DetachReference`, RunAsync preloads attached reference bytes via `Assembly.Load` + hooks `AppDomain.AssemblyResolve` so the user's PE can resolve its type references at JIT time. M4 extensions: `BuildAsync` returns PE + portable-PDB bytes without execution; shared `TryGetErrorFreeCompilationAsync(outputKind)` helper switches OutputKind (DynamicallyLinkedLibrary for build, ConsoleApplication for run); source `SourceText` uses UTF-8 encoding so portable-PDB emission doesn't surface CS8055. M5 extensions: auto-selects `OutputKind.ConsoleApplication` when any syntax tree contains top-level statements (so csproj projects with `Console.WriteLine` at file scope still build under `Build()`); ImplicitUsings-aware GlobalUsings injection can be toggled via `DocumentOptions.ImplicitUsings=false`. |
| `src/Services/SessionSolutions.cs` | `packages/core/src/Services/WasmSolution.cs` — M1 rename + extensions: explicit session layer, metadata references loaded from explicit URL list. M2: Update/Remove wrappers delegated to ProjectCompiler. M3: per-session `ReferenceRegistry`; `AddReference` / `RemoveReference` (with auto-detach from attached projects) / `AttachReference` wrappers. |
| `src/Services/ReferenceRegistry.cs` | New in M3 (no WasmSharp analogue). Thread-safe GUID-keyed registry of PE-byte metadata references with synchronous `HasManagedMetadata` validation at `Add`. Stores raw bytes alongside the `MetadataReference` so `ProjectCompiler.RunAsync` can `Assembly.Load` attached references into the AppDomain. |
| `src/Services/Diagnostic.cs` | `packages/core/src/Services/Diagnostic.cs` — extended with path, line/column, and string severity for the JSON boundary. |
| `src/Services/DiagnosticCollectionExtensions.cs` | `packages/core/src/Services/DiagnosticCollectionExtensions.cs` — rewritten to populate extended Diagnostic fields. |
| `src/Services/DocumentOptions.cs` | `packages/core/src/Services/DocumentOptions.cs` — namespace rename + M5 extensions: `Deterministic=true` on `DefaultCompilationOptions` (so two builds with the same inputs produce byte-identical PE), new `ImplicitUsings` / `AssemblyName` / `RootNamespace` init properties plumbed from `ProjectOptions`. |
| `src/Services/RunResult.cs` | `packages/core/src/Services/RunResult.cs` — extended with `ExitCode`, `UncaughtException`, `DurationMs`, `SchemaVersion`. |
| `src/Services/Tracer.cs` | `packages/core/src/Services/Tracer.cs` — namespace rename only. |
| `src/CompilationInterop.cs` | `packages/core/src/CompilationInterop.cs` — rewritten to the M1 JSExport surface (InitAsync, CreateSession, DisposeSession, CreateProject, AddSource, GetDiagnosticsAsync, RunAsync) with a source-generated `JsonSerializerContext`. M2: extended with `UpdateSource` and `RemoveSource` JSExports; JSON schema unchanged. M3: extended with `AddReference` / `RemoveReference` / `AttachReference` JSExports (base64 byte transport). M4: added `BuildAsync` JSExport returning a `BuildResultDto` with base64 `peBase64` / `pdbBase64` fields. M5: `ProjectOptionsDto` grows `DefineConstants`; `BuildDocumentOptions` maps it through `CSharpParseOptions.WithPreprocessorSymbols`; schema version bumped to 2 (both 1 and 2 accepted so old clients keep working). |
| `src/Services/BuildResult.cs` | New in M4 (no WasmSharp analogue). Structured outcome of `ProjectCompiler.BuildAsync`: PE bytes, portable-PDB bytes, diagnostics, duration. Mirrors `RunResult`'s shape so consumers can branch uniformly. |

## From cs-agent-tools

Upstream: `src/cs-agent-tools/cs-validate/src/wasmsharp/asset-server.ts` in this repository, and `src/cs-agent-tools/src/cs_kit/msbuild_lite.py`.

| Carbide file | Upstream | Notes |
|---|---|---|
| `src/ts/host/node/asset-server.ts` | `cs-validate/.../asset-server.ts` | Security posture (path-traversal guard, null-byte rejection, MIME by extension) preserved. Logger hook added; otherwise behaviour matches. |
| `packages/msbuild-lite/` | `cs_kit/msbuild_lite.py` | Python-to-TypeScript semantic port of the bounded `.csproj` parser. Same supported subset (TFM/Nullable/LangVersion/ImplicitUsings/DefineConstants/AssemblyName/RootNamespace, `PackageReference`/`ProjectReference` capture, `Compile` globs, simple `Condition` forms, `MSBLITE000`–`MSBLITE014` warning codes). Parity is enforced via fixtures under `packages/msbuild-lite/test/parity/`. |

## WebAssemblyConsoleLogger origin

`WebAssemblyConsoleLogger` is originally from [`dotnet/aspnetcore`](https://github.com/dotnet/aspnetcore) (MIT) and WasmSharp's copy carries the MIT attribution. Carbide preserves that attribution in the file header.
