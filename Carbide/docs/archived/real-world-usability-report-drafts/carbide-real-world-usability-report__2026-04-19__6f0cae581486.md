# Carbide real-world usability report

Created (UTC): 2026-04-19T00:35:59Z
Repository HEAD: bec44f9bfc7cc2ed581066ddd2339487f6f8c685

- Status: Report
- Audience: Carbide maintainers and contributors
- Scope: Practical evaluation of `@carbide/core` on more realistic multi-file scenarios than the original seed corpus, plus usability findings and improvement proposals.

## Executive summary

I expanded the Node corpus with three business-style fixtures (`order-fulfillment`, `log-analyzer`, and `feature-flags`) to push Carbide beyond hello-world and language-feature spot checks. These scenarios exercise realistic layering and APIs: multi-file domain modeling, LINQ-heavy analytics, regex parsing, JSON parsing, switch expressions, records, and deterministic text output contracts.

From an API ergonomics perspective, Carbide remains very easy to embed: session/project lifecycle and source management are straightforward, and fixture authoring is low ceremony. The main practical friction points are around environment bootstrapping (runtime assets must already be built), bounded platform scope (not full MSBuild / source-generator support), and runtime execution limits (no argv/stdin streaming, no isolation).

## Added real-world scenario tests

### 1) `order-fulfillment`

A miniature shipping quote workflow with:

- rich domain records (`Order`, `OrderLine`, `ShipmentQuote`)
- cross-file business logic (`PricingEngine.Quote`)
- `decimal` arithmetic for money
- `switch`-based regional policy and conditional surcharges
- deterministic formatted output for contract-style assertions

This scenario approximates common line-of-business pricing logic rather than language-demo snippets.

### 2) `log-analyzer`

A production-style log processing scenario with:

- structured parsing using `Regex`
- enum-based severity modeling
- exception-on-malformed input policy
- aggregate analytics (`GroupBy`, `OrderBy`, distinct service counts)

This validates that Carbide handles text processing + LINQ aggregation patterns typical in operational tooling.

### 3) `feature-flags`

A realistic configuration/evaluation scenario with:

- JSON ingestion through `System.Text.Json`
- typed projection into immutable records
- ring/environment gating rules
- deterministic ordering of activated features

This is close to feature-rollout logic frequently found in backend/service utilities.

## Usability findings

## What works well

1. **Low-friction embedded API**
   - `CarbideSession.initializeAsync()` + `createProject()` + `addSource()` + `run()` is a clean path for programmatic use.
2. **Good fit for incremental multi-document workflows**
   - The API shape naturally supports generated code, template expansion, and iterative editing loops.
3. **Deterministic assertion model**
   - Corpus fixtures with `expected.json` make behavior validation clear and maintainable.
4. **Feature envelope is enough for many utility workloads**
   - Records, LINQ, regex, and JSON scenarios are representable in the current model.

## Pain points and limitations

1. **Build/bootstrap dependency is front-loaded**
   - Tests and runtime usage require prebuilt `dist/*` and WASM runtime assets. In fresh environments without `.NET SDK + wasm-tools`, it is easy to hit missing-artifact failures before any scenario code runs.
2. **Bounded MSBuild semantics remain a real constraint**
   - For realistic projects, unsupported `ProjectReference` orchestration and limited MSBuild parity force manual composition.
3. **No source generators / analyzers**
   - Modern production code that assumes analyzers or generator-backed patterns cannot be reproduced faithfully yet.
4. **Execution-host limitations**
   - Lack of argv/stdin streaming and process isolation limits suitability for interactive CLI parity and long-running workloads.
5. **Document path identity strictness can surprise users**
   - Exact-string document identity (including case/slash differences) is predictable internally but can be unintuitive for cross-platform callers.

## Shortcoming assessment

- **Primary shortcoming:** Carbide is already strong as an embedded compile+run engine, but still requires users to internalize a custom bounded world (runtime boot expectations, constrained project-system model, and explicit feature non-goals).
- **Secondary shortcoming:** operational ergonomics for first-time setup are weaker than API ergonomics after setup.
- **Tertiary shortcoming:** real-world enterprise build graphs are not yet first-class until project-reference orchestration and broader project-system behavior mature.

## Improvement proposals

## Near-term (high ROI)

1. **Preflight diagnostics command / API**
   - Add a `carbide doctor` command (and equivalent API hook) that verifies runtime assets, ref-pack availability, and browser/Node host assumptions with actionable remediation text.
2. **Fixture-classification strategy in tests**
   - Tag corpus fixtures by workload class (`business-logic`, `data-processing`, `async`, `interop`) and publish coverage summaries to prevent regressions toward demo-only coverage.
3. **Path-normalization option**
   - Keep exact-path behavior as default for backward compatibility, but provide an opt-in project option for normalized/case-folded source-path identity.
4. **Better first-run failure messaging**
   - When runtime artifacts are missing, surface concise “build these assets first” guidance from a single canonical check.

## Mid-term

1. **Implement M9 project-to-project orchestration**
   - This unlocks much more realistic repository usage by reducing manual DLL plumbing.
2. **Add argv/stdin support for `run()` and CLI passthrough**
   - Increases compatibility with script/tool scenarios that currently cannot be modeled.
3. **Expand browser parity matrix**
   - Formalize browser coverage beyond current Chromium-oriented smoke tests.

## Longer-term

1. **Generator/analyzer story**
   - Even if bounded, supporting a curated subset would significantly improve compatibility with modern C# ecosystems.
2. **Optional stronger isolation model**
   - Better run-to-run isolation would make Carbide safer for repeated untrusted/unknown workloads.

## Conclusion

The new fixtures show that Carbide can handle meaningful, multi-file “real software” snippets today, not just tutorial demos. The largest remaining value gap is not raw compilation capability but operational and ecosystem parity: project graph orchestration, richer execution plumbing, and stronger first-run ergonomics.
