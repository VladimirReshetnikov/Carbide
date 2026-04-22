# @carbide-ui/launcher

Status: **UI-M0 stub.** Calling `launchInIframe` throws.

At UI-M3 this package will ship the TypeScript orchestrator that bridges a Carbide `BuildResult` into [`@carbide-ui/avalonia-runner`](../runner/README.md) via `postMessage`. The launcher carries no runtime dependency on `@carbide/core` (plan UI-I9); `BuildResult` is consumed as a structural type.

See the [implementation plan](../../../Carbide/docs/planning/carbide-ui-avalonia-approach-b-plan__2026-04-21__23-40-46-000000__d3b1a638db2c.md) §7.6.
