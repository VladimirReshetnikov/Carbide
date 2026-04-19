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

### Not supported (warning codes are emitted)

| Code | Meaning |
|---|---|
| `MSBLITE000` | XML parse error (fatal). |
| `MSBLITE001` | Unparseable `Condition` — element kept in scope. |
| `MSBLITE011` | `<Compile Update="…"/>` metadata (ignored). |
| `MSBLITE012` | A glob pattern matched no source files. |
| `MSBLITE013` | `<PackageReference>` captured; a consumer may resolve it (the CLI suppresses this once `@carbide/nuget` actually runs). |
| `MSBLITE014` | `<ProjectReference>` captured; a consumer (e.g. `@carbide/cli`'s project-graph walker) builds the sibling. The CLI suppresses this code once the walker actually runs. |

Not handled at all: `Directory.Build.props`, `<Import>`, `<Target>`, `<Task>`, item metadata functions, property functions. See M5 plan §7.

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
