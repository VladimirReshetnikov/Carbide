# Carbide Usability Report (Real-World Scenarios)

- Status: Informational (hands-on usability report + limitations + improvement proposals)
- Audience: Users evaluating Carbide for real tooling; maintainers prioritizing follow-up work
- Scope: `src/Carbide` as implemented today (M1–M6 shipped; M9 planned)
- Created (UTC): 2026-04-19T00:50:13Z
- Repository HEAD: bec44f9bfc7cc2ed581066ddd2339487f6f8c685
- Related code:
  - `../../packages/core/src/Services/ProjectCompiler.cs` (compile/emit/run + output capture)
  - `../../packages/cli/src/bin/carbide.ts` (CLI entry point + logging redirection)
  - `../../packages/cli/src/commands/run.ts` (run behavior + JSON payload shape)
  - `../../packages/cli/src/project-file.ts` (csproj + NuGet composition)
  - `../../packages/nuget/src/resolver.ts` (bounded resolver + lock model)
- Related docs:
  - [Carbide current-state guide](../Carbide-Current-State-Guide.md)
  - [@carbide/cli README](../../packages/cli/README.md)
  - [@carbide/core README](../../packages/core/README.md)

## Executive summary

Carbide is already genuinely usable for “tool-shaped” workloads that:

- compile and run bounded console programs,
- stay within a small `.csproj` subset, and
- use a small, allow-listed set of *managed-only* NuGet packages.

Where it currently gets rough in real-life usage:

- `carbide run --format json` is only *best-effort* machine-readable because user code can write to stdout in ways Carbide cannot intercept today (notably `Console.OpenStandardOutput`).
- CLI program args and stdin are not forwarded into the user program yet (even though `--` is parsed).
- C# runtime logging is very chatty by default (trace/info noise on every invocation).
- `<ProjectReference>` is captured/warned but not built (expected; M9).

I added more CLI tests to cover these scenarios, plus new live NuGet end-to-end tests for YamlDotNet and a transitive Serilog graph.

## Scenarios exercised

### 1) Multi-file, “modern C#” code (no NuGet)

Shape: a small “tool-like” program split across multiple files, using pattern matching and LINQ.

Result: works. Compilation and execution behave as expected.

Notable UX: by default, the CLI emits trace/info logs to stderr (makes `--format human` feel noisier than it needs to).

### 2) Piping generated code through stdin (`--source -`)

Shape: `carbide run --source -` with the source passed via stdin.

Result: works and is very convenient for agents and code generators.

Test coverage: `../packages/cli/test/advanced-usage.test.mjs`.

### 3) Program arguments (`-- ...`) and stdin forwarding

Shape: `carbide run ... -- one two three` with a program that prints `args.Length`.

Result: args are currently *not* forwarded. The program receives an empty array.

This is expected per current-state docs, but surprising given the CLI accepts `--`.

Test coverage: `../packages/cli/test/advanced-usage.test.mjs`.

### 4) `.csproj` with strict `<ImplicitUsings>disable</ImplicitUsings>`

Shape: a single-file `.csproj` that disables implicit usings, then compiles a source that references `Console` with/without `using System;`.

Result: behaves as expected; disabling implicit usings restores strict semantics.

Test coverage: `../packages/cli/test/advanced-usage.test.mjs`.

### 5) `.csproj` source-set control via `<Compile Include="..."/>` + `<Compile Remove="..."/>`

Shape: a project with `EnableDefaultCompileItems=false`, `Include="src/**/*.cs"`, and a removed file that would otherwise break compilation (multiple top-level statement files).

Result: works; globs and removes control the effective source set as intended.

Test coverage: `../packages/cli/test/advanced-usage.test.mjs`.

### 6) `.csproj` + allow-listed NuGet (live resolve + offline lock replay)

Shape A: `YamlDotNet` pinned version; parse YAML and print a deterministic string.

Result: live resolve downloads, lock is written, and `--offline` replay works.

Test coverage: `../packages/cli/test/integration/yaml-round-trip.test.mjs` (gated by `CARBIDE_NUGET_LIVE=1`).

Shape B: `Serilog.Sinks.Console` (transitive dependency on `Serilog`); confirm transitive graph resolves and offline replay works.

Result: lock contains both packages; replay works. Console output from libraries is discussed below (see output capture limitations).

Test coverage: `../packages/cli/test/integration/serilog-round-trip.test.mjs` (gated by `CARBIDE_NUGET_LIVE=1`).

### 7) Output capture vs. stdout handle writes (major sharp edge)

Shape: a program that writes to stdout through `Console.OpenStandardOutput()` and also uses `Console.Write(...)`.

Observation:

- Carbide captures `Console.Write(...)` via `Console.SetOut(...)` and reports it as `stdOut` in the JSON payload.
- Writes performed via `Console.OpenStandardOutput()` bypass that capture and can appear as raw bytes on the outer stdout.

Consequence: raw output can precede the JSON payload and break consumers that assume stdout is pure JSON.

Mitigation landed in this change:

- CLI now emits the JSON payload as a “trailer” and ensures it starts on a new line.
- CLI tests parse the last non-empty stdout line as JSON.

Test coverage: `../packages/cli/test/advanced-usage.test.mjs`.

Limitations: the raw bytes still exist; this is not a full sandboxed “structured output” solution.

## Practical usability notes

### JSON output contract (today)

For `carbide build/run/validate --format json`, the robust consumption pattern is:

1. treat stdout as “possibly mixed”,
2. parse the **last non-empty line** as the JSON payload.

This is now the test contract for the CLI because there is no complete enforcement that user code cannot write raw bytes to stdout.

### NuGet workflow feels good (within scope)

Within Carbide’s bounded resolver/safety model, the “resolve once, replay offline” workflow is solid:

- first run: downloads, writes `carbide.lock.json`
- later: `--offline` replays the lock and uses the cache

The allow-list and safety refusals are a usability win when Carbide is used as an agent runtime, but they must be very clearly surfaced (see proposals).

## Limitations and shortcomings (with proposals)

### P0: Structured output is not fully enforceable

**Problem:** output capture is `Console.SetOut`-based; handle-level writes bypass capture and can corrupt machine-readable stdout.

**Proposal:**

- Introduce an explicit framed output mode, e.g. `--format json-trailer`, with a strong guarantee:
  - JSON is always on its own line and is always last.
  - consumers parse only the trailer line.
- Longer-term: investigate dotnet.js / runtime-level stdout interception to prevent raw writes from escaping in Node.

### P0: `carbide run` did not surface csproj/NuGet warnings in JSON

**Problem:** `build` and `validate` already returned `warnings` in JSON, but `run` did not, which hides important warnings like `MSBLITE014` (ProjectReference captured-only).

**Fix landed:** `carbide run --format json` now includes a `warnings` array when `--project` is used.

### P1: Program argv/stdin not forwarded

**Problem:** the CLI parses `-- <program args>...`, but the runtime still invokes `Main(string[])` with `Array.Empty<string>()`, and stdin isn’t connected.

**Proposal:**

- Extend the interop schema and C# `RunAsync` path to accept argv and pass it to the reflected entry point.
- Provide an explicit design for stdin:
  - either “buffer to string and provide API to read”,
  - or host-level stream plumbing (harder in WASM).

Add acceptance tests once implemented.

### P1: Logging is too verbose by default

**Problem:** Carbide logs info/trace to JS `console.*` by default, and the CLI redirects those to stderr. This is technically correct but noisy.

**Proposal:**

- Add `--verbose` / `--quiet` flags (or env var) in the CLI to control verbosity.
- Default to warnings-and-above for the CLI in normal usage.

### P1: NuGet “known failures” should be caught and surfaced cleanly

**Problem:** allow-list refusals and safety refusals can currently surface as “unexpected error” flows in the CLI depending on the throw site.

**Proposal:**

- Catch and classify `@carbide/nuget` errors in the CLI:
  - print a structured error payload in JSON mode,
  - return a stable exit code for “policy refused” vs “network/cache miss” vs “internal”.

### P2: `.csproj` default compile items surprise

**Problem:** in a directory with multiple `.cs` files containing top-level statements, default compile discovery yields CS8802 (“Only one compilation unit can have top-level statements.”).

This isn’t wrong, but it’s a footgun for “scratch directories”.

**Proposal:**

- In CLI project mode, optionally support `--source` overrides *in addition to* `--project` (currently rejected), or provide an opt-in flag that disables default discovery and requires explicit Compile items.
- Improve the error message / troubleshooting guidance for CS8802 in project mode.

## Test additions

New tests added under `@carbide/cli`:

- `../packages/cli/test/advanced-usage.test.mjs`
  - stdin sources
  - argv separator current behavior
  - JSON trailer parsing robustness
  - ImplicitUsings=disable behavior
  - Compile globs Include/Remove behavior
  - ProjectReference warning surfacing
- `../packages/cli/test/integration/yaml-round-trip.test.mjs` (live, `CARBIDE_NUGET_LIVE=1`)
- `../packages/cli/test/integration/serilog-round-trip.test.mjs` (live, `CARBIDE_NUGET_LIVE=1`)

How to run:

```bash
cd src/Carbide/packages/cli
npm test

# Live NuGet end-to-end tests:
CARBIDE_NUGET_LIVE=1 npm run test:live
```
