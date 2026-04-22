# Carbide.UI

Companion project family for Carbide — Avalonia GUI integration via the cross-frame runner pattern (Approach B of the [integration proposal](../Carbide/docs/proposals/carbide-ui-avalonia-integration-proposal__2026-04-18__22-04-08-231875__2bc4122b7f3f.md)).

Status: **UI-M0 scaffolding.** Packages are stubs; no Avalonia, no runtime bundle, no protocol shipped yet. See the [implementation plan](../Carbide/docs/planning/carbide-ui-avalonia-approach-b-plan__2026-04-21__23-40-46-000000__d3b1a638db2c.md) for the full phase list and acceptance gates per milestone.

## Packages

- [`packages/refs-avalonia/`](packages/refs-avalonia/) — `@carbide-ui/refs-avalonia`: compile-time reference assemblies for Avalonia. Ships at UI-M1.
- [`packages/runtime-bundle/`](packages/runtime-bundle/) — `@carbide-ui/avalonia-runtime-bundle`: pre-built Avalonia.Browser `_framework/` tree. Ships at UI-M2.
- [`packages/runner/`](packages/runner/) — `@carbide-ui/avalonia-runner`: HTML/JS shell that embeds the runtime bundle inside an iframe. Ships at UI-M3.
- [`packages/launcher/`](packages/launcher/) — `@carbide-ui/launcher`: TypeScript orchestrator that bridges a Carbide `BuildResult` into a running runner via `postMessage`. Ships at UI-M3.
- [`packages/runner-dotnet/`](packages/runner-dotnet/) — internal C# project compiled into `@carbide-ui/avalonia-runtime-bundle`. Not published to npm.

## Size budgets (UI-I2)

Enforced in CI starting at UI-M2; [`scripts/measure-sizes.mjs`](scripts/measure-sizes.mjs) makes them enforceable locally from UI-M0 onward.

- `@carbide-ui/avalonia-runtime-bundle`: ≤ 35 MB compressed.
- `@carbide-ui/refs-avalonia`: ≤ 2 MB compressed (≤ 5 MB uncompressed).
- `@carbide-ui/avalonia-runner`: ≤ 100 KB on top of the bundle.
- `@carbide-ui/launcher`: ≤ 50 KB minified.

## Local size measurement

```bash
cd src/Carbide.UI
node scripts/measure-sizes.mjs
```

Runs `npm pack --dry-run --json` per package and prints the resulting tarball size vs. its UI-I2 budget. Exit code is `0` on all-within-budget, non-zero otherwise.

## Owner decisions (resolved 2026-04-21)

All four calls from proposal §13 are recorded; UI-M0 is shipped.

1. ✓ Companion-project clause added to [`../Carbide/docs/carbide-vision__2026-04-17__16-16-47-000000.md`](../Carbide/docs/carbide-vision__2026-04-17__16-16-47-000000.md) §13.
2. ✓ Avalonia target: **12.x latest stable** (specific patch pinned at UI-M1).
3. ✓ .NET target: **`net10.0-browser`** (matches Carbide core).
4. ✓ npm scope: **`@carbide-ui/*`** (final).

Authoritative record: [proposal §13](../Carbide/docs/proposals/carbide-ui-avalonia-integration-proposal__2026-04-18__22-04-08-231875__2bc4122b7f3f.md).
