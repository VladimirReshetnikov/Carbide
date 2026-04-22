# Carbide — `Carbide.UI` / `@carbide-ui/*` — Approach B (cross-frame runner) implementation plan

- Created (UTC): 2026-04-21T23:40:46Z
- Repository HEAD: 1caf40063579b2b6599ba4481072829c18161bde

Status: **implementation plan for Approach B** of the `@carbide-ui/*` companion family. Executes the recommendation of [`carbide-ui-avalonia-integration-proposal`](../proposals/carbide-ui-avalonia-integration-proposal__2026-04-18__22-04-08-231875__2bc4122b7f3f.md) §4.2 and §5. Phases UI-M0 through UI-M4 and UI-M6 are committed; UI-M5 (Approach C — offline CLI) is a concurrent sibling deliverable scoped in its own plan; UI-M7 and UI-M8 remain deferred per proposal §11.

Audience: repository owner Vladimir, and future contributors picking up phases.

Scope: the *how* of shipping Approach B. Takes the proposal's package layout (§7), protocol (§8), XAML strategy (§9), and required Carbide-core changes (§10) as settled, and turns them into per-phase work orders with acceptance gates, per-file change tables, invariants, and risk entries. Re-opening any architectural call routes through the proposal's §12 Open Questions, not this plan.

Out of scope (handled elsewhere):

- **Approach A (merged runtime)** — rejected in proposal §4.1/§5. Do not resurrect without first editing the proposal.
- **Approach C (offline CLI `--target avalonia-browser`)** — a concurrent deliverable that reuses UI-M2's runtime bundle and UI-M1's ref-pack. Separate plan, tracked under its own UI-M5 detailed plan document when the owner greenlights it. The shared-artefact contract is documented here (§8) so UI-M5 can pick it up without renegotiation.
- **UI-M7 / UI-M8** — gated on Carbide M12 (source generators) and a separate COOP/COEP serve helper, respectively.

## 1. Context

### 1.1 What the proposal settled

- **Two .NET runtimes on the page, handed off via `postMessage`.** Carbide's runtime compiles; the runner's runtime hosts Avalonia.Browser. Neither knows about the other's internals.
- **Four npm packages** with orthogonal jobs: `@carbide-ui/refs-avalonia` (compile-time refs), `@carbide-ui/avalonia-runtime-bundle` (prebuilt Avalonia WASM `_framework/`), `@carbide-ui/avalonia-runner` (the HTML/JS shell that boots the bundle), `@carbide-ui/launcher` (TS orchestrator on the Carbide side).
- **Three additive changes to `@carbide/core`** (§10 of the proposal): `CarbideOptions.sideload`, collectible `AssemblyLoadContext` in `ProjectCompiler.RunAsync`, and two optional `BuildResult` fields (`peSchemaVersion`, `primaryAssemblyName`). All three are GUI-neutral and ship independently of anything in `@carbide-ui/*`.
- **XAML strategy at v1 is runtime-parse.** `AvaloniaRuntimeXamlLoader.Parse` is the lever; no Avalonia source generator in v1. UI-M4 wraps the lever in a Carbide-side `.axaml`-as-document convenience.
- **Companion-family non-goals** (proposal §3 G.1–G.4) bind every phase: do not modify `@carbide/core`'s public surface except through §10's three additions; do not exceed the 40/40 MB split budget; do not couple Carbide's release cadence to Avalonia's; never claim "all Avalonia apps work".

### 1.2 Relationship to Carbide's existing phases

- **M1–M9** — all already shipped; no feature of `@carbide/core` changes behaviour for existing Carbide-only callers in Approach B. The §10.2 collectible-ALC change is the only touch to a shipped file (`packages/core/src/Services/ProjectCompiler.cs`), and it is observationally equivalent for console-program use.
- **M5 (`@carbide/msbuild-lite`)** — unused by UI-M0..UI-M6. UI-M5 (Approach C, separate plan) depends on M5 for `.csproj` parsing when the user passes `--project`.
- **M6 (`@carbide/nuget`)** — orthogonal. If users wire NuGet references into the session alongside the Avalonia ref-pack, the runner is unaware of the distinction; both paths feed bytes through `session.addReference`.
- **M12 (source generators, Band C)** — unlocks UI-M7. Not on this plan's critical path.
- **T1–T4 (xterm.js terminal)** — disjoint. The xterm work drives user `Console.*` IO; the Avalonia runner is a separate iframe with its own `<canvas>` and no console seam back to the launcher in v1.

### 1.3 Shape of the work

```text
  ┌───── UI-M0 ─────┐
  │ skeleton, CI,   │
  │ size gates,     │
  │ 4 empty packages│
  └────────┬────────┘
           ▼
  ┌───── UI-M1 ─────┐
  │ refs-avalonia   │
  │ (compile-time   │
  │  ref DLLs)      │
  └────────┬────────┘
           ▼
  ┌───── UI-M2 ─────┐       ┌────── core/§10 ──────┐
  │ runtime-bundle  │       │ sideload (§10.1)     │
  │ (prebuilt       │       │ collectible ALC      │
  │  Avalonia WASM) │       │   (§10.2)            │
  └────────┬────────┘       │ BuildResult fields   │
           ▼                │   (§10.3)            │
  ┌───── UI-M3 ─────┐◄──────┘
  │ runner HTML/JS, │
  │ launcher TS,    │
  │ postMessage     │
  │ protocol        │
  └────────┬────────┘
           ▼
  ┌───── UI-M4 ─────┐       ┌─── UI-M5 (Approach C) ───┐
  │ .axaml-as-doc   │       │ offline CLI — separate   │
  │ runtime-parse   │       │ plan; reuses UI-M1 +     │
  │ convenience     │       │ UI-M2 artefacts.         │
  └────────┬────────┘       └───────────────────────────┘
           ▼
  ┌───── UI-M6 ─────┐
  │ multi-preview,  │
  │ hardening,      │
  │ sample repo,    │
  │ cross-browser   │
  └─────────────────┘

  (UI-M7 XAML SG, UI-M8 multithreaded — deferred.)
```

UI-M1 and UI-M2 can land in parallel (disjoint contents). UI-M3 fan-in requires both plus the §10 additions in `@carbide/core`. Each of §10.1, §10.2, §10.3 is a standalone PR into `@carbide/core` and should merge ahead of UI-M3 rather than with it.

## 2. Invariants (hold across all phases)

Each phase's acceptance re-asserts these.

- **UI-I1. `@carbide/core` public surface stays GUI-neutral.** The only additions are `CarbideOptions.sideload` (§10.1), two optional `BuildResult` fields (§10.3), and an internal ALC change (§10.2). No `@carbide/core` file mentions Avalonia; no `@carbide/core` test depends on `@carbide-ui/*`.
- **UI-I2. Size budgets are enforced in CI, per package.** `@carbide/core` compressed ≤ 40 MB (pre-existing gate); `@carbide-ui/avalonia-runtime-bundle` compressed ≤ 35 MB; `@carbide-ui/refs-avalonia` compressed ≤ 5 MB; `@carbide-ui/avalonia-runner` ≤ 100 KB on top of the bundle; `@carbide-ui/launcher` ≤ 50 KB minified. A per-package size badge lands in CI output.
- **UI-I3. Triple-pin `(Carbide, Avalonia, .NET)` lives in `@carbide-ui/avalonia-runtime-bundle`.** The bundle package's `package.json` records all three versions in a `"pinned"` block; `@carbide-ui/refs-avalonia` records the Avalonia version only. No other package records the pin. Bumping Avalonia is a PR to those two packages, never `@carbide/core`.
- **UI-I4. Crash isolation is a tested property, not a comment.** UI-M3's acceptance includes a "runner hangs in a tight loop" fixture that verifies the launcher still reports a sensible error (teardown-by-timeout or iframe replace) and the Carbide session stays alive to compile the next program.
- **UI-I5. `postMessage` protocol is forward-compatible.** Every message carries `schemaVersion`. The runner rejects messages with a version it does not understand with an explicit `runnerError { kind: "load", message: "unsupported schemaVersion N" }`. Schema additions (new fields, new message types) are allowed; renames and removals are forbidden through v1.
- **UI-I6. Iframe is the teardown boundary.** `LaunchHandle.dispose()` drops the iframe via `iframe.src = "about:blank"` or DOM removal; any pending `load` or in-flight run is by definition abandoned. No runner-side "graceful shutdown" RPC in v1.
- **UI-I7. Single-threaded Avalonia.Browser by default.** No COOP/COEP headers are required of the host page. Multithreading is UI-M8.
- **UI-I8. The ref-pack ships only `ref/` content.** Build-task assemblies, analyzers, PowerShell cmdlets, and any other non-ref artefacts from Avalonia's NuGet packages are filtered out of the ref-pack.
- **UI-I9. The launcher does not import `@carbide/core`.** It consumes `BuildResult` via structural typing only (TypeScript `import type`, no runtime dependency). This lets the launcher version independently of Carbide core when only the consumer-facing fields matter.
- **UI-I10. The runner never runs outside an iframe.** `@carbide-ui/avalonia-runner`'s `index.html` is explicitly for same-origin or cross-origin iframe embedding; using it as a top-level page is unsupported. Documented in the package README.

## 3. Phase dependencies

### 3.1 Inter-phase

- UI-M0 depends on: none.
- UI-M1 depends on: UI-M0.
- UI-M2 depends on: UI-M0. (Independent of UI-M1 — can run in parallel.)
- UI-M3 depends on: UI-M1, UI-M2, and the three `@carbide/core` additions (§10.1–§10.3). The §10 changes should land via three separate PRs into `@carbide/core` *before* UI-M3 opens.
- UI-M4 depends on: UI-M3. The `.axaml`-as-document convenience needs the runtime-bundle + launcher flow to demonstrate end-to-end.
- UI-M6 depends on: UI-M3, UI-M4. Multi-preview requires the full launcher + runner flow stable; the sample repo uses UI-M4's XAML path.

### 3.2 `@carbide/core` side PRs (precondition for UI-M3)

These three land in `@carbide/core` independently of `Carbide.UI` work. Each has its own acceptance test inside `@carbide/core`'s existing test corpus.

- ✓ **core-P1. `CarbideOptions.sideload` (§10.1).** Shipped 2026-04-21 (Node adapter). Added `sideload?: readonly string[]` to [`CarbideOptions`](../../packages/core/src/ts/session.ts); the init path resolves each package's `refpack.json` (via `createRequire`, with a monorepo-sibling walk-up fallback that also scans sibling directories of each ancestor to cover cross-root layouts like `@carbide-ui/*` living under `src/Carbide.UI/`), reads each listed DLL's bytes, and calls `session.addReference` per entry. Sideloaded handles are kept as **default references** — `createProject` auto-attaches them to every project in the session, so a caller just sees "my Avalonia C# compiles". New `HostAdapter.loadSideloadRefPack(packageName)` optional method + `SideloadedRefPack` descriptor. Node adapter implements fully; [browser adapter](../../packages/core/src/ts/host/browser/browser-adapter.ts) rejects with a pointer to manual `addReference`. On sideload failure the session is torn down before rethrowing — no half-initialised state leaks. New test suite [`test/node/sideload.test.mjs`](../../packages/core/test/node/sideload.test.mjs) — 3/3 green against the real `@carbide-ui/refs-avalonia`. Full Node corpus **75/75** after the change. Browser-side sideload is the one follow-up before UI-M3 can run fully in-browser.
- ✓ **core-P2. Collectible ALC in `ProjectCompiler.RunAsync` (§10.2).** Shipped 2026-04-21. Replaced `Assembly.Load(byte[])` with `AssemblyLoadContext.LoadFromStream` against a per-run collectible ALC in both [`RunAsync`](../../packages/core/src/Services/ProjectCompiler.cs) (the original target) and [`RunInteractiveAsync`](../../packages/core/src/Services/ProjectCompiler.cs) (same pattern, noted in the original comment as "mirrors RunAsync"). Migrated the handler from `AppDomain.CurrentDomain.AssemblyResolve` to `runContext.Resolving`; `finally` unloads the context. Full `@carbide/core` Node test corpus: **69/69 green** (`multi-document`, `user-reference-lifecycle`, `user-reference-cross-session`, `workspace-churn`, `runtime-error`, `corpus`, etc.). Browser corpus (`test:browser`, Playwright) was not rerun locally — requires a Chromium binary download; the ALC path executes inside Mono-WASM for both host adapters and the Node suite exercises every reference-resolution and teardown scenario UI-M3 relies on.
- ✓ **core-P3. `BuildResult.peSchemaVersion` and `BuildResult.primaryAssemblyName` (§10.3).** Shipped 2026-04-21. Added two optional fields to [`BuildResult.cs`](../../packages/core/src/Services/BuildResult.cs), the [`BuildResultDto`](../../packages/core/src/CompilationInterop.cs) wire shape, the [TypeScript `BuildResult`](../../packages/core/src/ts/types.ts), and the [`parseBuildResult`](../../packages/core/src/ts/interop/schema.ts) parser. `peSchemaVersion` is `1` in v1 and `undefined` on failure; `primaryAssemblyName` is forwarded from `compilation.AssemblyName` on success. New targeted test suite [`test/node/build-result-fields.test.mjs`](../../packages/core/test/node/build-result-fields.test.mjs) — 3/3 green. Full Node corpus stayed 69/69 green after the change.

Each PR title prefix: `carbide-core: ` (the umbrella package tag). Each one is independently shippable.

## 4. UI-M0 — skeleton & build pipeline  ✓ shipped 2026-04-21

**Goal.** Stand up `src/Carbide.UI/`, four empty npm packages, the internal `runner-dotnet` csproj (empty stub), and CI wiring that measures sizes even before any real code exists. No Avalonia yet; no runtime bundle yet; no protocol.

**Shipped artefacts (2026-04-21, repo HEAD 1caf40063):**

- Tree scaffolded under [`src/Carbide.UI/`](../../../Carbide.UI/) per proposal §7.6.
- Four `package.json` files (`refs-avalonia`, `runtime-bundle`, `runner`, `launcher`) with `version: "0.0.0-dev.0"`, `private: true`, Apache-2.0 licence. Each packs cleanly via `npm pack --dry-run`.
- Internal C# stub: [`packages/runner-dotnet/Avalonia.UI.Runner.csproj`](../../../Carbide.UI/packages/runner-dotnet/Avalonia.UI.Runner.csproj) + [`Program.cs`](../../../Carbide.UI/packages/runner-dotnet/Program.cs) printing `"stub runner"`. Not built at UI-M0; UI-M2 brings the build online.
- Size gate: [`scripts/measure-sizes.mjs`](../../../Carbide.UI/scripts/measure-sizes.mjs) reports per-package tarball bytes against UI-I2 budgets. Replaces the `.github/workflows/carbide-ui.yml` deliverable from the original §4.2 table — the repo has no `.github/workflows/` directory, so a locally-runnable measurement script serves the same purpose and is CI-ready when a workflow story arrives. Baseline (2026-04-21): all four packages < 1 KB (stubs).
- `.gitignore` updates: repo root whitelists `!src/Carbide.UI/packages/`; `src/Carbide.UI/.gitignore` excludes generated `ref/`, `_framework/`, `dist/`, etc.

### 4.1 Acceptance

- `src/Carbide.UI/` exists with the proposal §7.6 directory layout: four `packages/*/` subtrees plus `runner-dotnet/` skeleton.
- `Directory.Build.props` and `Directory.Build.targets` at `src/Carbide.UI/` set TFM, language version, nullable, and the usual Carbide repo-wide options; `runner-dotnet` inherits from this root.
- Each of the four npm packages has a valid `package.json` with `"name"`, `"version": "0.0.0-dev.0"`, `"description"`, `"license"`, and `"private": true` (flip to `false` at first publish).
- `npm pack` in each package directory produces a tarball.
- CI jobs for all four packages run `npm install`, `npm pack`, and write measured compressed sizes to the build log; sizes are stored as build artefacts for UI-I2's later enforcement.

### 4.2 File & package work

| Path | Contents |
|---|---|
| `src/Carbide.UI/Directory.Build.props` | Inherits repo-wide props; sets `TargetFramework=net10.0` default (runner csproj overrides to `net10.0-browser`). |
| `src/Carbide.UI/Directory.Build.targets` | Repo-wide targets. |
| `src/Carbide.UI/README.md` | Index of the four packages + pointer to this plan + pointer to the proposal. |
| `src/Carbide.UI/packages/refs-avalonia/package.json` | `@carbide-ui/refs-avalonia`, empty `scripts/build.mjs` stub that prints "stub". |
| `src/Carbide.UI/packages/runtime-bundle/package.json` | `@carbide-ui/avalonia-runtime-bundle`, `build.mjs` stub. |
| `src/Carbide.UI/packages/runner/package.json` | `@carbide-ui/avalonia-runner`; `index.html` stub. |
| `src/Carbide.UI/packages/launcher/package.json` | `@carbide-ui/launcher`; `src/index.ts` exports a stub `launchInIframe` that throws "not implemented". |
| `src/Carbide.UI/packages/runner-dotnet/Avalonia.UI.Runner.csproj` | Empty csproj, `<TargetFramework>net10.0-browser</TargetFramework>`, `<Sdk Name="Microsoft.NET.Sdk.WebAssembly" />`, empty `Program.cs` that just calls `Console.WriteLine("stub runner")`. |
| `src/Carbide.UI/packages/launcher/tsconfig.json` | Matches `@carbide/core` style. |
| `.github/workflows/carbide-ui.yml` (new) or entry in existing workflow | Builds all four packages; runs `npm pack`; uploads sizes as artefact. |

### 4.3 Risks / notes

- **Decision gate.** UI-M0 does not start until the proposal §13 decisions are made: (1) companion-project vision amendment accepted; (2) Avalonia target version picked; (3) .NET version confirmed (`net10.0-browser`); (4) npm scope finalised. Without (1), the whole directory should not appear in the tree.

## 5. UI-M1 — reference pack  ✓ shipped 2026-04-21

**Goal.** `@carbide-ui/refs-avalonia` ships the compile-time reference DLLs for Avalonia. Mirrors the build script shape of `@carbide/refs-net10.0` (see `packages/refs-net10.0/scripts/build.mjs`). Enables Carbide callers to compile Avalonia-referencing C# without the runtime bundle.

**Shipped artefacts (2026-04-21, Avalonia 12.0.1):**

- [`packages/refs-avalonia/scripts/build.mjs`](../../../Carbide.UI/packages/refs-avalonia/scripts/build.mjs) — downloads Avalonia 12.0.1 from `api.nuget.org`, verifies SHA256, filters to `lib/<tfm>/` DLLs per UI-I8, writes `ref/net10.0-browser/` and `refpack.json`. Runs at `postinstall` and on demand via `npm run build`. Idempotent: subsequent runs print "up to date" when the manifest matches the pinned version.
- 8 extracted DLLs (`Avalonia.dll`, `Avalonia.Base.dll`, `Avalonia.Controls.dll`, `Avalonia.Markup.dll`, `Avalonia.Markup.Xaml.dll`, `Avalonia.Markup.Xaml.Loader.dll`, `Avalonia.Browser.dll`, `Avalonia.Themes.Fluent.dll`), total 4.93 MB uncompressed.
- [`refpack.json`](../../../Carbide.UI/packages/refs-avalonia/refpack.json) — schema version 1; records `avaloniaVersion`, per-DLL `{name, sha256, sizeBytes, sourceId}`, per-source `{id, version, url, sha256, sourceTfm}`. Shape parallels [`@carbide/refs-net10.0`'s `ref-manifest.json`](../../../Carbide/packages/refs-net10.0/ref-manifest.json).
- [`THIRD_PARTY_NOTICES.md`](../../../Carbide.UI/packages/refs-avalonia/THIRD_PARTY_NOTICES.md) — Avalonia MIT licence reproduced.
- [`README.md`](../../../Carbide.UI/packages/refs-avalonia/README.md) — consumption instructions, pinning workflow, DLL inventory.

**Size measurement:**

```
[OK  ] @carbide-ui/refs-avalonia  1.643 MB  (budget 2.000 MB)
```

Compressed tarball 1.64 MB against the 2 MB compressed budget; 4.93 MB against the 5 MB uncompressed budget. Both UI-I2 gates green.

**Acceptance closed 2026-04-21** with core-P1 landing. The full acceptance fixture now lives at [`test/node/sideload.test.mjs`](../../packages/core/test/node/sideload.test.mjs): `CarbideSession.initializeAsync({ sideload: ["@carbide-ui/refs-avalonia"] })` compiles a trivial Avalonia-referencing program on the Node adapter. Browser adapter still pending a follow-up; consumers can feed refs manually via `session.addReference(bytes, name)` using `refpack.json` until that lands.

### 5.1 Acceptance

- From a fresh environment:

  ```bash
  cd src/Carbide.UI/packages/refs-avalonia
  node scripts/build.mjs
  ```

  produces `ref/net10.0-browser/` (or `ref/net10.0/` per Avalonia's actual nupkg layout) with ≈15 DLLs and a `refpack.json` manifest listing each DLL's name, byte size, and SHA256.

- From a Carbide-core consumer:

  ```ts
  const session = await CarbideSession.initializeAsync({
      sideload: ["@carbide-ui/refs-avalonia"],
  });
  const project = session.createProject();
  project.addSource("App.cs", `
      using Avalonia;
      public class App : Application { public override void Initialize() {} }`);
  const result = await project.build();
  // result.success === true && result.pe !== undefined
  ```

  compiles clean against the ref-pack.

- Ref-pack size: ≤ 5 MB uncompressed, ≤ 2 MB compressed.
- Manifest shape matches `@carbide/refs-net10.0/ref-manifest.json`.

### 5.2 File & package work

| Path | Contents |
|---|---|
| `packages/refs-avalonia/scripts/build.mjs` | Downloads pinned `Avalonia.Browser.<version>.nupkg` + all transitive refs via `api.nuget.org/v3-flatcontainer/...`; extracts `ref/net10.0-browser/` or `ref/net10.0/` contents; writes DLLs into `ref/<tfm>/`; computes SHA256 per DLL; writes `refpack.json`. Filter-lists all `build/`, `analyzers/`, `lib/`, `tools/` content per UI-I8. |
| `packages/refs-avalonia/refpack.json` | Generated: `{ "name": "@carbide-ui/refs-avalonia", "avaloniaVersion": "...", "dlls": [{ "name": "Avalonia.Base.dll", "sizeBytes": 1234, "sha256": "..." }, ...] }`. |
| `packages/refs-avalonia/ref/` | Generated tree. Checked in (following `@carbide/refs-net10.0` precedent) so consumers don't need network at install time. |
| `packages/refs-avalonia/README.md` | How to bump Avalonia version; how the triple-pin works; which DLLs are included and why. |
| `packages/refs-avalonia/THIRD_PARTY_NOTICES.md` | Avalonia's MIT notice copied verbatim. |
| `packages/refs-avalonia/package.json` | `"main": "./refpack.json"`; `"files": ["ref/", "refpack.json", "README.md", "THIRD_PARTY_NOTICES.md"]`; `"version"` format: `<Avalonia-major>.<Avalonia-minor>.<patch>-<carbide>`. |

### 5.3 Risks

- **UI-M1-R1. Avalonia nupkg structure differs from `Microsoft.NETCore.App.Ref`'s flat `ref/net10.0/` layout.** Avalonia's `.nupkg` contains `ref/net8.0/` (and possibly `ref/net10.0-browser/`) plus build-task stuff. Mitigation: probe the actual pinned version's layout up front; the build script's TFM selector is a config constant. Out-of-band follow-up: record the observed layout in the build script's header comment so future maintainers don't re-derive it.
- **UI-M1-R2. `SkiaSharp` native binaries in the nupkg balloon the download.** Filter at extraction time: `ref/` content only; no `runtimes/` content goes into the ref-pack.

## 6. UI-M2 — runtime bundle  ✓ shipped 2026-04-21

**Goal.** `@carbide-ui/avalonia-runtime-bundle` ships the prebuilt Avalonia.Browser `_framework/` tree. This is the bytes-only package the runner embeds in its HTML shell.

**Shipped artefacts (2026-04-21, Avalonia 12.0.1 / .NET 10.0.6):**

- [`packages/runner-dotnet/Avalonia.UI.Runner.csproj`](../../../Carbide.UI/packages/runner-dotnet/Avalonia.UI.Runner.csproj) — `Microsoft.NET.Sdk.WebAssembly`, `net10.0-browser`, pinned `Avalonia.Browser`/`Avalonia.Themes.Fluent`/`Avalonia.Markup.Xaml.Loader` at 12.0.1.
- [`Program.cs`](../../../Carbide.UI/packages/runner-dotnet/Program.cs) + [`App.cs`](../../../Carbide.UI/packages/runner-dotnet/App.cs) — minimal Avalonia boot with a splash `TextBlock`. Becomes `RunnerProgram` at UI-M3.
- [`wwwroot/index.html`](../../../Carbide.UI/packages/runner-dotnet/wwwroot/index.html) + [`wwwroot/main.js`](../../../Carbide.UI/packages/runner-dotnet/wwwroot/main.js) — iframe-embeddable HTML shell + `_framework/dotnet.js` boot module.
- [`packages/runtime-bundle/build.mjs`](../../../Carbide.UI/packages/runtime-bundle/build.mjs) — runs `dotnet publish -c Release` on the runner csproj, copies `_framework/` + `shell/` (HTML + JS) into the package, computes per-file SHA256, writes `bundle-manifest.json`. Not wired to `postinstall` — the publish step requires the .NET 10 SDK + `wasm-tools` workload, which most consumers won't have; maintainers run `npm run build` on demand and commit the generated artefacts.
- [`bundle-manifest.json`](../../../Carbide.UI/packages/runtime-bundle/bundle-manifest.json) — schema v1; triple-pin block `{avalonia, dotnet, carbide}` (carbide still null until UI-M3 wires the protocol); `sizeBytes` records on-disk and effective-Brotli-cold-load; `framework[]` + `shell[]` arrays with `{path, sizeBytes, sha256}` per file.
- [`test-shell.html`](../../../Carbide.UI/packages/runtime-bundle/test-shell.html) — throwaway smoke fixture; excluded from the published tarball.

**Measurements:**

```
[OK  ] @carbide-ui/avalonia-runtime-bundle  25.118 MB  (budget 35.000 MB)
```

- 414 files bundled (412 framework + 2 shell); 49.64 MB on disk (raw + `.br` siblings); 25.12 MB npm tarball; **11.00 MB** effective cold-load in a Brotli-capable browser. Under UI-I2's 35 MB compressed budget with ~10 MB headroom.

**Design deviation from original §6 acceptance:** the plan originally said "Brotli + gzip files both committed". The bundle ships **only raw + `.br`**; `.gz` siblings are dropped. Rationale: modern browsers (99%+) accept Brotli; `.gz` is redundant compression that adds ~15 MB to the tarball for marginal compat gain. Any consumer that genuinely needs gzip can recompress the raw files at deploy time. Documented in [`packages/runtime-bundle/README.md`](../../../Carbide.UI/packages/runtime-bundle/README.md).

**Acceptance deferrals:** the plan-specified "opening `test-shell.html` in a browser boots Avalonia and renders the default splash" is a manual smoke step. It has not been auto-tested in CI at UI-M2 (no Playwright harness yet); UI-M3 introduces the first integration test suite.

### 6.1 Acceptance

- `packages/runner-dotnet/Avalonia.UI.Runner.csproj` builds with `dotnet publish -c Release` and produces `bin/Release/net10.0-browser/publish/wwwroot/_framework/`.
- `packages/runtime-bundle/build.mjs` runs the publish, copies `_framework/` into `packages/runtime-bundle/_framework/`, and computes a manifest (`bundle-manifest.json`) recording the Avalonia version, .NET runtime version, compressed size, and a SHA256 of each file.
- Opening `packages/runtime-bundle/_framework/test-shell.html` (a throwaway fixture) in a browser boots Avalonia and renders the default splash without posting anything cross-frame.
- Bundle compressed size ≤ 35 MB (Brotli + gzip files both committed).

### 6.2 Runner-dotnet contents at UI-M2

The `runner-dotnet/` C# project is minimal at UI-M2 — just enough to prove the publish pipeline. Full `RunnerProgram` (proposal §7.3) ships in UI-M3.

| Path | Contents (UI-M2 state) |
|---|---|
| `packages/runner-dotnet/Avalonia.UI.Runner.csproj` | `<Sdk Name="Microsoft.NET.Sdk.WebAssembly" />`; `<TargetFramework>net10.0-browser</TargetFramework>`; `<PackageReference Include="Avalonia.Browser" Version="..." />`; `<PackageReference Include="Avalonia.Themes.Fluent" Version="..." />`. |
| `packages/runner-dotnet/Program.cs` | Minimal `BuildAvaloniaApp().StartBrowserAppAsync("out")` with a placeholder `App` class that renders a `TextBlock` saying "runner stub". |
| `packages/runner-dotnet/App.cs` | Placeholder `App : Avalonia.Application`. |
| `packages/runner-dotnet/wwwroot/index.html` | Avalonia's default `<div id="out">` shell. |
| `packages/runner-dotnet/wwwroot/main.js` | Avalonia's default Blazor-style `main.js`. |

### 6.3 File & package work

| Path | Contents |
|---|---|
| `packages/runtime-bundle/build.mjs` | Shells out to `dotnet publish ../runner-dotnet/ -c Release`; copies `_framework/` into `./`; computes the manifest. |
| `packages/runtime-bundle/_framework/` | Generated. Checked in. Contains `dotnet.js`, `dotnet.wasm`, `icudt.dat`, Avalonia DLLs, BCL DLLs, `blazor.boot.json`-equivalent metadata. |
| `packages/runtime-bundle/bundle-manifest.json` | Written by `build.mjs`. Includes triple-pin block per UI-I3. |
| `packages/runtime-bundle/package.json` | `"files": ["_framework/", "bundle-manifest.json", "README.md"]`; `"pinned": { "avalonia": "...", "carbide": "...", "dotnet": "..." }`. |
| `packages/runtime-bundle/README.md` | Consumption instructions for both the runner (UI-M3) and the offline CLI (UI-M5, separate plan). |

### 6.4 Risks

- **UI-M2-R1. Avalonia.Browser's `_framework/` contents change between versions.** Mitigation: manifest hashes every file; CI gate diffs against a golden on version bumps.
- **UI-M2-R2. Publish-time trimming is too aggressive and breaks `AvaloniaRuntimeXamlLoader.Parse`.** Mitigation: `<TrimmerRootAssembly Include="Avalonia.Markup.Xaml.Loader" />` and an explicit `roots.xml` in the runner csproj. Root-set bootstrap borrowed from `packages/core/src/roots.xml` pattern.
- **UI-M2-R3. Bundle compressed size exceeds 35 MB.** Mitigation: enable Brotli level 11 for static-host consumers; document the `--decompression-method=br` expectation; re-profile the content list against proposal §7.1 for unneeded DLLs.

## 7. UI-M3 — runner boot logic, launcher, `postMessage` protocol end-to-end

**Goal.** Full in-browser compile-and-run. Promote the stub runner to the real `RunnerProgram` (proposal §7.3). Ship `@carbide-ui/avalonia-runner` (HTML/JS shell wrapping the runtime bundle) and `@carbide-ui/launcher` (TS orchestrator). Exercise the `postMessage` protocol (proposal §8). Requires core-P1, core-P2, core-P3 already merged into `@carbide/core`.

### 7.1 Acceptance

```ts
const session = await CarbideSession.initializeAsync({
    sideload: ["@carbide-ui/refs-avalonia"],
});
const project = session.createProject();
project.addSource("App.cs", /* Counter sample from proposal §16.2 */);
const build = await project.build();

const iframe = document.createElement("iframe");
document.getElementById("preview")!.append(iframe);
const handle = await launchInIframe(build, iframe, { appClass: "App" });
// After this resolves, the iframe renders the Counter UI.
// Clicking "+" increments the label.
```

A Playwright fixture drives the above against a chromium headless instance and compares a post-click screenshot against a golden.

### 7.2 Invariants specific to UI-M3

- **UI-M3-I1. Launcher rejects with a typed error within `readyTimeoutMs` if the runner never posts `runnerReady`.** Default 30 s. The error message includes the iframe's `src` and the elapsed time.
- **UI-M3-I2. `reload()` can be called at any time after the first `launchInIframe` resolves.** The runner's `OnLoadMessage` tears down the previous run (per proposal §7.3 sketch) and starts the new one. The `reload()` promise resolves only when the runner posts `runnerRunning` for the new PE.
- **UI-M3-I3. `dispose()` is idempotent.** First call clears bridges and (optionally) removes the iframe; subsequent calls are no-ops.
- **UI-M3-I4. No `window.*` globals are leaked by the launcher.** Event handlers attached to `window.addEventListener("message", ...)` are scoped by origin-and-iframe filtering, and removed on `dispose()`.

### 7.3 Core-side prerequisites (must merge ahead of UI-M3)

- **core-P1: `CarbideOptions.sideload`.** See §3.2.
- **core-P2: Collectible `AssemblyLoadContext` in `ProjectCompiler.RunAsync`.** See §3.2 and proposal §10.2. The change from `Assembly.Load(byte[])` to `context.LoadFromStream(stream)` is small but the surrounding `AssemblyResolve` handler must migrate from `AppDomain.CurrentDomain.AssemblyResolve` to the new ALC's `Resolving` event. Cross-check against `packages/core/src/Services/ProjectCompiler.cs:426` — that handler currently assumes AppDomain scope.
- **core-P3: `BuildResult.peSchemaVersion` + `primaryAssemblyName`.** See §3.2.

### 7.4 Runner-dotnet promotion

Replace UI-M2's stub with the `RunnerProgram` sketch from proposal §7.3. Notes:

- **`JSHost.ImportAsync("runner-bridge", "./runner-bridge.js")` is the runtime→JS direction.** Pair it with `[JSImport]` on `PostReady`/`PostRunning`/`PostError`.
- **`OnLoadMessage` is a `[JSExport]` static method.** Requires `System.Runtime.InteropServices.JavaScript` package (already part of .NET 10 browser workload).
- **Teardown ordering in `OnLoadMessage`.** As sketched in the proposal:
  1. Set the previous `ISingleViewApplicationLifetime.MainView` to `null` (releases the visual tree but keeps the `Application` alive).
  2. `s_currentContext?.Unload()` drops the previous PE.
  3. Create a fresh collectible `AssemblyLoadContext` and `LoadFromStream` the new PE.
  4. Instantiate the new `App` via `Activator.CreateInstance`.
  5. On first run: `builder.StartBrowserAppAsync("out")`. On later runs: swap the `MainView`.

- **Catch-all try/catch wraps `OnLoadMessage`.** Any exception becomes `PostError("..." , kind: "load" | "runtime")`.

### 7.5 Runner HTML/JS shell

`packages/runner/` owns the HTML shell that packages the bundle for iframe embedding.

| Path | Contents |
|---|---|
| `packages/runner/index.html` | `<html>`, `<head>`, `<body><div id="out" style="width:100%;height:100%"></div><script type="module" src="./main.js"></script></body>`. |
| `packages/runner/main.js` | Imports `./_framework/dotnet.js`, runs `dotnet.runMain(Avalonia.UI.Runner)` — the usual Avalonia browser boot. |
| `packages/runner/runner-bridge.js` | As in proposal §7.3. Listens for `message` events, forwards to `globalThis.__carbideRunnerInterop.OnLoadMessage`. |
| `packages/runner/_framework/` | Symlink (or copied tree) from `@carbide-ui/avalonia-runtime-bundle`'s `_framework/`. `package.json` lists `"dependencies": { "@carbide-ui/avalonia-runtime-bundle": "<version>" }` and a postinstall step materialises the tree. |
| `packages/runner/package.json` | `"files": ["index.html", "main.js", "runner-bridge.js"]`; bundle referenced as dep. |
| `packages/runner/README.md` | Iframe embedding instructions; security considerations (origin, CSP). |

### 7.6 Launcher TypeScript

The launcher lives in `packages/launcher/` and depends structurally on `BuildResult`.

| Path | Contents |
|---|---|
| `packages/launcher/src/index.ts` | Exports `launchInIframe`, `LaunchHandle`, `LaunchOptions` per proposal §7.4. |
| `packages/launcher/src/protocol.ts` | Protocol schema types (`LoadMessage`, `RunnerReady`, `RunnerRunning`, `RunnerError`); version constant. |
| `packages/launcher/src/resolve-runner.ts` | Resolves the runner's `index.html` URL at runtime — Node: `import.meta.resolve("@carbide-ui/avalonia-runner/index.html")`; browser (bundler): static URL import via `new URL("...", import.meta.url)`. |
| `packages/launcher/src/timeout.ts` | `readyTimeoutMs` enforcement helper. |
| `packages/launcher/test/` | Vitest unit tests + a Playwright integration test. |

### 7.7 `postMessage` protocol (normative for v1)

Frozen at schema version 1. Additions may follow per UI-I5; removals and renames do not.

```ts
// Launcher → Runner
type LoadMessage = {
    type: "load";
    schemaVersion: 1;
    peBase64: string;
    pdbBase64: string | null;
    appClass: string;
    runArgs: string[] | null;
};

// Runner → Launcher
type RunnerReadyMessage   = { type: "runnerReady";   schemaVersion: 1; };
type RunnerRunningMessage = { type: "runnerRunning"; schemaVersion: 1; };
type RunnerErrorMessage   = {
    type: "runnerError";
    schemaVersion: 1;
    message: string;
    kind: "load" | "runtime" | "teardown";
};
```

- Origin filter: launcher validates `event.source === iframe.contentWindow`. No wildcard.
- Envelope: all messages are plain objects (not `Transferable`). PE bytes are base64'd in v1; `ArrayBuffer` transfer is a v2 consideration.
- Ordering: exactly one `runnerReady` per iframe load; zero-or-more `runnerRunning` / `runnerError` per `load`.
- Rejection: message with unknown `schemaVersion` → `runnerError { kind: "load", message: "unsupported schemaVersion N" }`.

### 7.8 File & package work summary

| Area | Paths touched |
|---|---|
| core-P1 (§10.1) | `packages/core/src/ts/session.ts`, `packages/core/src/ts/types.ts`, maybe `packages/core/src/ts/node.ts` for file-system resolve. |
| core-P2 (§10.2) | `packages/core/src/Services/ProjectCompiler.cs` (collectible ALC + `Resolving` handler). Tests: `packages/core/test/ProjectCompilerTests.cs` (if present). |
| core-P3 (§10.3) | `packages/core/src/ts/types.ts` (add optional `peSchemaVersion`, `primaryAssemblyName`); `packages/core/src/Services/BuildResult.cs`. |
| Runner promotion | `packages/runner-dotnet/Program.cs` → `RunnerProgram.cs`; add `wwwroot/runner-bridge.js`. |
| Runner package | `packages/runner/*` (see 7.5). |
| Launcher package | `packages/launcher/*` (see 7.6). |
| CI | Add Playwright job for launcher integration test; add size gate checks against UI-I2. |

### 7.9 Risks

- **UI-M3-R1. `AssemblyLoadContext.Unload()` races with still-running event handlers in the user app.** If the previous `App`'s `Application.Current` still holds delegates into unloaded types, GC finalisation may throw. Mitigation: before `Unload()`, explicitly null out `MainView`, and verify no dispatcher timer / `System.Threading.Timer` in the user code holds captured references past teardown. Document the v1 limitation: static state in the old `App` lingers (proposal §12 Q.3).
- **UI-M3-R2. `postMessage` with large base64 strings.** A 100 KB PE base64-encodes to 133 KB + JSON-escape overhead. For v1 this is tolerable; if a user ships a multi-megabyte PE, the launcher can switch to `Transferable` `ArrayBuffer` at schema version 2. Document the v1 payload ceiling in the launcher README: tested up to 2 MB PE.
- **UI-M3-R3. `runner-bridge.js` `import.meta.url` resolution varies across bundlers.** Mitigation: the launcher resolves the runner's `index.html` via its own package URL, which supports bundler rewrites; `runner-bridge.js` is loaded by the runner page itself, not the launcher, so its path is runner-page-relative and does not depend on bundler logic.
- **UI-M3-R4. Boot race: launcher sends `load` before runner posts `runnerReady`.** The launcher buffers sends until `runnerReady`; an internal queue flushes on transition. This is a testable property.

## 8. UI-M4 — runtime XAML via `.axaml`-as-document

**Goal.** Carbide-side convenience: user calls `project.addSource("MainView.axaml", xamlText)`. Carbide detects the `.axaml` extension, generates a hidden companion `.g.cs` that embeds the XAML string as a resource and wires `InitializeComponent()` to call `AvaloniaRuntimeXamlLoader.Parse<T>(xamlString, rootObject: this, parentAssembly: userAssembly)`.

### 8.1 Acceptance

- Proposal §16.4 fixture (MVVM + runtime-parsed `.axaml`) builds and renders under `launchInIframe`. Golden-screenshot-tested.
- `project.addSource("MainView.axaml", xaml)` produces no user-visible `.cs` in the compiled output; the companion is a hidden generated unit keyed by the document's name.
- User-written `public partial class MainView : UserControl { public MainView() { InitializeComponent(); } }` compiles unchanged and renders the XAML at runtime.

### 8.2 Change surface

This lives entirely in `@carbide/core`'s document-handling layer. It is **not** a §10-style core API change — it's an extension to the existing source-handling code path that already special-cases C# sources.

| Path | Change |
|---|---|
| `packages/core/src/Services/ProjectCompiler.cs` | New branch in the document-adding path: if `name.EndsWith(".axaml", OrdinalIgnoreCase)`, generate a paired `<name>.g.cs` document that contains the runtime-parse companion. |
| `packages/core/src/Services/AxamlCompanionGenerator.cs` (new) | Deterministic generator producing the partial-class companion with `InitializeComponent()` body. |
| `packages/core/test/AxamlCompanionGeneratorTests.cs` (new) | Golden-file tests for the generator's output across the §9.3 sample set. |

### 8.3 Risks

- **UI-M4-R1. Ambiguity when the `.axaml` file declares `x:Class`.** The generator's output must match the `x:Class` fully-qualified name; otherwise the partial class does not match. Mitigation: parse the XAML's root element's `x:Class` attribute (cheap string-scan) and use it for the companion's namespace/class. If `x:Class` is absent, error out with a clear message and a pointer to §16.3's string-literal path.
- **UI-M4-R2. Namespaces in XAML reference types in the user assembly.** `AvaloniaRuntimeXamlLoader.Parse(xaml, parentAssembly: userAssembly)` resolves `clr-namespace:MyApp;assembly=MyApp` against the given assembly. Mitigation: the generator passes `Assembly.GetExecutingAssembly()` as `parentAssembly` so the runtime resolves user types correctly.
- **UI-M4-R3. Hidden companion leaks into user-facing diagnostics.** Roslyn diagnostics from the companion should be tagged as generator-origin and suppressed in the user-facing diagnostic stream, or re-mapped to the `.axaml` source. Mitigation: simplest v1 — suppress generator-origin warnings; surface only errors (marked as originating from the companion) with a note "XAML: <excerpt>". Refinement can wait until someone hits it.

## 9. UI-M6 — multiple preview polish & production hardening

**Goal.** Support N concurrent iframes driven from one `CarbideSession`. Ship production-grade error messages, comprehensive API docs, a cross-browser test matrix, and at least one sample repo entry per proposal §16.

### 9.1 Acceptance

- `samples/multi-preview/` hosts four iframes; each shows a different Avalonia sample; all four are driven from one `CarbideSession`. Playwright fixture verifies all four render without cross-talk.
- `npm run test:browser` runs the launcher + runner end-to-end on Chromium, Firefox, and WebKit. (Firefox and WebKit are new coverage beyond UI-M3.)
- `runnerError { kind }` taxonomy is populated per message with a curated set of `message` strings (no raw exception `.ToString()` text leaks unless wrapped by a `kind: "runtime"` framing).
- API docs (README for each of the four packages) describe: boot lifecycle, protocol versioning, iframe sizing expectations, teardown semantics, known limitations.
- `samples/` at the top of `src/Carbide.UI/` has entries corresponding to proposal §16.1, §16.2, §16.3, §16.4, §16.5. Each sample includes a `README.md` with ambition-tier labelling per §9.3.

### 9.2 Change surface

| Area | Work |
|---|---|
| Launcher | Document concurrent-iframe usage; verify `messageHandler` filtering is per-iframe (tested by one iframe seeing only its own messages); add `LaunchHandle.id` for logging. |
| Runner | Verify the runner handles multiple instances in different iframes without shared globals (`globalThis.__carbideRunnerInterop` is per-iframe by definition; confirm in docs). |
| Samples | Five fixture projects; `samples/README.md` cross-links to them. |
| CI | Cross-browser matrix; flake-management (retry on known Firefox WebGL context-limit hits per UI-R9). |
| Docs | Per-package README completion; `src/Carbide.UI/README.md` promoted to the canonical "getting started" doc. |

### 9.3 Risks

- **UI-M6-R1. Multiple iframes hit browser WebGL context limits.** Modern browsers cap WebGL contexts at ~16 per tab; Avalonia's Skia backend uses one per canvas. Mitigation: document the cap; expose a `LaunchOptions.renderBackend: "gpu" | "software2d"` knob that forwards to Avalonia's backend selector. The software fallback keeps the multi-preview experience working past the cap at the cost of frame rate.
- **UI-M6-R2. Cross-browser divergence in `Transferable` / iframe sandbox semantics.** Mitigation: the v1 protocol uses plain JSON objects (no transfers), sidestepping the worst cross-browser differences. Firefox's stricter iframe parent-window messaging rules are handled by origin-filtered handlers.

## 10. Cross-cutting concerns

### 10.1 Testing strategy

- **Unit (TS).** `packages/launcher/test/` with Vitest: protocol encode/decode, timeout behaviour, resolve-runner URL logic.
- **Unit (C#).** `packages/runner-dotnet/` has no unit test harness in v1 (the runner is a tiny boot class); its behaviour is verified through integration. If the runner grows, spin out a companion `Avalonia.UI.Runner.Tests.csproj`.
- **Integration (Playwright).** `packages/launcher/test/integration/` drives a headless browser through the full flow: load launcher + runner from a packaged npm install, compile a sample via `@carbide/core`, `launchInIframe`, assert pixel output.
- **Cross-browser.** Chromium (every CI run); Firefox and WebKit gated at UI-M6.
- **Size gates.** Per-package compressed-size measurement on every CI run; hard fail at UI-I2's thresholds.
- **Hash gates.** `refpack.json` and `bundle-manifest.json` hashes verified on every publish; drift triggers a manual review.

### 10.2 Documentation

- `src/Carbide.UI/README.md` — package-family overview + getting-started snippet. Points to each package's own README.
- Each package's `README.md` — what it is, how to install, pinning rules, caveats. Follows `packages/refs-net10.0/README.md` shape.
- `src/Carbide.UI/docs/` — architecture notes; protocol spec formal doc; a `drift/` subdirectory for tracking Avalonia upstream drift (per proposal §7.6).
- `docs/planning/milestones/carbide-UI-M*-detailed-plan__*.md` — per-milestone detailed plans, spun out as each milestone opens. This parent plan is the overview; the milestone plans are the working plans with per-file change tables and per-PR acceptance.

### 10.3 Versioning

- `@carbide/core` follows its own semver; Approach B adds no breaking change.
- `@carbide-ui/refs-avalonia` versions: `<AvaloniaMajor>.<AvaloniaMinor>.<CarbidePatch>`.
- `@carbide-ui/avalonia-runtime-bundle` versions: `<AvaloniaMajor>.<AvaloniaMinor>.<CarbidePatch>` — paired with the ref-pack.
- `@carbide-ui/avalonia-runner` and `@carbide-ui/launcher` follow their own semver; a runner-launcher mismatch across v1's protocol schema is tolerated (both understand schema version 1).
- Every `package.json` in `@carbide-ui/*` records its triple-pin or Avalonia-pin in a `"pinned"` metadata block per UI-I3.

### 10.4 Security considerations

- **CSP.** The runner iframe loads `./_framework/dotnet.wasm` and parses JS; requires `script-src 'wasm-unsafe-eval' 'self'` (or the stricter `'unsafe-eval'` depending on the user program's JIT-heavy paths). Document in `packages/runner/README.md`.
- **Sandbox.** Recommend `sandbox="allow-scripts allow-same-origin"` on the iframe for standard usage. Cross-origin iframes require the host page to serve the runner's `index.html` from a known origin; document the `origin` filter in the launcher.
- **PE content.** The PE is user-authored C# compiled by Carbide; the runner trusts the launcher implicitly (same page, same window). No cross-origin PE delivery in v1.
- **No network I/O from the runner.** The runner loads the bundle from its own origin and then runs Avalonia locally; no calls to external services originate from the runner infrastructure. User programs can of course use `HttpClient` through Avalonia's normal browser APIs.

### 10.5 Drift management

- A `src/Carbide.UI/docs/drift/` directory, per proposal §7.6, holds dated drift reports on upstream Avalonia + .NET releases.
- CI has a nightly job (opt-in, not blocking trunk) that rebuilds the runtime bundle against the next Avalonia minor release and runs the UI-M6 sample set. Failures produce a drift report stub in `docs/drift/` that a maintainer fills in.

## 11. Owner decisions (resolved 2026-04-21)

All four calls from proposal §13 are recorded. UI-M0 is unblocked and has shipped (see the ✓ marker on UI-M0's heading in §4).

1. ✓ **Companion-project concept approved.** The "Companion projects" paragraph was added to [`carbide-vision.md`](../carbide-vision__2026-04-17__16-16-47-000000.md) §13 on 2026-04-21, naming `@carbide-ui/*` as the first companion and explicitly stating that `@carbide/core`'s N.1–N.8 non-goals do not bind companions (and vice versa).
2. ✓ **Avalonia target: 12.x latest stable.** 13.0 is speculative as of the decision date and not on the v1 track. The exact 12.x patch is pinned in [`packages/refs-avalonia/scripts/build.mjs`](../../../Carbide.UI/packages/refs-avalonia/scripts/build.mjs) when UI-M1 opens, and in [`packages/runtime-bundle/package.json`](../../../Carbide.UI/packages/runtime-bundle/package.json)'s `"pinned"` block when UI-M2 opens. Bumping the 12.x patch is a routine PR; bumping the minor or moving to 13.x engages the drift workflow (§10.5).
3. ✓ **.NET target: `net10.0-browser`.** Mirrors Carbide core. The runner csproj already carries this — [`Avalonia.UI.Runner.csproj`](../../../Carbide.UI/packages/runner-dotnet/Avalonia.UI.Runner.csproj) sets `<TargetFramework>net10.0-browser</TargetFramework>` at UI-M0.
4. ✓ **npm scope: `@carbide-ui/*` (final).** The working name stays. Fits the vision amendment's `@carbide-<topic>/*` pattern exactly. If Carbide's own `@carbide` scope is renamed later (proposal Q.1 fallback), the companion family follows in lockstep — no need to re-open this plan.

The authoritative decision record lives in [proposal §13](../proposals/carbide-ui-avalonia-integration-proposal__2026-04-18__22-04-08-231875__2bc4122b7f3f.md); this section mirrors it for plan-first readers.

## 12. Open questions — ratified positions (2026-04-21)

All proposal §12 Positions relevant to this plan are ratified. Re-opening any of them routes through the proposal's §12, not this document.

- ✓ **Q.2 App-class discovery.** `LaunchOptions.appClass` is **required in v1**. v1.1 may infer from `BuildResult.primaryAssemblyName + ".App"` (the field is shipped via core-P3 §10.3; the inference is a launcher-side addition, non-breaking).
- ✓ **Q.3 Teardown across runs.** v1 swaps `MainView`; v2 supports full `Application` swap. The v1 limitation — static state in the previous `App` lingers — is a documented property, tested in UI-M3's fixture set, and recalled in `@carbide-ui/launcher`'s README.
- ✓ **Q.4 `Console.WriteLine` forwarding.** Not in v1. If users ask for it, add `{ type: "stdout", text: string }` at protocol schema version 2 — the runner can already intercept via `Console.SetOut`. Zero runner work until a user asks.
- ✓ **Q.6 XAML without Carbide M12.** v1 answer is runtime parse via `AvaloniaRuntimeXamlLoader` (UI-M4); UI-M7 is the compile-time fix, gated on Carbide M12. Documented in `@carbide-ui/refs-avalonia`'s README per plan §9.3.
- ✓ **Q.7 Ref-pack delivery.** v1 ships via npm (`@carbide-ui/refs-avalonia`). CDN + IndexedDB remains on the table as a v2 move if npm install cost becomes a real complaint; no action until it does.

## 13. Risks (roll-up from proposal §14, per-phase refinement)

| # | Risk | Phase | Mitigation |
|---|---|---|---|
| UI-R1 | Avalonia upstream breaks runner assumptions (e.g. `ISingleViewApplicationLifetime.MainView` semantics). | UI-M2, UI-M3, ongoing | Pinned version; nightly drift job (see §10.5); drift report in `docs/drift/`. |
| UI-R2 | Iframe `<canvas>` DPI/resize behaviour misbehaves on some host pages. | UI-M3 | Document non-zero iframe size before `load`; runner posts `runnerError { kind: "load" }` on canvas init failure. |
| UI-R3 | `.axaml`-heavy apps hit the source-generator gap. | UI-M4 | UI-M4 provides runtime parse; UI-M7 is the eventual fix; per-sample support matrix. |
| UI-R4 | core-P2 (collectible ALC) regresses existing console-program behaviour. | core-P2 | Ship core-P2 as a standalone PR ahead of UI-M3; run full Carbide golden corpus before merging. |
| UI-R5 | Combined bundle exceeds 80 MB budget. | UI-M2 | Per-package size gates; Brotli for hosting; consider CDN fetch for runtime bundle in v2. |
| UI-R6 | `postMessage` schema mistakes cause silent deadlocks. | UI-M3, UI-M6 | `schemaVersion` on every message; `readyTimeoutMs` default 30 s; golden-path integration test for load/reload/teardown. |
| UI-R7 | Users expect AOT-compiled perf (animations, high-freq UI). | UI-M6 docs | Interpreted-user-code gap documented prominently; position companion as "playground/verification/demo". |
| UI-R8 | COOP/COEP confusion in multithread attempts. | UI-M8 (deferred) | v1 single-threaded; UI-M8 adds documented headers + serve helper. |
| UI-R9 | Multiple iframes exceed WebGL context limit. | UI-M6 | Multi-iframe tests; document cap; `LaunchOptions.renderBackend` software fallback. |
| UI-R10 | Ref-pack picks up analyzer / build-task assemblies. | UI-M1 | `ref/`-only filter per UI-I8; CI inventory gate on pack contents. |
| UI-R11 | `AssemblyLoadContext.Unload()` leaks via timer captures in the previous run. | UI-M3 | Per-run teardown documented; static state leak documented (Q.3); v2 full-`Application` swap. |
| UI-R12 | `postMessage` base64 payload cost dominates for large PEs. | UI-M3, UI-M6 | v1 ceiling tested to 2 MB PE; `Transferable` `ArrayBuffer` is a v2 path. |

## 14. Change control

This plan is a working document.

- **Milestone completion.** When UI-M<N> lands, add `✓ shipped (YYYY-MM-DD)` next to the milestone heading and link to the shipped artefact (PR URL + tag).
- **Architectural re-opens.** Route through the proposal's §12 Open Questions. This plan's §12 mirrors, not authors, them.
- **Per-milestone detailed plans.** Spin out `docs/planning/milestones/carbide-UI-M<N>-detailed-plan__<timestamp>__<suffix>.md` as each milestone opens; link from this plan's §4–§9 headings.
- **Size/hash gates.** Changes to UI-I2 thresholds or the manifests' hash policy require a note here and a corresponding CI PR.
