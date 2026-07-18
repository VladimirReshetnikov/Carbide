# Carbide — post-M9 usability remediation plan (Phases U1–U3)

- Created (UTC): 2026-04-19T05:30:00Z
- Repository HEAD: 79ecc77f66828d29728c83242293637e7d13aeb1

Status: parent plan proposing three phased work items to clear the P0/P1/P2 backlog surfaced in [Carbide-Usability-Report](../reports/Carbide-Usability-Report.md). Complements (does not replace) the architecture doc's M10–M13 Band C stretch milestones in [carbide-architecture-and-implementation-plan §9](carbide-architecture-and-implementation-plan__2026-04-17__16-16-47-000000.md#9-implementation-plan).

Audience: Carbide Contributors, and future contributors picking up the phases.

Scope: usability sharpening — the CLI surface, the program I/O wire, diagnostics, and error classification. The phases are scoped so each can land as a single PR; each phase is "done" when its own acceptance tests are green.

Band labelling: these are Band A / Band B items in vision-doc terms. They sharpen existing shapes (S1–S5, all already green) rather than adding new ones. This plan explicitly does *not* claim any new Shape.

## 1. Context

M9 landed the last in-scope shape S5 ([detailed plan](milestones/carbide-M9-detailed-plan__2026-04-18__23-18-54-749142.md)), so the cross-cutting sharpness backlog — the part of the Usability Report that isn't "a missing milestone" — is now unblocked and overdue for attention.

### 1.1 State of the Usability Report items (post-M9)

| Ref | Item | Status | Addressed by |
|---|---|---|---|
| P0.1 | Structured output is not fully enforceable (`Console.OpenStandardOutput` bypasses capture) | **Open** | Phase U1 |
| P0.2 | `carbide run` did not surface csproj/NuGet warnings in JSON | **Done** | Resolved before this plan (M6-era fix); M9 kept `run`'s warnings array across the multi-project path. |
| P0.3 | Project-graph orchestration (`<ProjectReference>`) missing | **Done** | [M9](milestones/carbide-M9-detailed-plan__2026-04-18__23-18-54-749142.md) |
| P1.1 | Program argv / stdin not forwarded | **Open** | Phase U2 |
| P1.2 | Logging is too verbose by default | **Open** | Phase U1 |
| P1.3 | NuGet "known failures" surface as "unexpected error" | **Open** | Phase U1 |
| P2.1 | `.csproj` default compile items surprise (CS8802 for scratch dirs) | **Open** | Phase U3 |
| Proposal | `carbide audit --project` for introspection | **Open** | Phase U3 |

The P0.1 / P1 items are where Carbide feels most unfinished to someone actually driving it from a shell or agent runtime today. P2.1 is a smaller polish item but the "scratch directory" failure mode is easy to hit on day 1.

### 1.2 Shape of the work

Each phase is one landable PR, with its own acceptance, its own follow-ups, and a bounded risk budget. The phases are ordered by *dependency* — U1 clamps down exit-code / JSON shape / verbosity *before* U2 extends the JSON/schema surface with new argv-and-stdin fields, so consumers never see a mid-migration contract.

```text
  ┌───────────── Phase U1 ─────────────┐
  │  CLI UX: JSON framing, verbosity,  │
  │  structured errors, exit codes     │
  └───────────┬────────────────────────┘
              ▼
  ┌───────────── Phase U2 ─────────────┐
  │  Program I/O: argv + stdin wire    │
  │  through interop schema            │
  └───────────┬────────────────────────┘
              ▼
  ┌───────────── Phase U3 ─────────────┐
  │  Introspection + polish:           │
  │  `carbide audit`, CS8802 guidance, │
  │  scratch-dir story, `carbide tree` │
  └────────────────────────────────────┘
```

Phases can land independently on the trunk in the order shown, but U1 ordering is recommended first so the schema-change in U2 happens once against a stable error surface.

## 2. Phase U1 — CLI UX sharpening

**Goal.** Make the CLI a *predictable* surface for programmatic consumers (agents, scripts, CI) while keeping the human experience calm and easy. Close P0.1, P1.2, P1.3.

### 2.1 Acceptance

U1 is "done" when every item below is green.

#### 2.1.1 JSON framing contract — `--format json` (renaming: keep `json`; add strict mode)

**Current state.** The CLI emits a blank line then the JSON object then a newline, and the test-helper parses the last non-empty line. This works in practice but is a *convention*, not a contract: a user program that writes via `Console.OpenStandardOutput()` can inject raw bytes before the JSON trailer.

**New state.** Two tiers:

- `--format json` (default). Unchanged wire shape; still documented as "parse the last non-empty stdout line." The CLI gains a single-line `STX`-like sentinel immediately before the JSON trailer (`\x1F\x1Fcarbide-json\x1F\x1F\n`, or an equivalent byte sequence that is almost certainly not present in user console output). Consumers can scan from the end of stdout for the sentinel and read the next line; this is a *non-breaking* additive change.
- `--format json-strict`. The CLI writes *only* the JSON payload on stdout; any user-program output (including raw-bytes writes) is captured to a separate file via `--capture-output <path>` or omitted entirely. Errors exit 2 if the user program attempts a write that Carbide cannot capture (only reachable when the runtime-level capture proposed below is wired).

**Runtime-level capture (best-effort).** Investigate hooking Mono-WASM's `fd 1` / `fd 2` at the dotnet.js layer — when the runtime exposes a stream sink override, route it into the same capture buffer as `Console.SetOut`. Land this as a warning-only bump first (any escaped writes log `MSCAP001` but the build succeeds); promote to error-on-escape in a later release once the mechanism is proven.

Acceptance tests:

- A user program that writes only via `Console.Write(...)` produces identical JSON under both `json` and `json-strict`.
- A user program that writes raw bytes to the stdout handle produces valid JSON (no corruption of the trailer) under `json` with the sentinel; under `json-strict` the raw bytes never reach the CLI's stdout.
- The existing byte-identical PE test (M5) still passes.

#### 2.1.2 Verbosity (P1.2)

**Flags.** `--verbose` (alias `-v`) → info-and-above; default → warnings-and-above; `--quiet` (alias `-q`) → error-and-above only. Also honour `CARBIDE_LOG_LEVEL=trace|debug|info|warning|error|quiet`.

**Implementation.** The CLI's current `console.info` / `console.debug` → stderr redirect stays, but the runtime's `MinimumLogLevel` is set from the CLI flag before `CarbideSession.initializeAsync()`. Default policy: `Warning`. The existing `enableDiagnosticTracing` option stays reachable from the TS API for internal tooling.

Acceptance:

- `carbide build --source Foo.cs --assembly-name Foo` produces zero bytes on stderr when the build succeeds and there are no Carbide-level warnings.
- `carbide build --verbose …` emits the current info/trace stream (regression-safe for anyone scraping it today).
- `CARBIDE_LOG_LEVEL=trace carbide build …` matches `--verbose` behaviour exactly.
- `carbide build --quiet …` suppresses everything except error-severity output.

#### 2.1.3 Structured error classification (P1.3 + generalisation to all CLI failures)

The M9 work introduced `handleProjectGraphError` (cycles / collisions / missing refs → clean exits with `MSPROJ*` codes). U1 extends the pattern to every thrown error the CLI may encounter.

**Error taxonomy** (exit code → category):

| Code | Category | Example source |
|---|---|---|
| 0 | Success | — |
| 1 | User-source errors | Roslyn diagnostics (`CS*`), `MSPROJ001` cycle, `MSPROJ004` missing ref. |
| 2 | I/O or internal | Disk failure, PE malformed, unexpected runtime error. |
| 3 | Flag / config error | Bad flag combo, `MSPROJ002` AssemblyName collision, `MSPROJ003` stdout-pipe + multi-project. |
| 4 | **NuGet policy refusal** | `MSNUGET015/016/017` safety, `MSNUGET020/021` allow-list. |
| 5 | **NuGet network / cache miss** | `MSNUGET030` under `--offline`; flat-container fetch failures. |

Codes 4–5 are *new* — they split the current "NuGet fails → exit 2" bucket into "your policy blocked this" vs "the network / cache cannot satisfy this." Both still produce a structured JSON payload on stdout:

```json
{ "success": false, "error": { "code": "MSNUGET021", "category": "allow-list-refusal",
  "message": "…", "package": { "id": "…", "version": "…" } },
  "warnings": [ … ] }
```

**Implementation.** A single `handleCliFailure(err, format)` helper in `project-file.ts` (or a new `errors.ts`) consumes every throw-site's error:

- `AllowListRefusedError` → exit 4, `category: "allow-list-refusal"`.
- `SafetyRefusalError` → exit 4, `category: "safety-refusal"`.
- `OfflineCacheMissError` → exit 5, `category: "offline-cache-miss"`.
- `ProjectGraphCycleError` → exit 1 (matches M9).
- `ProjectGraphNameCollisionError` → exit 3.
- `ProjectReferenceNotFoundError` → exit 1.
- `LockReadError` → exit 2 (I/O-shaped).
- Anything else → exit 2, `category: "internal"` with a truncated stack preview under `--verbose`.

Acceptance:

- Each `MSNUGET*` error code has a negative-path test under `packages/cli/test/error-taxonomy.test.mjs` that asserts exit code and JSON category.
- The M9 MSPROJ test suite (`project-graph-round-trip.test.mjs`) keeps passing with the `error` field now populated on failure payloads (additive; existing assertions are untouched).

#### 2.1.4 Exit-code + diagnostics contract doc

A short "CLI contract" reference under `packages/cli/README.md` enumerates every exit code, category, and a stable example payload. The `schemaVersion` bumps to 3 because every failure now carries `error.code / category`; the BuildResult / RunResult success shape is unchanged.

### 2.2 Deliverables

| File | Change |
|---|---|
| `packages/cli/src/format.ts` | Add JSON sentinel helper + `json-strict` mode. Extend `Format` union. |
| `packages/cli/src/logging.ts` | **New.** Resolves verbosity from CLI flags + env; re-exports `LogLevel` constants for the CLI bin. |
| `packages/cli/src/errors.ts` | **New.** `handleCliFailure(err, format) → exitCode`. Central dispatcher consuming every named error type from `@carbide/nuget` + the M9 graph module. |
| `packages/cli/src/bin/carbide.ts` | Wire verbosity + `handleCliFailure` at the top-level catch. |
| `packages/cli/src/commands/{build,run,validate}.ts` | Replace ad-hoc catches with `handleCliFailure`. |
| `packages/cli/README.md` | Rewrite "Exit codes" into the §2.1.3 table; document `--format json-strict`. |
| `packages/core/src/ts/session.ts` | Accept a `logLevel` option; thread it into the runtime's `MinimumLogLevel`. |
| `packages/core/src/Program.cs` or equivalent | Read `CARBIDE_LOG_LEVEL` equivalent from the schema; set `LoggerFactory` min level. |
| `packages/cli/test/error-taxonomy.test.mjs` | **New.** Exhaustive exit-code + category tests for every named error. |
| `packages/cli/test/verbosity.test.mjs` | **New.** Default silence, `--verbose` restores info, `--quiet` suppresses warnings. |
| `packages/cli/test/json-strict.test.mjs` | **New.** `--format json-strict` is pure JSON even when the user program writes to `Console.OpenStandardOutput()`. |
| `docs/drift/README.md` | "Documented differences (U1)" block. |

### 2.3 Design decisions (U1)

- **U1.D1. Sentinel delimiter beats a pure JSON-only stream.** Making the default `json` mode strict is backwards-incompatible — every consumer today parses the last non-empty line. The sentinel is additive; old consumers ignore it, new ones key on it.
- **U1.D2. Exit codes 4 and 5 are reserved for NuGet.** Other subsystems (say, a future Webcil-loader failure) get codes 6+. Stable exit codes are load-bearing for scripts / agents; reusing codes would be painful.
- **U1.D3. `--quiet` is `-q`; there is no short flag for `--format`.** Follows Unix norms; keeps the short-flag namespace focused on the two most-used options.
- **U1.D4. `handleCliFailure` is a single function, not a class hierarchy.** Three commands, one helper — a registry of error constructors would be overkill. Adding a new code is "add one `if` branch."
- **U1.D5. Runtime-level capture is a *warning* first.** Changing `Console.OpenStandardOutput()` behaviour from "silently escapes" to "errors the build" would break any user code that relies on it today. Land the detection first, emit `MSCAP001`, give users a release to migrate, promote to `error` later.

### 2.4 Risks

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| U1.R1 | Sentinel bytes appear in user output (accidentally) | Very low | Medium | Use `U+001F` (Unit Separator) doubled; picking this accidentally from a user program is vanishingly unlikely. Document the exact bytes. |
| U1.R2 | `--quiet` hides a warning a user cares about | Low | Low | Warnings still go into the JSON payload; `--quiet` only silences stderr. |
| U1.R3 | Runtime-level capture changes Mono-WASM behaviour in a way Carbide can't reason about | Medium | Medium | Gate behind `--format json-strict`; keep `json` behaviour untouched. Ship with a feature flag (`CARBIDE_EXPERIMENTAL_STDOUT_CAPTURE=1`) for a release. |
| U1.R4 | Exit-code split (NuGet policy vs cache-miss) forces script rewrites | Low | Low | Exit 2 was the previous bucket; scripts that treat "anything non-zero" as failure are unaffected. Scripts that branch on codes will have to add cases, but the split is semantically useful. |
| U1.R5 | `schemaVersion` bump to 3 confuses a pinned consumer | Low | Low | C# validator already tolerates N or N-1; document the additive change in drift notes. |

### 2.5 Out of scope for U1

- Replacing `Console.SetOut` entirely with a runtime-level capture (follow-up after U1 lands as a warning).
- Streaming stdout to the consumer as the program runs (deferred; see §5).
- Restructuring the logging library itself. U1 consumes whatever Carbide.Core currently logs through.

## 3. Phase U2 — Program I/O forwarding

**Goal.** Make `carbide run --project Foo.csproj -- a b c` actually pass `["a", "b", "c"]` to the user program's `Main(string[] args)`. Wire stdin for programs that read from it. Close P1.1.

### 3.1 Acceptance

- `carbide run --source Prog.cs -- alpha beta` with `Prog.cs = Console.Write(string.Join(",", args));` prints `alpha,beta`.
- The same program receives exactly `["alpha", "beta"]` when invoked from the TS API via `project.run({ args: ["alpha", "beta"] })` — the in-process contract matches the CLI contract.
- `echo "hello" | carbide run --source Echo.cs` with `Echo.cs = Console.Write(Console.In.ReadToEnd());` prints `hello`.
- Under `--format json`, `carbide run` carries the forwarded argv and stdin byte count in the success payload:
  ```json
  { "success": true, "stdOut": "alpha,beta", "exitCode": 0,
    "invocation": { "args": ["alpha", "beta"], "stdinBytes": 0 } }
  ```
- A top-level-statements program sees `args` via Roslyn's synthesised `Main(string[] args)` parameter (standard C# behaviour; Carbide was dropping args, not *adding* `args`).

### 3.2 Implementation shape

1. **Interop schema bump.** `RunRequest` gains `args: string[]` (defaults `[]`) and `stdin: string | null` (defaults null). `schemaVersion` ticks to 4. C# validator accepts 3 or 4 during the rollout window; TS client always emits 4.
2. **C# `RunAsync` signature.** Today the reflected entry point is invoked with `Array.Empty<string>()`. Change to:
   - If the entry point takes `string[]`, pass the forwarded args.
   - If it takes no parameters (top-level, legacy), skip and continue (args still reachable via the synthesised `args` name in top-level statements because Roslyn emits a `Main(string[] args)` wrapper).
   - `Console.SetIn(new StringReader(stdin))` when `stdin != null` before invocation.
3. **CLI wiring.** `parseArgs` already captures `programArgs` after `--`. Thread through to `runRun`. For stdin: `--stdin -` reads from the CLI's stdin (consuming it before the runtime boots to avoid interleaving with Carbide's own logging); `--stdin <path>` reads a file.
4. **Browser host.** No stdin source available by default, so the `stdin` field is ignored in the browser host with a `MSCAPI002` warning if non-null. Args are honoured.

### 3.3 Deliverables

| File | Change |
|---|---|
| `packages/core/src/ts/interop/schema.ts` | Add `args`/`stdin` to the RunRequest schema. Bump `SCHEMA_VERSION` to 4. |
| `packages/core/src/ts/project.ts` | `Project.run(options?: { args?: string[]; stdin?: string })`. |
| `packages/core/src/Services/ProjectCompiler.cs` | `RunAsync(..., string[] args, string? stdin)`; invoke reflected entry point with args; `Console.SetIn` as needed. |
| `packages/cli/src/commands/run.ts` | Forward `programArgs`; add `--stdin <path \| ->`; populate `invocation` in JSON payload. |
| `packages/cli/test/argv-forwarding.test.mjs` | **New.** Positive + negative cases for argv, top-level vs `Main(string[])` entry points. |
| `packages/cli/test/stdin-forwarding.test.mjs` | **New.** Echo program + file / `-` stdin. |
| `packages/core/test/node/program-io.test.mjs` | **New.** Same semantics exercised via the TS API. |
| `docs/drift/README.md` | "Documented differences (U2)" — argv/stdin semantics. |

### 3.4 Design decisions (U2)

- **U2.D1. No argv forwarding in the browser CLI; yes in the TS API.** The CLI flag is Node-only. Browser consumers pass `args` directly via `project.run({ args })`.
- **U2.D2. `stdin` is eagerly read and buffered, not streamed.** Streaming stdin into WASM across the JSExport boundary is disproportionate for U2's scope. A streaming follow-up is a U4 candidate.
- **U2.D3. Missing stdin on the browser host is a warning, not an error.** Agents and scripts should be able to call `project.run({ stdin: "…" })` unconditionally; the browser ignoring it with a warning is friendlier than a throw.
- **U2.D4. `Environment.GetCommandLineArgs()` continues to return Carbide's own argv (process-level).** Users who need the program's forwarded args use `Main(string[])` or `Environment.GetCommandLineArgs().Skip(1)` is deliberately *not* wired — too confusing. Document the distinction.

### 3.5 Risks

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| U2.R1 | Top-level-statement programs lose argv because of how Roslyn names the synthesised parameter | Low | High | Test pins this on both hosts; if the name is unstable, bind by kind not name. |
| U2.R2 | stdin is consumed before the runtime wants it; agent pipelines break | Medium | Medium | Only consume stdin when the user explicitly passes `--stdin -`. Default: stdin is untouched. |
| U2.R3 | schemaVersion bump conflates with U1 | Low | Low | U1's bump to 3 already happened; U2's bump to 4 is a separate PR. Document the sequence. |
| U2.R4 | Existing consumers that called `project.run()` without an args argument break | Very low | Medium | `args` is optional (`string[] = []`) at both TS and C# layers. |

### 3.6 Out of scope for U2

- Streaming stdout/stderr to the outer process as the program runs. That requires a different JSExport shape (callbacks instead of a single return). Candidate follow-up: U4 "live streaming IO."
- `Console.OpenStandardInput()` handle-level behaviour. Mirrors U1's stdout story; defer.
- Program argv for `carbide build` / `validate` (neither runs the program).

## 4. Phase U3 — Introspection and csproj polish

**Goal.** Give users and agents a clear, machine-readable view of what Carbide sees, and soften the CS8802 "scratch directory" footgun. Close P2.1 and the `carbide audit` / `carbide tree` proposals.

### 4.1 Acceptance

- `carbide audit --project App/App.csproj --format json` prints a structured report covering the parsed csproj model, the project graph (M9 shape), the resolved NuGet graph, the compile-item resolution trace, and every warning Carbide emitted during the walk. No compilation is performed; no `.dll` is written.
- `carbide tree --project App/App.csproj` prints a human-readable ASCII tree of the project graph (M9 follow-up §8) combined with direct NuGet dependencies per node.
- A scratch directory containing two `.cs` files with top-level statements no longer prints Roslyn's bare `CS8802` error without context: Carbide wraps it with a help message suggesting `--source` + explicit compile items *or* structured csproj globs.
- `carbide build --project Foo.csproj --source Extra.cs` is accepted under a new `--scratch` escape hatch: the source is *added* to the csproj-derived set instead of conflicting.

### 4.2 Implementation shape

1. **`carbide audit`.** A new command under `packages/cli/src/commands/audit.ts`. Runs `runProjectGraphPipeline` but skips every `project.build()` and `project.getDiagnostics()` call — audit is a *structural* read. Emits JSON under `--format json` (default) and human-readable tables under `--format human`.
2. **`carbide tree`.** Either a flag on `audit` (`--tree`) or a separate command. Implementation is a walk over `multi.graph.order` with ASCII-art formatting; cheap.
3. **CS8802 hint.** When `build` or `validate` surfaces a `CS8802` diagnostic in `--project` mode, the CLI appends a trailing hint diagnostic (`id: "CARBIDE_HINT_CS8802"`, severity: info) pointing at the supported workarounds. Hints are info-level so they don't affect exit codes.
4. **`--scratch` mode.** An explicit flag (no implicit `--source` + `--project` mixing) that means "this csproj is a scaffold; also include these extra sources." Unlocks the scratch-dir workflow without making the default `--project` behaviour ambiguous.

### 4.3 Deliverables

| File | Change |
|---|---|
| `packages/cli/src/commands/audit.ts` | **New.** Graph-only pipeline, JSON + human output. |
| `packages/cli/src/commands/tree.ts` | **New.** ASCII-tree render over the graph. |
| `packages/cli/src/bin/carbide.ts` | Wire `audit` and `tree` subcommands. |
| `packages/cli/src/commands/build.ts` | Accept `--scratch` + extra `--source` when combined with `--project`; CS8802 hint emission. |
| `packages/cli/src/commands/validate.ts` | Same CS8802 hint emission. |
| `packages/cli/test/audit.test.mjs` | **New.** Hermetic fixture; assert audit JSON has expected shape for a lib+app graph. |
| `packages/cli/test/tree.test.mjs` | **New.** Snapshot-style ASCII-tree output assertion. |
| `packages/cli/test/scratch.test.mjs` | **New.** `--project Foo.csproj --source Extra.cs --scratch` produces the correct combined source set. |
| `packages/cli/README.md` | Audit / tree / scratch sections. |

### 4.4 Design decisions (U3)

- **U3.D1. `audit` is read-only.** No emit, no `carbide.lock.json` writes unless `--write-lock` is explicitly passed. Keeps audits cheap and safe to run in pre-commit or CI lint jobs.
- **U3.D2. `tree` is focused.** One subcommand, one output. No `--depth N`, no `--show-packages-only` — the user-visible signal is "what does this graph look like," and hiding parts of it defeats the purpose.
- **U3.D3. `--scratch` is opt-in.** The default `--project ↔ --source` mutual exclusion (M5 D59) stays. `--scratch` is the documented escape hatch; without it, mixing the two is still a flag error (exit 3).
- **U3.D4. CS8802 hint is a diagnostic, not a log line.** Structured output consumers pick it up alongside the Roslyn error; human output sees it under the error. Avoids a "sometimes stderr, sometimes stdout" inconsistency.

### 4.5 Risks

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| U3.R1 | `audit` JSON shape drifts from `build`/`validate` — consumers have to learn two schemas | Low | Medium | Reuse the same `diagnostics` + `warnings` shapes; add `graph` / `resolvedNuget` / `compileItems` as top-level siblings. Document the schema under `packages/cli/README.md`. |
| U3.R2 | `--scratch` invites implicit mutable `.csproj` semantics | Low | Low | The flag is documented as "scaffold; extra sources are appended in-memory only"; the csproj on disk is not rewritten. |
| U3.R3 | CS8802 hint fires in false positives | Low | Low | Only attached in `--project` mode *and* when `<EnableDefaultCompileItems>` is default-enabled; otherwise the user has already opted in to explicit items. |
| U3.R4 | `tree` output clashes with CI log parsers that interpret ASCII art | Very low | Very low | `tree` is a separate command; nothing that ran CI before now invokes it. |

### 4.6 Out of scope for U3

- `carbide doctor` (runtime-health diagnostic command). Interesting but orthogonal.
- `carbide lock sync` (follow-up from M9 §8, related to per-project lockfile divergence). Separate PR.
- `carbide init` (project scaffolding). Out of Carbide's bounded scope.
- A GUI / TUI for `audit`. Not here.

## 5. Closely-related work folded in (but intentionally deferred)

These are touched or implied by the phases above and are worth tracking, but don't belong in U1–U3.

- **U4 — Live streaming stdout/stderr.** The runtime captures user output into a buffer and returns it all at the end of the run. For long-running programs (progress bars, etc.) that's awkward. A future phase could add `project.run({ onStdout, onStderr })` callbacks in the TS API and a `--stream` flag in the CLI. Partly blocked by U1's runtime-level capture landing.
- **U5 — `carbide lock sync` subcommand.** Surfaces version divergence across per-project `carbide.lock.json` files (an M9 §8 follow-up). Natural U3 neighbour; deliberately split to keep U3's PR focused on introspection *reads*, not writes.
- **Determinism beyond single-machine.** M5 D53 pins determinism per machine. Cross-platform determinism (same PE bytes from the same inputs on Windows vs Linux vs macOS) is a bigger deal. Arguably belongs in M7 (API stability lock), not this backlog.
- **CI in this repo.** The Usability Report's closing note ("dotnet missing in cloud containers as blocker") hints at a Carbide-specific CI story. Container image + workflow is a one-day project worth pulling in if U-phase work stalls on a PR-review-ability concern.
- **Carbide `Directory.Build.props` support.** Architecturally M11 (Band C stretch). Not in U-phase.

## 6. Acceptance checklist across all U-phases

A sanity list for reviewers; the phase-local acceptances above stay authoritative.

- [ ] U1.1 Sentinel-framed JSON output landed; `--format json-strict` works.
- [ ] U1.2 `--verbose` / `--quiet` / `CARBIDE_LOG_LEVEL` work; default is silent on success.
- [ ] U1.3 Exit-code taxonomy is implemented and documented; `MSNUGET*` errors surface under codes 4 / 5.
- [ ] U2.1 argv forwarded from CLI `--` separator to `Main(string[])`.
- [ ] U2.2 stdin forwarded via `--stdin <path | ->`.
- [ ] U2.3 `schemaVersion` bumped to 4; both old + new consumers still work during the transition window.
- [ ] U3.1 `carbide audit --project` command works.
- [ ] U3.2 `carbide tree --project` command works.
- [ ] U3.3 CS8802 hint lands under `--project` mode.
- [ ] U3.4 `--scratch` escape hatch works.
- [ ] All existing M1–M9 test suites (core, CLI hermetic, CLI live-gated) remain green.
- [ ] Drift notes updated for each phase; current-state guide updated once all three land.

## 7. Out of scope for the U-phase cluster

Items that might look related but are deliberately NOT included:

| Item | Owning milestone / reason |
|---|---|
| `.sln` parsing | Vision §7 caps Shape S5 at one-plus-siblings; never. |
| Full MSBuild evaluator | M11 (architecture doc); Band C stretch. |
| Source generators / analyzers | M12; Band C stretch. |
| WASI execution target | M10 (architecture doc); Band C stretch. |
| Parallel per-project compilation | M9 §8 follow-up; not a usability issue. |
| GUI host (Avalonia / similar) | Separate proposal track. |
| `dotnet.js` fork or pinning | Part of the M7 API-stability lock. |
| Cross-TFM graph support | D89 / M9 scope bound. |
| IDE integration | Never (Carbide is a framework, not an editor). |

## 8. Follow-ups discovered while drafting

- **An explicit consumer-contract doc.** Right now "what the CLI promises" is scattered across README, drift notes, and individual command help texts. A `docs/contracts/cli-contract.md` pinning exit codes, JSON shapes, and sentinel format would pay back for every downstream consumer (agent runtimes in particular).
- **Example integration harness.** A small standalone project in `docs/examples/cli-consumer/` that demonstrates calling `carbide` programmatically and parsing the JSON trailer, would double as an executable sanity check.
- **Promoting the M9 `handleProjectGraphError` helper.** U1 absorbs this helper into the broader `handleCliFailure`; leave the exported error classes (`ProjectGraphCycleError`, etc.) public for third-party TS consumers who want the same classification without shelling out.
- **Deprecation ladder for schemaVersion bumps.** With U1 bumping to 3 and U2 to 4, worth pinning a rule: "the C# validator accepts current and current-1; anything older must emit the current version." Document in architecture §5 or similar.

## 9. Links

- [Carbide Usability Report](../reports/Carbide-Usability-Report.md) — source-of-truth for the items this plan addresses.
- [Carbide architecture and implementation plan](carbide-architecture-and-implementation-plan__2026-04-17__16-16-47-000000.md) — §9 for the canonical milestone roadmap (unaffected by this plan); U-phases sit parallel to M10–M13.
- [Carbide M9 detailed plan](milestones/carbide-M9-detailed-plan__2026-04-18__23-18-54-749142.md) — the sibling plan; U1's error-handling design extends M9's `handleProjectGraphError`.
- [Carbide current-state guide](../Carbide-Current-State-Guide.md) — update target once U-phases land.
- [Drift tracking](../drift/README.md) — per-phase "Documented differences" section lands here.
