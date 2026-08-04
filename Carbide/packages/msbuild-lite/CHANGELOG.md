# Changelog — `@carbide/msbuild-lite`

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
package follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The exported
surface frozen by this release is recorded in
[`api/carbide-msbuild-lite.api.md`](../../../api/carbide-msbuild-lite.api.md).

## [Unreleased]

## [0.1.0] - 2026-08-04

First published release.

### Added

- **`parseCsproj`** — reads a single `.csproj`, walks `<PropertyGroup>` and `<ItemGroup>`,
  expands compile-item globs against the accumulated property bag, and returns a
  `ProjectModel`.
- **Recognised properties.** `TargetFramework`, `Nullable`, `LangVersion`,
  `ImplicitUsings`, `DefineConstants`, `AssemblyName`, `RootNamespace`, plus the reserved
  `MSBuild*` properties needed for path substitution.
- **Item capture.** `Compile` includes and removes with default-glob handling
  (`EnableDefaultCompileItems`), `PackageReference`, and `ProjectReference`.
- **Bounded evaluation.** Simple `Condition` expressions, property substitution, and a
  `Directory.Build.props` ancestor walk whose properties feed the evaluator before the
  `.csproj` itself.
- **`<Import Project="…"/>`** with conditions, variable substitution, nesting, and cycle
  detection. A missing import target logs `MSBLITE024` rather than failing the parse.
- **Explicit refusals.** `<Target>`, `<Task>`, `<UsingTask>`, `<Choose>`, and
  `<ItemDefinitionGroup>` emit `MSBLITE020`–`MSBLITE023` and `MSBLITE028`;
  `Directory.Build.targets` is discovered but not ingested (`MSBLITE027`). Nothing is
  silently ignored — every unsupported construct produces a diagnostic code.

### Fixed

Corrected before the first release. Four `Condition` defects, all of which returned a
*confident* answer rather than reporting
themselves as unevaluated — so the affected element was silently kept or dropped with no
warning:

- **String comparison is now case-insensitive**, as MSBuild specifies.
  `Condition="'$(Configuration)' == 'Debug'"` against a `Configuration` of `debug` evaluated
  to false, silently skipping a `<PropertyGroup>` that a real build takes.
- **`and` now binds tighter than `or`.** The evaluator split on `and` first, so
  `A or B and C` grouped as `(A or B) and C` instead of `A or (B and C)` — inverted
  precedence, and a different answer for any mixed condition without explicit parentheses.
- **`!` negation of a parenthesised group is supported and correct.** `!` used to be folded
  into the left operand, so `!('$(Configuration)' == 'release')` compared the literal
  `!('debug'` against `'release')` and answered false where MSBuild answers true. Other `!`
  forms have no unambiguous meaning in this subset and are now refused rather than
  mis-parsed.
- **Parentheses are respected when splitting.** A group is no longer torn apart by the
  `and`/`or` scan, so explicit grouping overrides precedence as written. Operator words
  inside quoted literals (`'a and b'`) no longer split the expression either.

Two compile-item glob defects, both of which made a pattern match nothing at all — so the
project compiled without those sources and the error surfaced as CS0246 against code that was
not at fault:

- **`?` is a wildcard again.** It was escaped into a literal question mark, which cannot
  appear in a Windows filename, so `Include="File?.cs"` could never match. It now matches
  exactly one character, as MSBuild specifies.
- **A pattern may reach outside the project directory.** The walk was rooted at the project
  directory regardless of what the pattern said, so `Include="..\Shared\*.cs"` — the standard
  shared-source idiom — matched nothing. The pattern's wildcard-free prefix now becomes the
  walk root, which also keeps the walk tight: `../Shared/*.cs` visits `../Shared` and nothing
  else. Named prefix segments are matched case-sensitively against real directory entries
  rather than handed to the filesystem, so a case-insensitive host cannot make one half of a
  pattern follow different rules from the other.

- **Ordering in emitted output is reproducible.** The evaluation trace's `resolved` list and
  the collected package/project reference list were sorted with `localeCompare`, whose result
  depends on the host's ICU locale data and which collates punctuation and case rather than
  comparing code units — so `Serilog.Sinks.Console` and `SerilogTimings` could order
  differently on two machines. Both are `carbide audit` output. Sorting is now ordinal,
  matching the adjacent `sources` list, which already used the default ordinal sort.

### Changed

- Glob matching is documented as **case-sensitive on every host**. MSBuild inherits the
  filesystem's behaviour, so the same `.csproj` resolves differently on Windows and Linux;
  Carbide gives one answer everywhere, consistent with how it treats document paths as exact
  identities.

[Unreleased]: https://github.com/VladimirReshetnikov/Carbide/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/VladimirReshetnikov/Carbide/releases/tag/v0.1.0
