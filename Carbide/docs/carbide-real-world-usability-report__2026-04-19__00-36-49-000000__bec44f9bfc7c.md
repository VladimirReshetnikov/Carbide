# Carbide real-world usability report

Created (UTC): 2026-04-19T00:36:49Z
Repository HEAD: bec44f9bfc7cc2ed581066ddd2339487f6f8c685

## Purpose

This report evaluates Carbide against several **more realistic application scenarios** than the prior baseline tests and summarizes practical usability findings, current limitations, and improvement proposals.

## Scenario set exercised

I extended the test corpus with three new API-level scenarios and two CLI-level scenarios.

### Core corpus additions (`@carbide/core`)

1. **log-analytics**
   - Multi-file parser + analyzer for production-like HTTP log lines.
   - Uses records, LINQ grouping, percentile math, sorting, and formatted text output.
   - Exercises non-trivial in-memory analytics workflows and deterministic output shaping.

2. **invoicing**
   - Multi-file invoice domain model with decimal math, taxation rules, discount flow, and rounding mode choices.
   - Closer to line-of-business business-rule code than algorithm demos.

3. **release-notes**
   - Multi-file release-note formatter with enum-priority ordering, grouping, and deterministic markdown rendering.
   - Mimics CI/release-automation style transformation pipelines.

### CLI additions (`@carbide/cli`)

1. **complex multi-file project**
   - `.csproj`-driven project with `ImplicitUsings`, `Nullable`, and `DefineConstants`.
   - Domain-oriented triage pipeline (`Ticket`, `Policy`, `Report`, `Program`) executed via `carbide run --project`.

2. **project-reference limitation probe**
   - `.csproj` with `<ProjectReference>` validated via `carbide validate --project`.
   - Confirms current behavior surfaces expected `MSBLITE014` warning (captured-only, not built).

## Usability findings

## What works well now

1. **Strong baseline for SDK-less inner-loop execution**
   - Carbide handles realistic, multi-file, business-logic-heavy workloads without requiring a local .NET SDK runtime path.

2. **Output determinism is good enough for scenario-golden testing**
   - Several scenarios rely on ordered grouping/aggregation output and remain stable under test assertions.

3. **Project-mode ergonomics are already practical for bounded shapes**
   - Using `.csproj` with common properties and compile-item discovery is sufficient for many “single-project utility/service” prototypes.

4. **Conditional compilation path works in project mode**
   - `DefineConstants` reliably affects runtime behavior in realistic branching logic, which is important for environment-aware builds.

## Friction points and shortcomings

1. **No project graph orchestration yet (`<ProjectReference>`)**
   - This is the biggest practical blocker for migrating medium-size real solutions.
   - Current workaround (build siblings separately and pass `--ref`) is viable but not ergonomic for iterative development.

2. **Bounded NuGet policy narrows compatibility envelope**
   - Safety-focused refusals are correct for threat-model control, but real project portability drops quickly when packages carry analyzers, native assets, or MSBuild logic.

3. **No generators/analyzers means incomplete parity with modern C# ecosystems**
   - Many production codebases rely on source generators (e.g., serializers, mappers, DI source-gen), which currently makes direct adoption partial.

4. **Diagnostics/UX still optimized for technical users**
   - Current warnings and errors are serviceable, but higher-level remediation hints (actionable “next best step”) would reduce friction for less-specialized users.

5. **No first-class workflow for corpus-scale scenario management**
   - The corpus pattern exists and works, but there is no dedicated metadata schema (tags, expected-runtime envelope, feature dependencies, known limitations) to make it a strategic benchmark set.

## Improvement proposals

## Priority 0 (highest leverage)

1. **Deliver M9 project-graph support for `<ProjectReference>`**
   - Add topological build orchestration and cross-project diagnostic attribution.
   - Provide an opt-in `--graph-mode` (initially) to de-risk rollout.

2. **Introduce scenario benchmark metadata for corpus fixtures**
   - Add `scenario.json` per fixture with dimensions like: `domain`, `featuresUsed`, `expectedComplexity`, `knownLimitations`.
   - This turns tests into an explicit capability matrix, not just pass/fail scripts.

## Priority 1

3. **Improve diagnostics with remediation contracts**
   - For each major warning/error family (`MSBLITE*`, `MSNUGET*`), emit concise suggested actions.
   - Example for `MSBLITE014`: “Run carbide build in referenced project and pass output with --ref.”

4. **Add an opinionated “workspace mode” helper in CLI**
   - Even before full M9, provide a thin helper command to discover sibling projects and bootstrap manual `--ref` pipelines.

## Priority 2

5. **Expand real-world acceptance suite toward documented domains**
   - Suggested domain packs: ETL/reporting, REST-client SDK stubs, policy engines, text processing pipelines, finance calculations, and CLI utility apps.

6. **Track performance envelopes in CI for realistic scenarios**
   - Add lightweight timing and memory telemetry per scenario to prevent regressions hidden by only functional pass/fail checks.

## Bottom line

Carbide is already highly usable for **single-project**, **managed-only**, **bounded-package** workloads with meaningful business logic complexity. The largest adoption blocker for broader real-world use is **project graph support**; next is **package/generator ecosystem parity**. The new scenarios confirm the current platform can carry realistic application logic today, while making the remaining gaps concrete and actionable.
