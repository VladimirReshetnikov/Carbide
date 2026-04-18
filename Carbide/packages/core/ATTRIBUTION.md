# Attribution

Files in this package adapted from upstream sources. Carbide retains the original licenses and surfaces changes here so the derivation is traceable.

## From WasmSharp (Apache-2.0)

Upstream: https://github.com/JakeYallop/WasmSharp — vendored snapshot at `lib/WasmSharp/` in this repository.

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
| `src/Services/ProjectCompiler.cs` | `packages/core/src/Services/CodeSession.cs` — M1 rename + extensions: use `Compilation.GetEntryPoint` (fixes WasmSharp#5), handle `Task`/`Task<int>`/`int`/`ValueTask` return types, capture stderr alongside stdout, ship a hidden implicit-usings document so bare `Console.WriteLine` compiles. |
| `src/Services/SessionSolutions.cs` | `packages/core/src/Services/WasmSolution.cs` — M1 rename + extensions: explicit session layer, metadata references loaded from explicit URL list. |
| `src/Services/Diagnostic.cs` | `packages/core/src/Services/Diagnostic.cs` — extended with path, line/column, and string severity for the JSON boundary. |
| `src/Services/DiagnosticCollectionExtensions.cs` | `packages/core/src/Services/DiagnosticCollectionExtensions.cs` — rewritten to populate extended Diagnostic fields. |
| `src/Services/DocumentOptions.cs` | `packages/core/src/Services/DocumentOptions.cs` — namespace rename only. |
| `src/Services/RunResult.cs` | `packages/core/src/Services/RunResult.cs` — extended with `ExitCode`, `UncaughtException`, `DurationMs`, `SchemaVersion`. |
| `src/Services/Tracer.cs` | `packages/core/src/Services/Tracer.cs` — namespace rename only. |
| `src/CompilationInterop.cs` | `packages/core/src/CompilationInterop.cs` — rewritten to the M1 JSExport surface (InitAsync, CreateSession, DisposeSession, CreateProject, AddSource, GetDiagnosticsAsync, RunAsync) with a source-generated `JsonSerializerContext`. |

## From cs-agent-tools

Upstream: `src/cs-agent-tools/cs-validate/src/wasmsharp/asset-server.ts` in this repository.

| Carbide file | Upstream | Notes |
|---|---|---|
| `src/ts/host/node/asset-server.ts` | `cs-validate/.../asset-server.ts` | Security posture (path-traversal guard, null-byte rejection, MIME by extension) preserved. Logger hook added; otherwise behaviour matches. |

## WebAssemblyConsoleLogger origin

`WebAssemblyConsoleLogger` is originally from [`dotnet/aspnetcore`](https://github.com/dotnet/aspnetcore) (MIT) and WasmSharp's copy carries the MIT attribution. Carbide preserves that attribution in the file header.
