# Attribution

This record identifies source adapted from third-party and predecessor projects. Carbide-owned work, including code originating in the predecessor Tools repository, is Apache-2.0; identified third-party material retains its applicable upstream terms.

## From WasmSharp (Apache-2.0)

Upstream: https://github.com/JakeYallop/WasmSharp at commit [`2f8c93bfa39f2068ad932a748ba23f740075327c`](https://github.com/JakeYallop/WasmSharp/tree/2f8c93bfa39f2068ad932a748ba23f740075327c). Carbide's initial C# core sources were selected from that exact upstream snapshot; the table below records the imported files and later adaptations.

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

## From the predecessor Tools repository

Before Carbide was extracted into this standalone repository, these sources lived at `src/cs-agent-tools/cs-validate/src/wasmsharp/asset-server.ts` and `src/cs-agent-tools/src/cs_kit/msbuild_lite.py` in the predecessor Tools repository. The paths below record that pre-extraction provenance and are not paths in the current checkout.

| Carbide file | Upstream | Notes |
|---|---|---|
| `src/ts/host/node/asset-server.ts` | `cs-validate/.../asset-server.ts` | Security posture (path-traversal guard, null-byte rejection, MIME by extension) preserved. Logger hook added; otherwise behaviour matches. |
| `packages/msbuild-lite/` | `cs_kit/msbuild_lite.py` | Python-to-TypeScript semantic port of the bounded `.csproj` parser. Same supported subset (TFM/Nullable/LangVersion/ImplicitUsings/DefineConstants/AssemblyName/RootNamespace, `PackageReference`/`ProjectReference` capture, `Compile` globs, simple `Condition` forms, `MSBLITE000`–`MSBLITE014` warning codes). Parity is enforced via fixtures under `packages/msbuild-lite/test/parity/`. |

## From .NET runtime and ASP.NET Core (MIT)

These files retain the .NET Foundation's MIT terms and attribution headers. The full notice is reproduced in [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).

| Carbide file | Upstream | Notes |
|---|---|---|
| `src/Terminal/KeyParser.cs` | [`dotnet/runtime` `System.Console/src/System/IO/KeyParser.cs` at .NET 10.0.0](https://github.com/dotnet/runtime/blob/60629d14374c56f1cb51819049ad1fa529307f8d/src/libraries/System.Console/src/System/IO/KeyParser.cs) | Adapted port with namespace, visibility, and terminfo-shim substitutions documented in the file header. |
| `src/ts/runtime/dotnet-types.ts` | `dotnet/runtime` generated `dotnet.d.ts` | Hand-maintained subset of the public Mono-WASM host definitions, reduced to the fields Carbide consumes and extended with Emscripten output overlays. |
| `src/Hosting/WebAssemblyConsoleLogger.cs` | [`dotnet/aspnetcore` `WebAssemblyConsoleLogger.cs`](https://github.com/dotnet/aspnetcore/blob/da6c314d76628c1f130f76ed3e55f1d39057e091/src/Components/WebAssembly/WebAssembly/src/Services/WebAssemblyConsoleLogger.cs) | Adapted first by WasmSharp and then by Carbide; the MIT attribution is preserved in the file header. |
| `../core-bcl/System.Console/` | [`dotnet/runtime` `System.Console` at .NET 10.0.0](https://github.com/dotnet/runtime/tree/60629d14374c56f1cb51819049ad1fa529307f8d/src/libraries/System.Console) | Browser-specific fork with copied, adapted, and newly implemented files. The file-level MIT scope is reproduced in this package's [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md); the sibling source tree also carries its own detailed provenance record. |
