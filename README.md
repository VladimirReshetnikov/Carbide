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
├── AGENTS.md      # symbolic link to README.md
├── CLAUDE.md      # symbolic link to README.md
├── LICENSE        # Apache-2.0 repository license
└── README.md      # repository overview and canonical guidance
```

## Where to start

- Read [`Carbide/README.md`](Carbide/README.md) for the core framework, package map, CLI, and build workflow.
- Read [`Carbide/docs/README.md`](Carbide/docs/README.md) for design, planning, research, and current-state documentation.
- Read [`Carbide.UI/README.md`](Carbide.UI/README.md) for the Avalonia browser integration architecture and tests.

## Build and validation

The repository requires the .NET 10 SDK version pinned in [`global.json`](global.json), Node.js 20 or newer, and the `wasm-tools` workload. Browser tests additionally require Playwright Chromium. The SDK pin also fixes the Mono WebAssembly runtime at 10.0.6 so the bundled upstream notices remain version-accurate.

Continuous integration runs in [GitHub Actions](.github/workflows/ci.yml) on pushes to `main`, `feature/**` branches, and pull requests: the license/provenance check and ESLint, the pure-Node package suites, the shell workstream's C# xUnit suites, the full core + CLI validation (including a browser smoke slice), and the Carbide.UI launcher suite. Lint locally with `npm ci && npm run lint` at the repository root.

Run the repository-level license and provenance consistency check from the repository root:

```powershell
node scripts/check-licenses.mjs
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

## History and provenance

The imported Tools lineage contains 139 commits beginning with the original `Carbide project kick-off` on 2026-04-17. It retains the predecessor documents that lived under Tools' `docs/proposals/carbide/` and `docs/reports/` before they were renamed into the Carbide workspace. Commit hashes necessarily changed when repository-relative paths changed from `src/Carbide*` to the top-level directories. The annotated tag `extraction-2026-07-18` identifies the last imported commit before standalone-repository adjustments.

Pre-extraction documents that record a `Repository HEAD` value retain their original Tools commit as historical provenance. Resolve those hashes in the [Tools repository](https://github.com/VladimirReshetnikov/Tools), not in this rewritten repository.

## Working guidance

- Keep framework work in `Carbide/` and Avalonia integration work in `Carbide.UI/`; preserve the frontend/core boundary and sibling layout.
- Read the relevant workspace README and documentation before changing package contracts or build orchestration.
- Use the real .NET and Node toolchains for validation. Keep generated packages, build output, downloaded reference packs, and test artifacts untracked under the workspace-local ignore rules.
- Keep package-local `LICENSE` copies byte-for-byte aligned with the root license, and ship attribution and third-party notices with any package that redistributes upstream material. Validate both rules with `node scripts/check-licenses.mjs`.
- Treat active READMEs and architecture documents as current-state descriptions. Historical material belongs under an `archived/` tree or must be explicitly identified as historical.
- Commit validated, self-contained changes directly to `main`, include a descriptive message and `Co-Authored-By` trailer, and push to `origin/main` unless a task explicitly says otherwise.
