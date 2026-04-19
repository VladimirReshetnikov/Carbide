# Proposal: `Carbide.UI` / `@carbide-ui/*` — Avalonia GUI integration for Carbide

- Created (UTC): 2026-04-18T22:04:08Z
- Repository HEAD: 0b929aad1eef7e0307cede8e6fb6b4dd1468b1d3

Status: **proposal for a new sibling project family**. Takes positions on every major design decision; where a decision is ambiguous, picks one and states the alternative alongside with rationale. Re-opening any decision requires editing §12 (Open questions) rather than silently reshaping the design.

Audience: repository owner and future contributors. The design here is expected to be buildable from the §7–§9 specifications without further research.

Scope: the *what* and the *how* of an Avalonia-in-browser GUI story that sits next to Carbide. The *whether* is the open call this proposal exists to support — §13 lists the explicit decisions an owner needs to make before implementation starts.

Paired with the feasibility survey [`src/Carbide/docs/research/avalonia-ui/carbide-avalonia-browser-gui-integration__2026-04-18__21-52-50-185670__57c69d8c45e3.md`](../research/avalonia-ui/carbide-avalonia-browser-gui-integration__2026-04-18__21-52-50-185670__57c69d8c45e3.md). The feasibility report establishes that the integration is **technically possible** (via the [`AvaloniaUI/XamlPlayground`](https://github.com/AvaloniaUI/XamlPlayground) precedent and the shared `Microsoft.NET.Sdk.WebAssembly` foundation). This proposal establishes *which specific shape* the integration should take and how it should be delivered.

## 1. Elevator pitch

> **`Carbide.UI` is a sibling project family that lets Carbide callers compile and run Avalonia-shaped GUI C# programs in a browser tab.** The Carbide compiler stays unchanged; a small npm runner package boots a pre-built Avalonia.Browser host inside an iframe, accepts the PE Carbide emits via `postMessage`, and mounts the user's UI on a `<canvas>`. A companion CLI path produces an Avalonia-browser static-site bundle for deployment. Both paths share one Avalonia reference pack.

Two concrete user experiences fall out of this:

```ts
// Interactive: compile-and-run an Avalonia app in an iframe on the same page.
import { CarbideSession } from "@carbide/core";
import { launchInIframe } from "@carbide-ui/launcher";

const session = await CarbideSession.initializeAsync({
    sideload: ["@carbide-ui/refs-avalonia"],
});
const project = session.createProject({ appClass: "MyApp.App" });
project.addSource("App.cs", /* ... */);
project.addSource("MainView.cs", /* ... */);
const build = await project.build();
await launchInIframe(build, document.getElementById("preview"));
```

```bash
# Offline: produce a deployable Avalonia-browser bundle.
npx carbide build --target avalonia-browser \
    --project MyApp.csproj --out ./dist
python -m http.server --directory ./dist
# → http://localhost:8000 renders the Avalonia app.
```

Two deliverables, one reference pack, one runtime bundle. No new surface inside `@carbide/core`.

## 2. Relationship to Carbide's vision

Carbide's [vision document](../carbide-vision__2026-04-17__16-16-47-000000.md) §6 explicitly lists two non-goals that touch this proposal:

> **N.2 Not a .NET platform replacement.** Carbide does not ship ASP.NET Core, Blazor, EF Core, WPF, WinForms, MAUI, Avalonia, or any desktop/server framework. […]
>
> **N.3 Not a GUI platform.** Carbide runs console programs. Anything that opens a window, binds to a display server, or uses a UI framework is out of scope at the runtime layer.

This proposal **preserves both non-goals** by keeping Avalonia out of `@carbide/core` entirely. The integration is a **companion project family** under a distinct npm scope (`@carbide-ui/*`) and — if adopted — a separate source tree under `src/Carbide.UI/`. The companion family has its **own vision, own scope discipline, own size budget, own non-goals**. It consumes `@carbide/core` as a compile-only dependency.

**Vision amendment proposed.** Add to `carbide-vision.md` §13 a new paragraph naming companion projects:

> **Companion projects.** Named `@carbide-*` or `@carbide-<topic>/*` (not `@carbide/*`). Each has its own vision and non-goals. `@carbide/core`'s non-goals N.1–N.8 do not bind companions; companion projects cannot expand `@carbide/core`'s scope by the back door either. First companion: `@carbide-ui/*` for the Avalonia GUI story.

This is the only edit to Carbide's core docs that this proposal requires.

## 3. Scope of this proposal

In scope:

- An Avalonia reference pack (`@carbide-ui/refs-avalonia`) usable from `@carbide/core` today via `session.addReference`.
- A browser runner package (`@carbide-ui/avalonia-runner`) that hosts Avalonia.Browser in an iframe and accepts PE payloads via `postMessage`.
- A Carbide-side launcher (`@carbide-ui/launcher`) that bridges a Carbide `BuildResult` into the runner.
- A CLI subcommand (`carbide build --target avalonia-browser`) that produces a static-site bundle.
- A runtime XAML strategy based on `AvaloniaRuntimeXamlLoader.Parse` (works today; no source-generator dependency).
- A minimal collectible-`AssemblyLoadContext` change in Carbide core (§10.2), shippable independently.

Out of scope:

- Compile-time `.axaml` compilation via Avalonia's XAML source generator. Defers to Carbide M12 (Band C). Tracked as a follow-up in §12.
- Avalonia.Browser's multithreaded mode (COOP/COEP headers). Single-threaded is the v1 default.
- NativeAOT of the user program. User code runs interpreted; Avalonia framework code can be AOT-compiled.
- Non-Avalonia GUI stacks (WinForms, WPF, MAUI, Uno, Blazor). Named out of scope; the `@carbide-ui/*` umbrella could theoretically host more frameworks later, but this proposal is Avalonia-only.
- A hosted playground site. The packages are libraries; anyone can build a playground on top.
- Persistent storage, file dialogs, or other browser-gesture features. Users get what Avalonia.Browser provides.

Non-goals (hard, for this proposal):

- **G.1 Do not modify `@carbide/core`'s public surface** beyond additive, GUI-neutral improvements (collectible `AssemblyLoadContext`).
- **G.2 Do not exceed the combined size budget** of 80 MB compressed (Carbide core at ≤ 40 MB plus Avalonia runner at ≤ 40 MB). Each half carries an independent gate.
- **G.3 Do not couple Carbide's release cadence to Avalonia's.** The reference pack is triple-pinned `(Carbide, Avalonia, .NET)`; Avalonia upgrades are companion-side PRs that do not touch `@carbide/core`.
- **G.4 Do not ship a "works for all Avalonia apps" claim.** The support matrix is explicit: what works, what needs inline-string XAML, what needs M12. Documented per release.

## 4. Approaches considered

Three architectures are viable. The choice between them turns on where Carbide and Avalonia runtimes sit relative to each other in memory and process topology.

### 4.1 Approach A — Merged runtime (in-process, single WASM instance)

One npm package (`@carbide-ui/avalonia-host`) ships a WebAssembly bundle that contains Carbide.Core **and** Avalonia.Browser as a single `_framework/`. Consumers import `CarbideAvaloniaSession` instead of `CarbideSession`. The hosting HTML has a `<div id="out">`. User code is compiled by Carbide's Roslyn and executed in the same .NET runtime that Avalonia is already booted in. The user's entry point calls `BuildAvaloniaApp().StartBrowserAppAsync("out")` and mounts its canvas on the pre-existing `<div>`.

**Technical feasibility.** Proven by [XamlPlayground](https://github.com/AvaloniaUI/XamlPlayground), which does exactly this pattern (Roslyn + Avalonia.Browser + `AssemblyLoadContext.LoadFromStream`). [`src/XamlPlayground/Services/CompilerService.cs`](https://github.com/AvaloniaUI/XamlPlayground/blob/main/src/XamlPlayground/Services/CompilerService.cs) is the reference implementation.

**Pros.**
- Single WASM cold-start. Users pay one download, not two.
- Lower peak memory (one .NET runtime instance, not two).
- Carbide's compiler APIs are directly callable from inside the running Avalonia app (interesting for in-app editors with live diagnostics).
- Simpler debugging (one runtime, one call stack).

**Cons.**
- **Bundle size**: ~50–60 MB compressed, ~180–220 MB uncompressed. Above Carbide's P.8 budget (40/120 MB). Forces the companion to carry its own budget rather than Carbide's.
- **Scope creep risk**: "since Avalonia is in the runtime anyway, can we also ship X?" becomes an evergreen pressure. Vision discipline is harder when size is already blown.
- **Crash radius**: a misbehaving user program (infinite loop, heap exhaustion) takes down the compiler too. No way to reset the compiler without reloading the page.
- **Coupling**: Avalonia version bumps force `@carbide-ui/avalonia-host` rebuilds that touch the merged runtime. The triple-pinning discipline is harder to keep airtight when the packages share a `_framework/`.

**Cost.** ~2 weeks of engineering. Most of it is build-pipeline plumbing for the merged `_framework/`.

### 4.2 Approach B — Cross-frame runner (Carbide compiles, iframe Avalonia runs) — **proposed**

Two independent npm packages:

- `@carbide-ui/avalonia-runner` — a WebAssembly bundle carrying Avalonia.Browser + the .NET runtime and **nothing of Carbide**. Its `Program.cs` is a bootstrapper that listens on `window.addEventListener("message", …)` for `{ type: "load", pe, pdb? }` payloads, compiles-free loads them via a collectible `AssemblyLoadContext`, and starts the user's Avalonia `App` class.
- `@carbide-ui/launcher` — a TypeScript helper that takes a Carbide `BuildResult` plus a DOM `HTMLIFrameElement` (whose `src` points to the runner's `index.html`), posts the PE to it, and resolves when the runner reports "UI alive".

Carbide itself is unchanged. Two runtimes coexist on the page: Carbide's (compiles) and the runner's (runs). Handoff is JSON `postMessage` with base64 PE.

**Pros.**
- **Respects Carbide core's vision.** No Avalonia bits inside `@carbide/core`. Zero extra size for Carbide-only users.
- **Crash isolation.** If the user's Avalonia code hangs or throws, the iframe is the blast radius. Carbide's session survives. Teardown is `iframe.src = iframe.src` (or remove + re-create).
- **Independent release cadence.** Avalonia upgrades, .NET runtime upgrades, and Carbide core upgrades move on their own timelines. The triple-pin lives entirely in the runner's package metadata.
- **Publishable playgrounds**: multiple previews on one page = multiple iframes.
- **Publisher ergonomics.** The runner's `_framework/` is also usable by the CLI path (Approach C), making one runtime bundle serve both interactive and offline use cases.

**Cons.**
- **Two cold starts.** Cold-loading a page that wants both compiles and runs costs ~30 MB for Carbide + ~30 MB for the runner. Mitigation: HTTP caching separates them; typical interactive users pay both once.
- **Two runtime memory footprints.** Two .NET heaps, two sets of BCL assemblies. On memory-constrained devices this is a real cost; on desktop browsers it's ~200 MB total, within tolerance.
- **`postMessage` copy.** PE bytes transit the frame boundary as a base64 string in a JSON envelope. For a typical 100 KB user assembly this is imperceptible.
- **Coordination complexity.** The protocol must handle boot races (Carbide tries to send before runner is ready), teardown races (new run requested while old one is still starting), and error propagation (runtime errors in the iframe must surface back to the launcher).

**Cost.** ~1.5–2 weeks of engineering. Most of it is the runner project (a small Avalonia.Browser app whose `App` class is a dynamic loader) and the `postMessage` protocol.

### 4.3 Approach C — Offline build CLI (no in-browser execution) — **proposed as concurrent delivery**

`@carbide/cli` gains a `--target avalonia-browser` flag. The CLI takes user C# (or a `.csproj` via M5's `msbuild-lite`), compiles via `@carbide/core` as it does today, and assembles a static-site bundle in `--out <dir>` that contains:

- `_framework/` — the pinned `@carbide-ui/avalonia-runtime-bundle`'s runtime contents.
- `MyApp.dll` — the user's compiled PE.
- `main.js` — a tiny JS bootstrap (~30 lines, the same shape as Avalonia's template `main.js`).
- `index.html` — Avalonia's `<div id="out">` shell.

No browser execution happens inside Carbide; the output is deployable to any static host. "Run" means "`python -m http.server --directory ./dist`" (or GitHub Pages, or Netlify, etc.).

**Pros.**
- **Lowest engineering cost.** All Node-side; no new browser runtime work. ~1 week.
- **Produces a deployable artifact** that survives Carbide itself. Users can ship their Avalonia app to static hosting.
- **Shares the runtime bundle with Approach B.** Same `_framework/` serves both interactive and offline paths; the only difference is whether the HTML shell has an inline launcher or a static user-PE reference.

**Cons.**
- **Doesn't satisfy "running in the browser with Carbide"**: the run happens on whatever host serves the static files, not inside Carbide's workflow.
- **No iteration loop.** Edit → build → deploy → reload is slower than edit → run.

**Cost.** ~1 week.

### 4.4 Comparison table

| Dimension | A (Merged) | **B (Cross-frame)** | C (Offline CLI) |
|---|---|---|---|
| Cold-start cost per use | 1× ~50 MB | 2× ~30 MB | 0 (offline build) |
| Runtime memory peak | ~150 MB | ~250 MB | 0 in Carbide |
| Alignment with Carbide N.2/N.3 | Risky | **Clean** | Clean |
| Engineering effort | ~2 weeks | **~1.5–2 weeks** | ~1 week |
| Interactive in-browser run | Yes | **Yes** | No |
| Deployable static artifact | No | No | **Yes** |
| Can run multiple previews on one page | 1 (one runtime) | **N (N iframes)** | N/A |
| Crash isolation | No | **Yes** | N/A |
| Avalonia version upgrade complexity | Medium | **Low** | Low |
| Compiler + GUI cross-talk (e.g. in-app Roslyn diagnostics for an embedded editor) | **Easy** (same runtime) | Medium (via JSON-RPC) | N/A |

### 4.5 Alternatives considered and rejected

- **Worker-based runner.** Running Avalonia in a Web Worker would save the iframe overhead. Rejected because Avalonia renders through `<canvas>` on the main thread and uses `requestAnimationFrame` for layout; `OffscreenCanvas` is not a complete substitute in Avalonia's current browser backend.
- **Blazor WebAssembly as the host.** Wrapping Carbide + Avalonia inside a Blazor Server/WASM app. Rejected because it adds a transitive dependency on Blazor without serving any of the request's goals.
- **Server-hosted Avalonia** (run the Avalonia compile + execute on a server, stream to a browser). Rejected because it violates Carbide's no-SDK-on-host constraint and inverts the whole no-daemon architecture.
- **A single merged bundle with hot-swap** (Approach A plus `AssemblyLoadContext.Unload()` on every user edit). Rejected for crash isolation and scope reasons — Approach B gets the same iteration loop via iframe reload, without merging runtimes.

## 5. Recommendation

**Ship Approach B (Cross-frame runner) as the primary interactive story, with Approach C (Offline build CLI) as a concurrent deliverable that shares the reference pack and runtime bundle. Do not pursue Approach A.**

Rationale:

1. **Approach B keeps the letter and spirit of Carbide's N.2/N.3 non-goals.** No Avalonia artefacts inside `@carbide/core`; no vision amendment beyond adding the "companion projects" clause.
2. **Approach B is only marginally more expensive than Approach A** (1.5–2 weeks vs 2 weeks) but buys **crash isolation, independent release cadence, multi-preview capability, and clean size accounting**.
3. **Approach C piggybacks on Approach B's work** (shared `_framework/`, shared reference pack). It costs < 1 extra week on top of B and delivers the "publishable artefact" use case that Approach B does not.
4. **Approach A's one genuine advantage** — in-process compiler–GUI cross-talk — is not a v1 requirement. If it becomes one, `@carbide-ui/avalonia-host` can be added later as a third sibling package; nothing in B or C precludes it.
5. **The combined B+C bundle**, sized ~40 MB compressed for the runner (Approach C's `_framework/` **is** Approach B's runner's `_framework/`), fits the proposed companion-family budget without Carbide's budget taking the hit.

## 6. High-level architecture

```text
 ┌───────────────────────────────────────────────────────────────────┐
 │  Consumer page (user's HTML + JS)                                 │
 │                                                                   │
 │  ┌─────────────────────────────┐  postMessage   ┌──────────────┐  │
 │  │  @carbide/core (compile)    │ ─────────────► │  iframe      │  │
 │  │                             │                │              │  │
 │  │  CarbideSession             │                │  runner boot │  │
 │  │    + session.addReference() │                │   .NET WASM  │  │
 │  │    + session.createProject()│                │   + Avalonia │  │
 │  │    + project.build()        │ ◄───────────── │   + postMsg  │  │
 │  │                             │  status events │   listener   │  │
 │  │                             │                │              │  │
 │  │  @carbide-ui/launcher       │                │  @carbide-ui │  │
 │  │    launchInIframe(build)    │                │  /runner     │  │
 │  │                             │                │              │  │
 │  └─────────────────────────────┘                └──────────────┘  │
 │                                                                   │
 │    ref-pack auto-loaded:                                          │
 │      @carbide-ui/refs-avalonia (bytes for Roslyn)                 │
 │                                                                   │
 └───────────────────────────────────────────────────────────────────┘

   Separately (Approach C):

 ┌───────────────────────────────────────────────────────────────────┐
 │  Offline CLI:                                                     │
 │    npx carbide build --target avalonia-browser ...                │
 │      → dist/index.html, main.js, MyApp.dll, _framework/           │
 │    Statically host dist/ anywhere.                                │
 │    dist/_framework/ = @carbide-ui/avalonia-runtime-bundle         │
 │                      (the same bundle the runner ships)           │
 └───────────────────────────────────────────────────────────────────┘
```

## 7. Package layout

### 7.1 `@carbide-ui/refs-avalonia`

**Purpose.** Compile-time reference-assembly set for Avalonia. Analogous to `@carbide/refs-net10.0` but for the Avalonia API surface.

**Contents.** Extracted from the pinned `Avalonia.Browser.<version>.nupkg` and its transitive deps. The `ref/net10.0-browser/` or `ref/net10.0/` folders of each package yield reference DLLs; runtime DLLs are excluded. Typical list: `Avalonia.Base.dll`, `Avalonia.Controls.dll`, `Avalonia.Layout.dll`, `Avalonia.Markup.dll`, `Avalonia.Markup.Xaml.dll`, `Avalonia.Markup.Xaml.Loader.dll`, `Avalonia.Browser.dll`, `Avalonia.Themes.Fluent.dll`, plus SkiaSharp refs.

**Shape.** Mirrors `@carbide/refs-net10.0`: a `scripts/build.mjs` that downloads the `.nupkg`s from `api.nuget.org`, extracts `ref/` contents, lays them out in a known tree, and exports a manifest (`refpack.json` listing DLL names + sizes + SHA256).

**Consumption.** At session init:

```ts
const session = await CarbideSession.initializeAsync({
    sideload: ["@carbide-ui/refs-avalonia"],
});
```

This causes `@carbide/core`'s session to auto-call `session.addReference(bytes, name)` for every DLL in the ref-pack's manifest. The `sideload` option needs a small addition to `@carbide/core`'s `CarbideOptions` (details in §10.1).

**Versioning.** Triple-pin `(@carbide-ui/refs-avalonia X.Y.Z, Avalonia A.B.C, .NET N.M)`. The version of `@carbide-ui/refs-avalonia` is the authority; patch bumps of Avalonia produce patch bumps of the ref-pack.

### 7.2 `@carbide-ui/avalonia-runtime-bundle`

**Purpose.** The pre-built Avalonia.Browser `_framework/` directory, installable as a dependency.

**Contents.** The output of `dotnet publish -c Release` on a tiny internal Avalonia.Browser project that:

- Targets `net10.0-browser`, uses `Microsoft.NET.Sdk.WebAssembly`.
- References `Avalonia.Browser` at the pinned version.
- Exposes a `ProgramDispatcher` C# class (details in §7.3) that implements the `postMessage` protocol.

The bundle is the `AppBundle/_framework/` directory, compressed via Brotli and gzip.

**Shape.** One tarball. Unpacked, it lives at `node_modules/@carbide-ui/avalonia-runtime-bundle/_framework/`. Both the runner and the offline CLI reference this tree.

**Independence.** This package has **zero** TypeScript. It is just bytes. Versioning follows the ref-pack.

### 7.3 `@carbide-ui/avalonia-runner`

**Purpose.** A browser-ready Avalonia.Browser app that boots into a waiting state and loads user PEs on demand via `postMessage`.

**Contents.**

- `index.html` — has `<div id="out">` and `<script type="module" src="./main.js">`.
- `main.js` — standard Avalonia `main.js` (imports `./_framework/dotnet.js`, runs `dotnetRuntime.runMain(…)`).
- `_framework/` — symlinked (or copied) from `@carbide-ui/avalonia-runtime-bundle`.
- A tiny loader C# class compiled into the runner's main assembly:

```csharp
// src/Carbide.UI/src/runner/RunnerProgram.cs (sketch)
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Loader;
using Avalonia;
using Avalonia.Browser;
using Avalonia.Controls.ApplicationLifetimes;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("browser")]

public partial class RunnerProgram
{
    private static AssemblyLoadContext? s_currentContext;
    private static Avalonia.Application? s_currentApp;

    public static Task Main(string[] _) => InitAsync();

    private static async Task InitAsync()
    {
        await JSHost.ImportAsync("runner-bridge", "./runner-bridge.js");
        // Runner is now ready to receive 'load' messages.
        PostReady();
        // Avalonia is NOT started yet; first 'load' message starts it.
        // We keep Main alive by awaiting a TaskCompletionSource that never completes.
        await new TaskCompletionSource().Task;
    }

    [JSExport]
    public static void OnLoadMessage(string peBase64, string? pdbBase64, string appClassName)
    {
        var peBytes = Convert.FromBase64String(peBase64);
        var pdbBytes = pdbBase64 is null ? null : Convert.FromBase64String(pdbBase64);

        // Tear down previous run if any.
        if (s_currentApp is not null && s_currentApp.ApplicationLifetime is
            ISingleViewApplicationLifetime svl)
        {
            svl.MainView = null;                       // release the visual tree
        }
        s_currentContext?.Unload();                    // drop the previous PE
        s_currentContext = new AssemblyLoadContext(name: Guid.NewGuid().ToString("N"),
                                                    isCollectible: true);

        var userAssembly = pdbBytes is null
            ? s_currentContext.LoadFromStream(new MemoryStream(peBytes))
            : s_currentContext.LoadFromStream(new MemoryStream(peBytes), new MemoryStream(pdbBytes));
        var appType = userAssembly.GetType(appClassName, throwOnError: true)!;

        var builder = AppBuilder.Configure(() => (Application)Activator.CreateInstance(appType)!);

        if (s_currentApp is null)
        {
            // First run: actually start Avalonia on the "out" element.
            builder.WithInterFont()
                   .StartBrowserAppAsync("out");
            s_currentApp = Application.Current;
        }
        else
        {
            // Subsequent run: replace MainView inside the already-started lifetime.
            var newApp = (Application)Activator.CreateInstance(appType)!;
            if (s_currentApp.ApplicationLifetime is ISingleViewApplicationLifetime svl)
            {
                // Swap the MainView. Full App swap across runs is deferred to v2.
                svl.MainView = (newApp as ISingleViewApplicationLifetime)?.MainView;
            }
        }
        PostRunning();
    }

    [JSImport("postReady", "runner-bridge")] private static partial void PostReady();
    [JSImport("postRunning", "runner-bridge")] private static partial void PostRunning();
    [JSImport("postError", "runner-bridge")] private static partial void PostError(string message);
}
```

`runner-bridge.js`:

```js
export function postReady()           { parent.postMessage({ type: "runnerReady"   }, "*"); }
export function postRunning()         { parent.postMessage({ type: "runnerRunning" }, "*"); }
export function postError(message)    { parent.postMessage({ type: "runnerError", message }, "*"); }

globalThis.addEventListener("message", (ev) => {
    if (ev.data?.type !== "load") return;
    // Call back into .NET via the runner's exposed JSExport.
    globalThis.__carbideRunnerInterop?.OnLoadMessage(ev.data.peBase64, ev.data.pdbBase64 ?? null,
                                                     ev.data.appClassName);
});
```

The runner's complete cold path: `main.js` → `dotnet.runMain(RunnerProgram.Main)` → `InitAsync` imports `runner-bridge.js` → `postReady()` fires → parent frame's launcher hears `runnerReady` and starts sending `load` messages.

**Size target.** ≤ 40 MB compressed (ref-pack + runtime bundle).

### 7.4 `@carbide-ui/launcher`

**Purpose.** Carbide-side TypeScript helper that orchestrates the handoff.

**API.**

```ts
// @carbide-ui/launcher/src/index.ts

import type { BuildResult } from "@carbide/core";

export interface LaunchOptions {
    /** The Avalonia Application-derived type to instantiate in the runner.
     *  Fully-qualified name, e.g. "MyApp.App". */
    appClass: string;
    /** How long to wait for the runner to report "ready" before rejecting. Default 30s. */
    readyTimeoutMs?: number;
    /** Called when the runner reports a runtime error inside the user program. */
    onRuntimeError?(message: string): void;
}

export interface LaunchHandle {
    /** Update the running UI with a new build. Safe to call repeatedly. */
    reload(build: BuildResult): Promise<void>;
    /** Tear down the runner and (optionally) remove the iframe from the DOM. */
    dispose(removeIframe?: boolean): void;
}

export async function launchInIframe(
    build: BuildResult,
    iframe: HTMLIFrameElement,
    options: LaunchOptions,
): Promise<LaunchHandle>;
```

`launchInIframe` sets `iframe.src` to the packaged runner's `index.html` (resolved from the launcher's own package base URL), waits for the `runnerReady` message, then posts the `load` payload. Returns a `LaunchHandle` whose `reload` posts again with a new `BuildResult`.

**No coupling to `@carbide/core`.** The launcher depends only on the `BuildResult` shape, which is a published TypeScript interface. It does **not** import or re-export any `@carbide/core` runtime.

### 7.5 `@carbide/cli` extension

Adds `--target avalonia-browser` to the existing `carbide build` subcommand. When set:

1. Resolve `@carbide-ui/avalonia-runtime-bundle` (must be installed).
2. Resolve `@carbide-ui/refs-avalonia` and feed it to `session.addReference` for the compile pass.
3. Require `--app-class <fully-qualified-name>` (or infer from the user project if unambiguous).
4. After compile, write `--out <dir>/` with:
   - `index.html` (shell from `@carbide-ui/avalonia-runtime-bundle/templates/`)
   - `main.js` (likewise)
   - `MyApp.dll` (the user PE)
   - `_framework/` (copied from the bundle)
   - `MyApp.pdb` (if emitted)

No browser runs. Output is deployable. Optional `--zip` flag produces a `.zip` for web upload.

### 7.6 Source tree layout (proposed)

If Vladimir accepts this proposal, the work lives in a new directory:

```
src/Carbide.UI/
  Directory.Build.props
  Directory.Build.targets
  README.md
  docs/
    carbide-ui-avalonia-integration-proposal__<timestamp>__<suffix>.md  [← this doc, copied here on move]
    drift/
      README.md
  packages/
    refs-avalonia/          # @carbide-ui/refs-avalonia
      package.json
      scripts/build.mjs
      README.md
    runtime-bundle/         # @carbide-ui/avalonia-runtime-bundle
      package.json
      build.mjs             # builds via: dotnet publish ../runner-dotnet/
      README.md
    runner-dotnet/          # C# runner project (compiled into the bundle)
      RunnerProgram.cs
      Avalonia.UI.Runner.csproj
      wwwroot/
        index.html
        main.js
        runner-bridge.js
    runner/                 # @carbide-ui/avalonia-runner
      package.json
      index.html            # symlink or thin wrapper over runtime-bundle/
      README.md
    launcher/               # @carbide-ui/launcher
      src/index.ts
      test/
      package.json
      README.md
```

## 8. Runner ↔ Launcher protocol

Stable across v1. Additions are allowed; existing fields are not renamed or repurposed.

```text
(Launcher → Runner)

  { type: "load",
    schemaVersion: 1,
    peBase64: "<base64>",
    pdbBase64: "<base64>" | null,
    appClass: "MyApp.App",
    runArgs: string[] | null }

(Runner → Launcher)

  { type: "runnerReady",   schemaVersion: 1 }           // after boot, once per iframe
  { type: "runnerRunning", schemaVersion: 1 }           // after user App is instantiated
  { type: "runnerError",   schemaVersion: 1,
    message: string,
    kind: "load" | "runtime" | "teardown" }
```

**Ordering rules.**

- The runner posts `runnerReady` exactly once per iframe load. The launcher must not send `load` before seeing it.
- After `runnerReady`, `load` messages may be sent at any time. Each `load` may be answered with `runnerRunning` (success) or `runnerError` (any failure during load, teardown, or initial `App` instantiation).
- Runtime errors that happen *after* the user program is running (e.g. uncaught exceptions in event handlers) are reported as `runnerError { kind: "runtime" }` but do not invalidate the iframe.
- The launcher may send a second `load` while the previous run is still active; the runner tears down (see §7.3 `OnLoadMessage`) and starts the new one.

**No streaming I/O in v1.** User `Console.WriteLine` output is not forwarded back to the launcher. If this becomes needed, add a `{ type: "stdout", text: string }` message in a later schema version.

**No request IDs in v1.** Single in-flight `load` per iframe. If concurrency becomes needed, add `{ requestId: number }` fields symmetrically.

## 9. XAML handling strategy

This is the most consequential technical choice the proposal pins down.

### 9.1 Supported paths at v1

- **XAML-in-strings via `AvaloniaRuntimeXamlLoader.Parse`.** The user's C# code contains XAML as string literals; calls like `AvaloniaRuntimeXamlLoader.Parse<UserControl>(xamlText, parentAssembly: userAssembly)` construct the object tree at runtime. No build-time XAML processing. Requires `Avalonia.Markup.Xaml.Loader` as a reference (included in the ref-pack). This is what XamlPlayground does.
- **`.axaml` source files as Carbide documents, runtime-loaded.** The user calls `project.addSource("MainView.axaml", xamlText)`. Carbide detects the `.axaml` extension and, at compile time, generates a hidden companion `.cs` that packages the XAML as a resource string, plus a partial class that wires `InitializeComponent()` to call `AvaloniaRuntimeXamlLoader.Parse<T>(xamlString, parentAssembly: userAssembly)`. This is a Carbide-side convenience over the previous path; no Avalonia changes required. Implementable in v1.

### 9.2 Deferred to Carbide M12

- **Build-time XAML compilation via Avalonia's source generator (`Avalonia.Markup.Xaml.Generator` / `XamlX`).** This is what real-world Avalonia apps use today. It requires Carbide to support source generators (M12, Band C stretch) *and* to pass `.axaml` files as `AdditionalFiles` to the generator. Two upstream dependencies.

  Until M12, users of `@carbide-ui/*` write XAML as strings or accept the runtime-parse cost (the latter is small — parse cost is microseconds for typical views).

### 9.3 XAML reference documentation plan

Document the XAML strategy prominently in `@carbide-ui/refs-avalonia`'s README. Provide three samples in the companion repo:

1. `samples/hello-code-only/` — no XAML, pure C# UI construction. Smallest.
2. `samples/hello-runtime-xaml-string/` — C# with a XAML string literal.
3. `samples/hello-runtime-xaml-axaml-file/` — `.axaml` companion file, runtime-parsed at `InitializeComponent()`.

Each sample has a `README.md` stating which ambition tier it exercises and what does not yet work.

## 10. Required changes in Carbide core

Three additive, non-breaking changes. All three are independently useful to Carbide and not GUI-specific.

### 10.1 `CarbideOptions.sideload`

New optional field. `sideload?: string[]` — an array of npm package names whose `refpack.json` manifests are auto-resolved at session init and fed to `session.addReference(bytes, name)`.

```ts
const session = await CarbideSession.initializeAsync({
    sideload: ["@carbide-ui/refs-avalonia"],
});
```

Implementation: a small host-adapter extension that, given a package name, reads `node_modules/<name>/refpack.json` (Node) or fetches `{packageUrl}/refpack.json` (browser, requires a CDN-hosted ref-pack). Bytes are loaded from the listed DLLs and passed through the existing `addReference` flow. Handles are tracked in the session and torn down on shutdown.

**Non-breaking.** Default is no sideload. Existing code is unaffected.

### 10.2 Collectible `AssemblyLoadContext` in `ProjectCompiler.RunAsync`

Current code in [`ProjectCompiler.cs:371`](../../packages/core/src/Services/ProjectCompiler.cs) does `Assembly.Load(byte[])`, which lands in the default (non-collectible) `AssemblyLoadContext`. Change it to:

```csharp
var context = new AssemblyLoadContext(
    name: $"CarbideRun-{Guid.NewGuid():N}",
    isCollectible: true);
var assembly = context.LoadFromStream(new MemoryStream(peBytes));
// ... after run finishes, or on exception path:
context.Unload();
```

Requires a small refactor: the current `AssemblyResolve` handler for attached references must be registered on the `AssemblyLoadContext.Resolving` event instead of the legacy `AppDomain.AssemblyResolve`, so resolution is scoped.

**Non-breaking.** `Assembly.Load(byte[])` → `context.LoadFromStream(…)` is observationally equivalent for Carbide's existing console-program use cases. The change removes the accumulation-across-runs leak and is the prerequisite for GUI reuse (§8 runner teardown depends on it).

**Shipping independently.** This can land as a standalone PR ahead of any `Carbide.UI` work and is valuable regardless.

### 10.3 `BuildResult` carries `peSchemaVersion` and `primaryAssemblyName`

Two new optional fields on `BuildResult`, harmless for existing callers:

```ts
export interface BuildResult {
    // existing fields...
    peSchemaVersion?: number;           // 1 in v1
    primaryAssemblyName?: string;       // e.g. "MyApp"
}
```

The launcher uses `primaryAssemblyName` as a fallback for `appClass` when the user doesn't specify one (by appending `.App`). The schema version guards the launcher against future `BuildResult` shape changes.

**Non-breaking.** Both fields are optional.

## 11. Milestones

The companion project has its own milestone track, starting at `UI-M1`. Each milestone has a green-gate acceptance test and is shippable in isolation.

### UI-M0 — Skeleton & build pipeline

Create `src/Carbide.UI/` with `Directory.Build.props`, `Directory.Build.targets`, empty package skeletons for the four npm packages. Add CI jobs that build and publish-candidate each one.

**Acceptance.** `npm pack` produces four valid tarballs. CI produces build logs with measured sizes.

### UI-M1 — Reference pack

Ship `@carbide-ui/refs-avalonia`. Mirror `@carbide/refs-net10.0`'s build script shape. Sources: pinned Avalonia `.nupkg` files from `api.nuget.org`, extracted under `refpack/ref/`. Manifest `refpack.json` lists each DLL + SHA256.

**Acceptance.** From a fresh environment:

```bash
cd src/Carbide.UI/packages/refs-avalonia
node scripts/build.mjs
# produces refpack/ with ~15 DLLs
```

From a Carbide consumer, adding the ref-pack must enable compile of a trivial Avalonia-referencing program:

```ts
const session = await CarbideSession.initializeAsync({
    sideload: ["@carbide-ui/refs-avalonia"],
});
const project = session.createProject();
project.addSource("App.cs", `
    using Avalonia;
    public class App : Application { public override void Initialize() {} }`);
const result = await project.build();
// result.success === true, result.pe !== undefined
```

Ref-pack size ≤ 5 MB uncompressed.

### UI-M2 — Runtime bundle

Ship `@carbide-ui/avalonia-runtime-bundle`. Internal `runner-dotnet/Avalonia.UI.Runner.csproj` builds with `dotnet publish -c Release`; the output `AppBundle/_framework/` becomes the bundle's contents. The runner's `RunnerProgram` is just enough to boot Avalonia and expose the `postMessage` bridge.

**Acceptance.** Running the runner's `index.html` directly in a browser shows the Avalonia splash (no user PE yet); opening DevTools shows a `runnerReady` message posted to `window.parent`.

Bundle size ≤ 35 MB compressed.

### UI-M3 — Launcher & in-browser compile + run

Ship `@carbide-ui/avalonia-runner` (HTML/JS wrapper) and `@carbide-ui/launcher` (TS API). Wire `launchInIframe` end-to-end.

Carbide core gets UI-M3-required additions: `CarbideOptions.sideload` and collectible `AssemblyLoadContext` in `RunAsync`.

**Acceptance.** From a fresh Node environment:

```bash
npm init -y
npm install @carbide/core @carbide-ui/refs-avalonia @carbide-ui/avalonia-runner @carbide-ui/launcher
# Run a test HTML via Playwright:
npx playwright install chromium
node --experimental-vm-modules tests/hello-avalonia.mjs
```

`tests/hello-avalonia.mjs` writes a minimal Avalonia app via Carbide, launches into an iframe, asserts that the canvas renders non-background pixels (via Playwright screenshot comparison against a golden).

### UI-M4 — Runtime XAML support

Add Carbide-side convenience: `project.addSource("*.axaml", xamlText)` auto-generates a C# companion that runtime-parses the XAML at `InitializeComponent()` via `AvaloniaRuntimeXamlLoader`. Update the reference documentation with the three samples from §9.3.

**Acceptance.** The `samples/hello-runtime-xaml-axaml-file/` fixture builds and renders. The XAML text is loaded and rendered; the running UI matches the golden screenshot.

### UI-M5 — Offline CLI (`--target avalonia-browser`)

Extend `@carbide/cli`. Same ref-pack and runtime bundle are consumed via Node's `require.resolve` on the installed packages. Write shell HTML/JS and the compiled PE into `--out`.

**Acceptance.** `npx carbide build --target avalonia-browser --source App.cs --app-class App --out ./dist` produces a directory that, served via `python -m http.server`, renders the Avalonia app in a fresh browser.

### UI-M6 — Multiple-preview polish & production-hardening

Support multiple iframes on a page, each its own runner. Ship API docs, comprehensive error messages (`runnerError { kind }` populated), and an example React/Vue/Svelte integration.

**Acceptance.** `samples/multi-preview/` page embeds four iframes, each showing a different Avalonia app, all driven from one Carbide session. `npm run test:browser` passes on Chromium and Firefox.

### UI-M7 (deferred) — XAML source generator

Waits for Carbide M12 (source-generator subset, Band C). Pull Avalonia's XAML source generator through Carbide's generator driver. Real-world Avalonia apps with `.axaml` companion files build at compile time (no runtime parse cost).

### UI-M8 (deferred) — Multithreaded Avalonia

Document COOP/COEP requirements. Provide a pre-built HTTP server (`@carbide-ui/serve`) that sets the required response headers for local testing.

## 12. Open questions

Called out explicitly so decisions are visible and re-openable.

- **Q.1 npm namespace.** `@carbide-ui/*` is the working name. If `@carbide` is taken (vision §12 R9), the companion family will follow whatever renamed scope Carbide chooses. No hyphen/no-scope considered; keep them distinct.
- **Q.2 App-class discovery.** `LaunchOptions.appClass` is required in v1. Alternatives: attribute (`[assembly: CarbideAvaloniaApp(typeof(MyApp))]`) scanned reflectively, or inference from `AssemblyName + ".App"`. **Position:** required in v1; infer as a v1.1 addition once the common convention is known.
- **Q.3 Teardown semantics across runs.** v1 swaps `MainView` on re-run, reusing the Avalonia `Application`. This avoids the full `AppBuilder` teardown cost but means static state in the previous user `App` lingers. **Position:** v1 documents this; v2 supports full `Application` swap.
- **Q.4 `Console.WriteLine` forwarding.** v1 does not forward user stdout to the launcher. **Position:** add `{ type: "stdout", text: string }` in v2 if users ask; the runner can already intercept via `Console.SetOut`.
- **Q.5 Interactive input devices.** Clipboard, file dialogs, geolocation, etc. all work through Avalonia's normal browser APIs in the runner iframe. No extra wiring needed. Subject to browser gesture requirements. Documented.
- **Q.6 XAML without Carbide M12.** v1 supports runtime XAML. Users who cannot tolerate the (microsecond-scale) parse cost must wait for UI-M7. **Position:** document prominently; pitch v1 as the 95% solution.
- **Q.7 Ref-pack delivery.** Initially published via npm (`@carbide-ui/refs-avalonia`). A CDN + IndexedDB path is a future option if npm install cost becomes problematic.

## 13. Decisions the owner needs to make before UI-M0 starts

This proposal is ready to drive implementation, but four calls belong to the owner:

1. **Approve the companion-project concept.** Add the "Companion projects" paragraph to Carbide's vision §13 (see §2 above). If rejected, the proposal stops here.
2. **Pick a target Avalonia version.** 12.x is the public stable; 13.0 (if released by implementation time) may be preferred. Determines the initial ref-pack contents.
3. **Pick a target .NET version.** `net10.0-browser` to match Carbide core. No reason to diverge.
4. **Confirm the npm scope (`@carbide-ui` vs alternative).** Subject to final naming (Q.1).

Items 2–4 are short decisions. Item 1 is the material one.

## 14. Risks and mitigations

| # | Risk | Likelihood | Impact | Mitigation |
|---|------|-----------|-------:|-----------|
| UI-R1 | Avalonia upstream breaks something the runner depends on (e.g. `ISingleViewApplicationLifetime.MainView` swap semantics). | Medium | Medium | Pin Avalonia version; CI job that rebuilds the bundle against next Avalonia minor and runs the sample suite; drift report. |
| UI-R2 | The iframe's `<canvas>` has unexpected DPI / resize behavior inside some host pages. | Medium | Low | Document the requirement that the iframe has a non-zero size before the first `load`; runner posts `runnerError { kind: "load", message }` if the canvas cannot initialize. |
| UI-R3 | Users try to compile a real-world `.axaml`-heavy Avalonia app and hit the source-generator gap. | High | Medium | UI-M4 provides runtime-XAML; UI-M7 is the eventual fix; the support matrix documents this clearly per sample. |
| UI-R4 | Carbide's collectible-ALC change regresses an existing console-program behavior. | Low | High | Ship §10.2 as a standalone Carbide PR ahead of UI-M3; run the full golden corpus before merging. |
| UI-R5 | Combined bundle size (Carbide + runner + ref-pack) exceeds the 80 MB combined budget. | Medium | Medium | Size gates in CI per package; Brotli for static hosting documented; consider CDN fetch for runtime bundle in v2. |
| UI-R6 | `postMessage` schema mistakes cause silent deadlocks. | Medium | High | Schema version on every message; `readyTimeoutMs` default 30 s in the launcher; golden-path integration test covers load / reload / teardown. |
| UI-R7 | User programs assume AOT-compiled performance (animations, high-frequency UI updates). | High | Low | Document the interpreted-user-code gap prominently; position the companion as "playground / verification / demo", not "production UI". |
| UI-R8 | COOP/COEP confusion when users try multithreading. | Medium | Low | v1 is single-threaded default; UI-M8 adds documented headers-and-serve helper. |
| UI-R9 | Multiple runner iframes on one page compete for GPU/WebGL contexts. | Low | Medium | UI-M6 tests four-iframe scenarios; document the WebGL context limit; offer Software2D fallback option in `LaunchOptions`. |
| UI-R10 | Avalonia ref-pack pulls in unexpected analyzer / build-task NuGets whose runtime behaviour Carbide can't support. | Medium | Medium | Ref-pack script filters to `ref/` content only; `build/` and `analyzers/` trees are excluded; CI gates on what the pack contains. |

## 15. Alignment with adjacent work

- **`cs-agent-tools`** ([`src/cs-agent-tools/`](../../../cs-agent-tools/)). The agent-facing Python surface can gain `cs-kit build-avalonia` and `cs-kit preview-avalonia` commands that wrap the CLI and (eventually) the launcher. No changes proposed in this document; `cs-agent-tools` is a downstream consumer.
- **Carbide M5 (`msbuild-lite`)**. `carbide build --target avalonia-browser --project MyApp.csproj` uses M5's `.csproj` parser directly. UI-M5 depends on M5 for the project-file path; users without a `.csproj` can still use `--source`.
- **Carbide M6 (`@carbide/nuget`)**. If implemented before UI-M3, the launcher can feed NuGet-resolved additional references alongside the Avalonia ref-pack. Non-blocking.
- **Carbide M12 (source generators)**. Enables UI-M7 (compile-time `.axaml`). Non-blocking for UI-M1 through UI-M6.

## 16. Sample programs that anchor the design

These are the programs the companion family commits to making work end-to-end. The list doubles as acceptance test set.

### 16.1 Hello, world (code-only)

```csharp
// App.cs
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

public class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime svl)
        {
            svl.MainView = new TextBlock { Text = "Hello, Carbide + Avalonia" };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
```

Ship from UI-M3.

### 16.2 Counter (interactive)

```csharp
public class App : Application { /* ... */ }

public class CounterView : StackPanel
{
    private int _count = 0;
    private readonly TextBlock _label = new() { Text = "0" };
    public CounterView()
    {
        var button = new Button { Content = "+" };
        button.Click += (_, _) => _label.Text = (++_count).ToString();
        Children.Add(button);
        Children.Add(_label);
    }
}
```

Ship from UI-M3.

### 16.3 XAML runtime string

```csharp
using Avalonia.Markup.Xaml;

public class MainView : UserControl
{
    private const string Xaml = @"
        <UserControl xmlns='https://github.com/avaloniaui'
                     xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
            <TextBlock Text='Hello from XAML' />
        </UserControl>";

    public MainView()
    {
        AvaloniaRuntimeXamlLoader.Load(Xaml, this, null, Assembly.GetExecutingAssembly().GetName());
    }
}
```

Ship from UI-M4.

### 16.4 MVVM + runtime-parsed `.axaml` file

```csharp
// MainView.axaml is provided as a Carbide document.
// Carbide's UI-M4 integration emits a companion partial class that
// calls AvaloniaRuntimeXamlLoader at InitializeComponent() time.
public partial class MainView : UserControl
{
    public MainView() { InitializeComponent(); }
}
```

```xml
<!-- MainView.axaml -->
<UserControl xmlns='https://github.com/avaloniaui'>
  <StackPanel>
    <TextBlock Text='{Binding Greeting}' />
    <Button Command='{Binding Greet}' Content='Greet' />
  </StackPanel>
</UserControl>
```

```csharp
public class MainViewModel : ObservableObject
{
    private string _greeting = "Hello";
    public string Greeting { get => _greeting; set => SetProperty(ref _greeting, value); }
    public void Greet() => Greeting = "Hello, Vladimir";
}
```

Ship from UI-M4.

### 16.5 Multiple previews on one page

Host page with four iframes, each a different Avalonia view driven by `launchInIframe` with a different `BuildResult`. Shared `CarbideSession`; independent runners.

Ship from UI-M6.

## 17. Appendix — rejected design alternatives

Captured here so future proposals don't re-litigate them.

- **Single merged runtime bundle (Approach A).** Rejected in §4.1/§5. Can be revived as a third companion package if in-process compiler-GUI cross-talk becomes a clear requirement.
- **Avalonia.Browser multithreaded by default.** Rejected for v1: requires COOP/COEP, complicates static hosting. Single-threaded by default, multithreaded documented opt-in (UI-M8).
- **Automatic `App` class discovery via assembly attribute.** Considered. Ruled out of v1 for simplicity: one fewer moving part in the protocol.
- **Worker-based runner.** Rejected in §4.5: Avalonia's canvas requires main-thread DOM; `OffscreenCanvas` is an incomplete substitute for Avalonia's current browser backend.
- **Server-hosted compile (Kestrel + Blazor)** — Rejected in §4.5: violates no-SDK constraint.
- **Wrapping `dotnet-script` or CSharpRepl as the runner backend** — Rejected because both require the .NET SDK on host. Carbide is the only SDK-free compiler in the space.
- **Emit NativeAOT'd user code** — Rejected for v1: NativeAOT requires ahead-of-time compilation; Carbide's model is runtime compilation. User code stays interpreted; Avalonia itself can be AOT'd in the runner.

## 18. Document change control

This is a proposal, not a specification. Changes to this document should:

- preserve the approaches-comparison framing in §4 (new approaches get added as §4.N, not edited into existing ones);
- keep §5 "Recommendation" explicit — if new data changes the call, update §5 with a dated decision line rather than silently revising the conclusion;
- keep §10 "Required changes in Carbide core" additive — anything that would break `@carbide/core`'s public surface is a re-proposal;
- track §11 milestones alongside the actual delivery: completed milestones get a ✓ prefix and a pointer to the shipped artefact; deferred ones get a note;
- never drop §12 open questions without a decision record added in this directory.
