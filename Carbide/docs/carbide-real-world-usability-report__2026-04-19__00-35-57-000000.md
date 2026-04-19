# Carbide real-world usability report

Created (UTC): 2026-04-19T00:35:57Z
Repository HEAD: bec44f9bfc7cc2ed581066ddd2339487f6f8c685

## Scope

This report evaluates Carbide against more realistic workloads than the existing baseline smoke tests, using new end-to-end scenarios added in this change set.

## New real-world scenarios covered by tests

### 1) Core corpus: multi-file business-style programs (`@carbide/core`)

Added three new corpus fixtures under `packages/core/test/node/corpus/`:

1. **`ecommerce-analytics`**
   - Multi-file order model + analytics layer.
   - Uses grouping, aggregation, deterministic tie-breaking, and record types.
   - Emulates a lightweight reporting job.

2. **`log-pipeline`**
   - Multi-file parser + analytics service over structured text logs.
   - Uses enums, record DTOs, percentile calculation, and summary projection.
   - Emulates observability post-processing in backend tooling.

3. **`pricing-engine`**
   - Multi-file domain model + pluggable rule engine.
   - Uses interfaces, primary constructors, strategy-style composition, and aggregate quoting.
   - Emulates rules-heavy line-of-business logic.

### 2) CLI integration: package-backed operational workloads (`@carbide/cli`)

Added gated live integration tests under `packages/cli/test/integration/real-world-scenarios.test.mjs`:

1. **`CsvHelper` report pipeline + offline replay**
   - Resolves package from NuGet, executes a realistic CSV aggregation, writes lock, and replays offline.
   - Verifies the "download once, run hermetically" workflow.

2. **`YamlDotNet + Scriban` config rendering workflow**
   - Resolves and executes a two-package composition to parse YAML and render a deployment-style template.
   - Verifies multi-package project behavior and practical text-processing utility.

## Usability assessment

### What feels strong already

- **Fast path to value is excellent** for script-like and utility-like C# workloads.
- **Project-file workflow is practical**: `carbide run --project` maps well to how engineers already structure C# code.
- **Lock-file and offline replay model is a major win** for reproducibility and air-gapped-ish environments.
- **Multi-document support is mature enough** for non-trivial domain partitioning (models/services/program split).

### Where usability degrades in realistic usage

- **NuGet scenario confidence depends on live network tests**; there is still limited always-on coverage for nontrivial package graphs.
- **No first-class project-to-project orchestration yet** means real solutions need manual pre-build + `--ref` plumbing.
- **Diagnostics UX is still low-level** in CLI output for large projects (raw compiler diagnostics are accurate but not yet highly curated for developer ergonomics).
- **MSBuild subset boundaries are still easy to hit** in existing enterprise `.csproj` files.

## Current limitations and shortcomings (practical impact)

1. **Bounded MSBuild semantics**
   - Complex target imports, generated files, custom item transforms, and many enterprise conventions are outside scope today.
   - Impact: migration friction for existing production repositories.

2. **No built-in project graph build (`<ProjectReference>`)**
   - Currently warning/capture oriented; no automatic transitive orchestration.
   - Impact: medium-to-large solutions cannot be run end-to-end without manual composition.

3. **Allow-list governance for packages is intentionally strict**
   - Strong for safety, but can block real workloads unless package onboarding cadence keeps up with user demand.
   - Impact: perceived "it almost works" moments for teams with broader dependency sets.

4. **Integration testing split between offline and live**
   - Live tests are gated, so default CI confidence can under-represent real dependency-resolution stress.
   - Impact: regressions in package resolution/compatibility may be discovered later than ideal.

## Improvement proposals

### High-priority

1. **Ship M9 (`<ProjectReference>` orchestration) sooner rather than later**
   - This is the largest usability unlock for real repos.
   - Include cycle diagnostics + assembly-name collision guidance with actionable fixes.

2. **Add a curated "real-world fixture suite" with lock snapshots**
   - Keep live tests, but add lock-backed integration fixtures for default CI to maximize deterministic coverage.
   - Aim for a matrix: single package, two-package composition, transitive-heavy package, and strict/offline failure paths.

3. **Upgrade CLI diagnostics UX**
   - Group diagnostics by file, display compact context excerpts, and summarize top blocking categories.
   - Add a `--diagnostics detailed|compact` mode.

### Medium-priority

4. **Publish clearer compatibility profiles**
   - Document practical "works well" solution archetypes (script utility, data transform, CLI tool, etc.).
   - Include a migration checklist for existing `.csproj` to Carbide-friendly shape.

5. **Add package-onboarding workflow docs + automation guardrails**
   - Template-based process to add allow-list entries with required resolver/test evidence.

6. **Extend corpus breadth toward "50 realistic programs" objective**
   - Prioritize IO-heavy transforms, JSON/YAML pipelines, templating, and mini-ETL scenarios.

## Bottom line

Carbide is already compelling for SDK-less execution of meaningful C# workloads, especially for automation and constrained environments. The biggest blockers to broader "drop-in for existing repos" usability are project-graph orchestration and broader/high-confidence package-path validation. The newly added scenarios show the current architecture holds up well for realistic single-project applications and package-backed workflows, while also highlighting where the next set of improvements will produce disproportionate user impact.
