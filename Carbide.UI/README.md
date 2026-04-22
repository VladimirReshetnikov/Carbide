# Carbide.UI

Companion project family for Carbide — Avalonia GUI integration via the cross-frame runner pattern (Approach B of the [integration proposal](../Carbide/docs/proposals/carbide-ui-avalonia-integration-proposal__2026-04-18__22-04-08-231875__2bc4122b7f3f.md)).

Status: **UI-M0 through UI-M6 shipped.** Proof-of-concept Avalonia apps compile in a browser-hosted `@carbide/core` session, launch into iframes via `@carbide-ui/launcher`, and render on an Avalonia.Browser runtime. Verified end-to-end in Chromium; cross-browser coverage (Firefox, WebKit) is noted as a follow-up.

See the [implementation plan](../Carbide/docs/planning/carbide-ui-avalonia-approach-b-plan__2026-04-21__23-40-46-000000__d3b1a638db2c.md) for the full phase list, acceptance criteria, and architectural decisions.

## Getting started

```ts
import { CarbideSession, BrowserHostAdapter } from "@carbide/core";
import { launchInIframe } from "@carbide-ui/launcher";

const adapter = new BrowserHostAdapter({
    frameworkAssetsBaseUrl: "/path/to/@carbide/core/_framework/",
    sideloadBaseUrl:       "/node_modules", // or wherever node_modules is served
});

const session = await CarbideSession.initializeAsync({
    hostAdapter: adapter,
    sideload: ["@carbide-ui/refs-avalonia"],
});

const project = session.createProject({ assemblyName: "MyApp" });
project.addSource("App.cs", /* your Avalonia.Application-derived type */);

const build = await project.build();
if (!build.success) throw new Error("compile failed");

const iframe = document.createElement("iframe");
iframe.style.width  = "100%";
iframe.style.height = "400px";
document.body.append(iframe);

const handle = await launchInIframe(build, iframe, {
    appClass: "MyApp.App",
});

// Later:
await handle.reload(await project.build()); // iframe-reboot + new PE
handle.dispose(/* removeIframe */ true);
```

Worked examples: [`samples/`](samples/README.md).

## Packages

- [`packages/refs-avalonia/`](packages/refs-avalonia/) — `@carbide-ui/refs-avalonia`: compile-time Avalonia reference assemblies. Pinned to Avalonia 12.0.1; 8 DLLs; 1.6 MB packed.
- [`packages/runtime-bundle/`](packages/runtime-bundle/) — `@carbide-ui/avalonia-runtime-bundle`: pre-built Avalonia.Browser WebAssembly runtime. 415 files; 25 MB packed; 11 MB effective cold load in a Brotli-capable browser. Acts directly as the runner's iframe src since UI-M3 (the shell files live at the bundle root).
- [`packages/runner/`](packages/runner/) — `@carbide-ui/avalonia-runner`: deprecated pointer to `@carbide-ui/avalonia-runtime-bundle`. Reserved for a future revision if runner/bundle split becomes useful again.
- [`packages/launcher/`](packages/launcher/) — `@carbide-ui/launcher`: TypeScript orchestrator. Ships `launchInIframe` + `LaunchHandle`; implements the `postMessage` protocol client-side.
- [`packages/runner-dotnet/`](packages/runner-dotnet/) — internal C# project compiled into the runtime bundle. Not published to npm.

## Size budgets (UI-I2)

Enforced per [`scripts/measure-sizes.mjs`](scripts/measure-sizes.mjs).

| Package | Current | Budget |
|---|---:|---:|
| `@carbide-ui/refs-avalonia` | 1.65 MB | ≤ 2 MB |
| `@carbide-ui/avalonia-runtime-bundle` | 25.1 MB | ≤ 35 MB |
| `@carbide-ui/avalonia-runner` | <1 KB | ≤ 100 KB |
| `@carbide-ui/launcher` | 6.3 KB | ≤ 50 KB |

## Known limitations (v1)

Documented per proposal §12 and plan §13:

- **One load per iframe lifetime.** `LaunchHandle.reload()` iframe-reboots. Full in-process `Application` swap is a v2 follow-up (proposal Q.3).
- **Avalonia user code runs interpreted.** Animations and high-frequency UI updates will not match AOT-compiled performance (plan UI-R7).
- **No user stdout forwarding.** `Console.WriteLine` in user code is visible only in the iframe's devtools console (proposal Q.4).
- **`appClass` is required.** Inference from `BuildResult.primaryAssemblyName + ".App"` is a v1.1 follow-up (proposal Q.2).
- **Single-threaded Avalonia.Browser.** COOP/COEP multithreading is UI-M8 (deferred).
- **WebGL context limits.** Browsers cap WebGL contexts (~16/tab); with >N iframes consider a Software2D rendering fallback (plan UI-R9). Not encountered at 3-iframe scale.

## Testing

```bash
# @carbide/core + browser-adapter: node corpus (pulls in core-P1/P2/P3 regressions)
cd ../Carbide/packages/core && npm run test:fast

# @carbide-ui/launcher: unit tests (Node fake-window harness)
cd packages/launcher && npm test

# @carbide-ui/launcher: browser end-to-end (requires Chromium; auto-starts static server)
cd packages/launcher && npm run test:browser

# Per-package size gate
node scripts/measure-sizes.mjs
```
