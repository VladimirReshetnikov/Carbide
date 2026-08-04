# Carbide repository

[![CI](https://github.com/VladimirReshetnikov/Carbide/actions/workflows/ci.yml/badge.svg)](https://github.com/VladimirReshetnikov/Carbide/actions/workflows/ci.yml)

This repository is the standalone home of Carbide, a C# compile-and-run framework for environments without the .NET SDK, and Carbide.UI, its Avalonia browser UI integration family.

This document is also the canonical repository guidance for maintainers and coding agents. `AGENTS.md` and `CLAUDE.md` point here.

## License and provenance

Carbide-authored source code, documentation, tests, and samples are licensed under the [Apache License 2.0](LICENSE), with copyright held collectively by Carbide Contributors. No sample or first-party subtree uses different license terms. Selected third-party-derived files and redistributed artifacts retain their upstream terms at file or artifact scope, as described below.

Carbide began as a structural fork of [JakeYallop/WasmSharp at commit `2f8c93b`](https://github.com/JakeYallop/WasmSharp/tree/2f8c93bfa39f2068ad932a748ba23f740075327c), which is also Apache-2.0. The [core attribution record](Carbide/packages/core/ATTRIBUTION.md) documents the imported and adapted source files and the changes made after the fork. This provenance is part of Carbide's maintained documentation, not merely historical background.

Identified third-party source and redistributed artifacts retain their respective upstream terms. Their exact scope and notices are recorded in adjacent `ATTRIBUTION.md` or `THIRD_PARTY_NOTICES.md` files. Those terms apply only to the identified material and do not change the Apache-2.0 license of surrounding Carbide work.

## Top-level structure

```text
.
├── Carbide/       # SDK-free C# compilation, execution, CLI, shell, and package workspaces
├── Carbide.UI/    # Avalonia browser runtime, launcher, reference pack, and samples
├── api/           # frozen public API surface reports for the published packages
├── scripts/       # repository gates: licenses, changelogs, wire contract, API surface
├── AGENTS.md      # symbolic link to README.md
├── CHANGELOG.md   # repository-level release notes
├── CLAUDE.md      # symbolic link to README.md
├── LICENSE        # Apache-2.0 repository license
├── README.md      # repository overview and canonical guidance
└── RELEASING.md   # publish procedure and compatibility rules
```

## Where to start

- Read [`Carbide/README.md`](Carbide/README.md) for the core framework, package map, CLI, and build workflow.
- Read [`Carbide/docs/README.md`](Carbide/docs/README.md) for design, planning, research, and current-state documentation.
- Read [`Carbide.UI/README.md`](Carbide.UI/README.md) for the Avalonia browser integration architecture and tests.
- Read [`RELEASING.md`](RELEASING.md) before changing anything a published package exports.

## Published packages

Five packages ship in lock-step at a single version, currently `0.1.0`: `@carbide/core`, `@carbide/cli`, `@carbide/msbuild-lite`, `@carbide/nuget`, and the opt-in reference pack `@carbide/refs-net10.0`. Release notes live in [`CHANGELOG.md`](CHANGELOG.md) and in each package's own changelog. `Carbide.UI` packages are private and are not part of this release train.

Three contracts are frozen from `0.1.0` onward, each with its own gate:

- the exported TypeScript surface, CLI flags, exit codes, and error categories — recorded in [`api/`](api/README.md);
- the JSExport wire payloads between the TypeScript and C# layers — pinned at `SCHEMA_VERSION` 5 by golden fixtures under `Carbide/packages/core/test/fixtures/wire/`;
- package versions and release notes — kept in lock-step across all five packages.

## Build and validation

The repository requires the .NET 10 SDK version pinned in [`global.json`](global.json), Node.js 20 or newer, and the `wasm-tools` workload. Browser tests additionally require Playwright Chromium. The SDK pin also fixes the Mono WebAssembly runtime at 10.0.6 so the bundled upstream notices remain version-accurate.

Continuous integration runs in [GitHub Actions](.github/workflows/ci.yml) on pushes to `main`, `feature/**` branches, and pull requests: the repository consistency gates and ESLint, the public API surface freeze, the pure-Node package suites, the shell workstream's C# xUnit suites, the full core + CLI validation (including a browser smoke slice), and the Carbide.UI launcher suite.

Run the repository-level gates from the repository root. They need no .NET toolchain:

```powershell
npm ci
npm run check
```

That runs the license and provenance check, the changelog and release-metadata check, the TypeScript ↔ C# wire-contract check, and ESLint. The API surface freeze needs the TypeScript packages built first:

```powershell
node scripts/build-ts-packages.mjs
node scripts/api-surface.mjs
```

Core validation proceeds in dependency order:

```powershell
Set-Location Carbide\packages\refs-net10.0
node scripts\build.mjs

Set-Location ..\core
dotnet publish -c Release src\Carbide.Core.csproj
npm install
npm run build:ts
npm run build:test-fixtures
npm run test:fast

Set-Location ..\msbuild-lite
npm install
npm run build
npm test

Set-Location ..\nuget
npm install
npm run build
npm test

Set-Location ..\cli
npm install
npm run build
npm test
```

Carbide.UI validation starts with its reference and runtime bundles, then exercises the launcher:

```powershell
Set-Location Carbide.UI\packages\refs-avalonia
npm run build

Set-Location ..\runtime-bundle
npm run build

Set-Location ..\launcher
npm install
npm run build
npm test
npm run test:browser

Set-Location ..\..\
node scripts\measure-sizes.mjs
```

See the workspace READMEs for the complete and faster smoke-test variants.

## Working guidance

- Keep framework work in `Carbide/` and Avalonia integration work in `Carbide.UI/`; preserve the frontend/core boundary and sibling layout.
- Read the relevant workspace README and documentation before changing package contracts or build orchestration.
- Use the real .NET and Node toolchains for validation. Keep generated packages, build output, downloaded reference packs, and test artifacts untracked under the workspace-local ignore rules.
- Keep package-local `LICENSE` copies byte-for-byte aligned with the root license, and ship attribution and third-party notices with any package that redistributes upstream material. Validate both rules with `node scripts/check-licenses.mjs`.
- A change to anything a published package exports must land together with its regenerated [`api/`](api/README.md) report (`node scripts/api-surface.mjs --write`) and its `CHANGELOG.md` entry under `## [Unreleased]`. A change to a JSExport payload shape bumps `SCHEMA_VERSION` on both the TypeScript and C# sides and adds a new frozen payload; the existing ones are append-only.
- Treat active READMEs and architecture documents as current-state descriptions. Historical material belongs under an `archived/` tree or must be explicitly identified as historical.
- Commit validated, self-contained changes directly to `main`, include a descriptive message and `Co-Authored-By` trailer, and push to `origin/main` unless a task explicitly says otherwise.
