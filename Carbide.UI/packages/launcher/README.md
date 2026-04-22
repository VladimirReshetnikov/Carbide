# @carbide-ui/launcher

TypeScript orchestrator that bridges a Carbide `BuildResult` into an Avalonia.Browser runner iframe via `postMessage`. Implements the client half of the UI-M3 / plan §8 protocol.

## Install

```bash
npm install @carbide-ui/launcher @carbide-ui/avalonia-runtime-bundle
```

The launcher requires [`@carbide-ui/avalonia-runtime-bundle`](../runtime-bundle/README.md) at runtime because its default `runnerSrc` resolves to that package's `index.html` relative to the launcher's own package URL. Override via `LaunchOptions.runnerSrc` when serving from a custom URL (CDN, monorepo dev, etc.).

## Usage

```ts
import { CarbideSession } from "@carbide/core";
import { launchInIframe } from "@carbide-ui/launcher";

const session = await CarbideSession.initializeAsync({
    sideload: ["@carbide-ui/refs-avalonia"],
});
const project = session.createProject({ assemblyName: "MyApp" });
project.addSource("App.cs", /* C# source that defines MyApp.App */);
const build = await project.build();
if (!build.success) throw new Error("compile failed");

const iframe = document.createElement("iframe");
iframe.style.width  = "100%";
iframe.style.height = "100%";
document.getElementById("preview")!.append(iframe);

const handle = await launchInIframe(build, iframe, {
    appClass: "MyApp.App",
});

// Later, after recompile:
await handle.reload(await project.build());

// Teardown:
handle.dispose(/* removeIframe */ true);
```

## Protocol (plan §7.7, frozen at schema version 1)

Launcher → Runner:

```ts
{ type: "load", schemaVersion: 1, peBase64, pdbBase64, appClass, runArgs }
```

Runner → Launcher:

```ts
{ type: "runnerReady",   schemaVersion: 1 }
{ type: "runnerRunning", schemaVersion: 1 }
{ type: "runnerError",   schemaVersion: 1, message, kind: "load" | "runtime" | "teardown" }
```

- Exactly one `runnerReady` per iframe load; the launcher buffers any `load` send until it arrives.
- After `runnerRunning` the initial `launchInIframe` promise resolves. Later `runnerError` messages with `kind: "runtime"` invoke the caller's `onRuntimeError` callback.
- `reload()` reuses the iframe via `src = "about:blank"` followed by a fresh boot — v1 runner only handles one load per iframe lifetime (plan §7.3).
- Messages with an unknown `schemaVersion` are ignored by the launcher (surface symptom: boot times out with a self-descriptive error referencing the stale runner).

## API

```ts
export function launchInIframe(
    build: BuildResult,
    iframe: HTMLIFrameElement,
    options: LaunchOptions,
): Promise<LaunchHandle>;

export interface LaunchOptions {
    readonly appClass: string;
    readonly readyTimeoutMs?: number; // default 30_000
    readonly runnerSrc?: string;      // default: bundle's index.html
    onRuntimeError?(message: string): void;
}

export interface LaunchHandle {
    reload(build: BuildResult): Promise<void>;
    dispose(removeIframe?: boolean): void;
}
```

`BuildResult` is consumed as a **structural type** — the launcher has no runtime dependency on `@carbide/core` (plan UI-I9). If your own `BuildResult` shape matches the fields the launcher uses (`success`, `pe`, optionally `pdb`), it will work.

## Limitations (v1)

- **Single load per iframe.** In-place `Application` swap is a v2 follow-up (proposal §12 Q.3); reload re-boots the iframe.
- **No stdout forwarding.** User `Console.WriteLine` output does not reach the launcher. Deferred to protocol v2 (proposal §12 Q.4).
- **Inference of `appClass`.** Currently required. v1.1 will fall back to `BuildResult.primaryAssemblyName + ".App"` (proposal §12 Q.2).
