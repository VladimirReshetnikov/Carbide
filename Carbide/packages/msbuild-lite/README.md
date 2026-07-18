# @carbide/msbuild-lite

Bounded `.csproj` parser for Carbide. Produces a structured `ProjectModel` from an MSBuild-style project file, covering the subset Carbide cares about: target framework, common properties, `PackageReference` / `ProjectReference` capture, and `Compile` item globs.

## Scope

This is a semantic port of `src/cs-agent-tools/src/cs_kit/msbuild_lite.py` — the two parsers are meant to produce the same output for the same input. Parity is enforced by a shared fixture corpus under `test/parity/`.

### Supported

- `<TargetFramework>` / `<TargetFrameworks>` (semicolon list; "first-listed" selection).
- `<Nullable>`, `<LangVersion>`, `<ImplicitUsings>`, `<DefineConstants>`, `<AssemblyName>`, `<RootNamespace>`, `<EnableDefaultCompileItems>`.
- `<PackageReference Include="…" Version="…"/>` (captured; the parser itself does not resolve packages, but `@carbide/cli` now hands them to `@carbide/nuget`).
- `<ProjectReference Include="…"/>` (captured; `@carbide/cli`'s project-graph walker now consumes the list and builds each sibling csproj in topological order).
- `<Compile Include="…"/>` and `<Compile Remove="…"/>` (glob expansion with `**` and `*`).
- `Condition=" '$(X)' == 'Y' "`, `!=`, `and`, `or`. Unparseable conditions are treated as "applies = true" with a warning.
- Default `.cs` discovery under the project directory, excluding `bin/`, `obj/`, `.git/`, and other dotted directories.
- **M11:** `<Import Project="…"/>` with `Condition`, variable substitution (`$(MSBuildThisFileDirectory)`, `$(MSBuildProjectDirectory)`, and the rest of the `$(MSBuildThis*)` / `$(MSBuildProject*)` family), nested imports, and cycle detection.
- **M11:** Implicit `Directory.Build.props` discovery — walks up from the csproj's directory; the closest one found is imported at the head of evaluation (same semantics as upstream MSBuild).
- **M11:** `Directory.Build.targets` discovery — found, logged (`MSBLITE027`), and explicitly NOT ingested (targets files hold target definitions Carbide can't execute).

### Not supported (warning codes are emitted)

| Code | Meaning |
|---|---|
| `MSBLITE000` | XML parse error (fatal). |
| `MSBLITE001` | Unparseable `Condition` — element kept in scope. |
| `MSBLITE011` | `<Compile Update="…"/>` metadata (ignored). |
| `MSBLITE012` | A glob pattern matched no source files. |
| `MSBLITE013` | `<PackageReference>` captured; a consumer may resolve it (the CLI suppresses this once `@carbide/nuget` actually runs). |
| `MSBLITE014` | `<ProjectReference>` captured; a consumer (e.g. `@carbide/cli`'s project-graph walker) builds the sibling. The CLI suppresses this code once the walker actually runs. |
| `MSBLITE020` | `<Target>` encountered; refused (Carbide does not execute targets). |
| `MSBLITE021` | `<Task>` encountered; refused. |
| `MSBLITE022` | `<UsingTask>` encountered; refused. |
| `MSBLITE023` | `<Choose>/<When>/<Otherwise>` encountered; refused. Use `<PropertyGroup Condition="…">` instead. |
| `MSBLITE024` | `<Import Project="…"/>` target not found / unreadable. |
| `MSBLITE025` | `<Import>` cycle detected. The cycle chain is written into the warning message. |
| `MSBLITE027` | `Directory.Build.targets` auto-discovered but not ingested (Carbide does not execute targets). Silent if the file is an empty `<Project/>` marker. |
| `MSBLITE028` | `<ItemDefinitionGroup>` encountered; refused. |
| `MSBLITE029` | Attempt to set a reserved MSBuild property (`$(MSBuildProjectDirectory)` etc.) via `<PropertyGroup>`; ignored. |

Not handled at all: property functions (`$(Foo.ToUpper())`), item metadata functions (`%(Identity)`), item element functions (`@(Compile->Distinct())`), `<InitialTargets>` / `<DefaultTargets>`, SDK-style implicit `Sdk.props` / `Sdk.targets` imports. See M5 plan §7 and M11 plan §7.

### M11 — MSBuild evaluator

```ts
// Directory.Build.props in App/../Directory.Build.props contains <Nullable>enable</Nullable>.
// App/App.csproj declares only <TargetFramework>net10.0</TargetFramework>.
const model = await parseCsproj("App/App.csproj");
// model.properties.nullable === "enable"                    — inherited.
// model.evaluationTrace.imports                            — records every file walked.
//   [{ importedFile: ".../Directory.Build.props", kind: "props", applied: true },
//    { importedFile: ".../App.csproj", kind: "csproj", applied: true }]
```

`<Import Project="…"/>` works both at the top of a csproj and inside imported files. Cycle detection uses canonical paths; the same file is walked at most once per `parseCsproj` invocation.

## Usage

```ts
import { parseCsproj } from "@carbide/msbuild-lite";

const model = await parseCsproj("path/to/Foo.csproj");
console.log(model.evaluationTrace.targetFramework.selected);   // "net10.0"
console.log(model.sourceFiles);                                // ["…/Program.cs", "…"]
console.log(model.warnings);                                   // [{ code, message, severity }, …]
```

`parseCsprojString(xml, projectPath)` is the in-memory variant; the project directory is derived from `projectPath`.

## Design notes

- Zero runtime dependencies. The XML walker is ~200 lines of hand-rolled code (see `src/xml.ts`).
- Paths in the model's `sourceFiles` and `projectReferences` are absolute. The `@carbide/cli` wrapper translates them to relative document paths before feeding them to Carbide.
- Condition evaluation is intentionally shallow — nested parentheses and property functions are treated as "can't evaluate". This matches `cs_kit.msbuild_lite` so both parsers behave identically.
- `@carbide/msbuild-lite` is intentionally a parser, not an evaluator/executor. It captures package and project references so higher layers can decide what to do with them; it does not execute MSBuild logic and does not fetch packages itself.

## License and provenance

`@carbide/msbuild-lite` is licensed under [Apache-2.0](LICENSE), with copyright held collectively by Carbide Contributors. It is a TypeScript semantic port of the bounded `msbuild_lite.py` parser that lived in the predecessor Tools repository; that Carbide-owned provenance does not impose a separate license.
