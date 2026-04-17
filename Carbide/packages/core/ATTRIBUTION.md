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
| `src/Hosting/Host.cs` | `packages/core/src/Hosting/Host.cs` — trimmed to the M0 subset (logger singleton only; session dispatch helpers land in M1). |
| `src/Hosting/WebAssemblyConsoleLogger.cs` | `packages/core/src/Hosting/WebAssemblyConsoleLogger.cs` — namespace moved to `Carbide.Core.Hosting`. |
| `src/Services/MetadataReferenceCache.cs` | `packages/core/src/Services/MetadataReferenceCache.cs` — namespace rename only. |
| `src/Services/MetadataReferenceResolver.cs` | `packages/core/src/Services/MetadataReferenceResolver.cs` — namespace rename only. |
| `src/Services/WasmMetadataReferenceResolver.cs` | `packages/core/src/Services/WasmMetadataReferenceResolver.cs` — namespace rename only. |

`WebAssemblyConsoleLogger` itself is originally from [`dotnet/aspnetcore`](https://github.com/dotnet/aspnetcore) (MIT) and WasmSharp's copy carries the MIT attribution. Carbide preserves that attribution in the file header.
