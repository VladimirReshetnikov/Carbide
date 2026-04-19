# Feasibility: integrating `src/Carbide` and Avalonia UI for compiling and running GUI C# apps in a browser

- Created (UTC): 2026-04-18T21:52:50Z
- Repository HEAD: 0b929aad1eef7e0307cede8e6fb6b4dd1468b1d3

Status: feasibility / architecture-survey report. Cross-reads the current state of Carbide (M5 plan, M4 shipped) against Avalonia 12's `Avalonia.Browser` WebAssembly head and the existing public precedent (`AvaloniaUI/XamlPlayground`). Output targets a decision about whether to pursue GUI scope for Carbide, and — if so — which of three architectural shapes to pursue.

Audience: repository owner, future Carbide contributors. Written to be actionable without further background research.

## 1. Request

> Research a possibility of integrating `src/Carbide` and `https://avaloniaui.net/` for compiling and running GUI C# apps in a browser. Create a detailed report under `docs/reports`.

Three scope words to pin down before anything else:

- **"integrate"** — both at the *compile* side (Carbide's Roslyn-in-WASM machinery compiles user code that references Avalonia) and at the *execute* side (the compiled user program actually renders UI to pixels on a visible surface).
- **"GUI C# apps"** — Avalonia-shaped single-view programs with controls, layout, input routing, text rendering, and optional XAML markup. Not just `MessageBox.Show`.
- **"in a browser"** — the constraint that made Carbide exist. Node.js execution is in-scope too; native dotnet SDK is not assumed.

## 2. Executive summary

| Capability | Today, unmodified | ~2 weeks of work | Months of work | Blocked or vision-breaking |
|---|---|---|---|---|
| Compile C# referencing `Avalonia.dll` / deps | — | ✔ (ref-pack + ReferenceRegistry wiring) | — | — |
| Run compiled program that imports BCL only | ✔ Carbide M4 | — | — | — |
| Run compiled program that calls `BuildAvaloniaApp().StartBrowserAppAsync(...)` | — | ✔ (Sketch A, see §7) | — | — |
| Load XAML at runtime (`AvaloniaRuntimeXamlLoader.Parse`) | — | ✔ (ships with Avalonia.Markup.Xaml.Loader) | — | — |
| `.axaml` files processed by Avalonia's XAML **source generator** | — | — | ✔ Carbide M12 (Band C) | — |
| Run with NativeAOT (production-quality startup/perf) | — | — | — | ✘ (user code is emitted at runtime and loaded via `Assembly.Load`; not a NativeAOT input) |
| Size budget ≤ 40 MB compressed | ✔ core | ✘ with Avalonia managed+native (+25–60 MB) | — | — |
| Vision non-goals N.2, N.3 ("Not a .NET platform replacement", "Not a GUI platform") | — | — | — | ✘ — requires a documented vision amendment or a **sibling project** with its own vision |

**Headline.** Technically, this is **already a solved problem** in the large: [`AvaloniaUI/XamlPlayground`](https://github.com/AvaloniaUI/XamlPlayground) is a production Avalonia-browser app that hosts Roslyn in-process, compiles user C# into an in-memory `Assembly`, loads it via `AssemblyLoadContext.LoadFromStream`, and parses user-authored XAML at runtime through `AvaloniaRuntimeXamlLoader`. The runtime+compiler coexistence is demonstrated to work in a shipped WebAssembly bundle. Carbide and Avalonia.Browser furthermore share the exact same SDK (`Microsoft.NET.Sdk.WebAssembly`) and runtime identifier (`browser-wasm`), so the two stacks are not architecturally alien — they are two C# consumers of one .NET WebAssembly runtime.

The honest engineering gap is narrow and specific: about **1–2 weeks of work on four seams** (see §7 Sketch A) to deliver "compile-and-show-an-Avalonia-window" in the browser, plus a larger and more ambiguous decision about **vision alignment** — Carbide's vision document explicitly lists Avalonia among the `N.2` non-goals. The technical path is clear; the strategic path requires an owner call about whether GUI belongs in Carbide's scope, in a **sibling project** like `src/Carbide.Avalonia` or `@carbide-ui/avalonia`, or nowhere at all.

The strategic risk (separately from the technical one) is **size-budget pressure**: adding Avalonia's managed assemblies, its browser-specific native WebAssembly glue (Skia-backed rendering via WebGL), and the Avalonia.Markup.Xaml.Loader dependency pushes the bundle from Carbide's current ~30 MB target into the 60–100 MB range — past Carbide's declared budget. This alone justifies carrying the integration in a *sibling* package rather than folding it into `@carbide/core`.

## 3. Local inputs inventoried

### 3.1 Carbide (current state, M5 plan in flight)

Carbide is the repository's in-house "C# compile-and-run framework that ships as a single npm package, embeds the .NET runtime and Roslyn, and works identically in a browser tab and a Node.js process." (Quoted from [`src/Carbide/docs/carbide-vision__2026-04-17__16-16-47-000000.md`](../../carbide-vision__2026-04-17__16-16-47-000000.md).) Status at the report's commit: **M4 shipped, M5 in progress**.

Layered runtime topology (from [`carbide-architecture-and-implementation-plan`](../../planning/carbide-architecture-and-implementation-plan__2026-04-17__16-16-47-000000.md) §1):

- L9 Consumer → L8 `@carbide/core` TS surface → L7 Host adapter (browser or Node) → L6 .NET-WASM boot → L5 `Carbide.Core` (C#, compiled to WASM) → L4 Roslyn. Ref-pack, NuGet, msbuild-lite sit alongside.

Key properties that matter for this report:

- **Uses `Microsoft.NET.Sdk.WebAssembly` with `RuntimeIdentifier=browser-wasm`, `TargetFramework=$(NetVersion)` (net10.0 in practice).** See [`src/Carbide/packages/core/src/Carbide.Core.csproj:1`](../../../packages/core/src/Carbide.Core.csproj). This is **exactly** the SDK + RID Avalonia.Browser uses.
- **JSExport boundary is string-based JSON**; `BuildAsync` returns `{pe, pdb}` as base64 ([`CompilationInterop.cs:86`](../../../packages/core/src/CompilationInterop.cs)). User code never escapes the browser tab that loaded Carbide.
- **`ProjectCompiler.RunAsync`** ([`src/Carbide/packages/core/src/Services/ProjectCompiler.cs:311`](../../../packages/core/src/Services/ProjectCompiler.cs)) compiles → `Assembly.Load(byte[])` → calls `EntryPoint.Invoke`, captures stdout/stderr via `StringWriter`. No `AssemblyLoadContext.Unload()` yet (Mono-WASM's default context is non-collectible; running user code repeatedly accumulates assemblies until session `Reset`).
- **Reference registry** already exists (`session.addReference(bytes) → handle`, `project.addReference(handle)`) — ready to absorb an arbitrary set of Avalonia DLLs at runtime.
- **Multi-document source support** already in M2; implicit usings already synthesized; Portable-PDB emission already wired.
- **Vision non-goals N.2, N.3** explicitly call out Avalonia/GUI as out of scope for `@carbide/core`. Integration with Avalonia is either a **vision amendment** or a **sibling project** decision.

### 3.2 Avalonia.Browser (WebAssembly head)

Avalonia (12.x at the time of this report) ships an official WebAssembly host as the `Avalonia.Browser` NuGet package. The canonical template layout from [`AvaloniaUI/avalonia-dotnet-templates`](https://github.com/AvaloniaUI/avalonia-dotnet-templates) `templates/csharp/xplat/AvaloniaTest.Browser/`:

`AvaloniaTest.Browser.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.WebAssembly">
  <PropertyGroup>
    <TargetFramework>net10.0-browser</TargetFramework>
    <OutputType>Exe</OutputType>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Avalonia.Browser" Version="$(AvaloniaVersion)" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\AvaloniaTest\AvaloniaTest.csproj" />
  </ItemGroup>
</Project>
```

`Program.cs`:

```csharp
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Browser;
using AvaloniaTest;

internal sealed partial class Program
{
    private static Task Main(string[] args) =>
        BuildAvaloniaApp()
            .WithInterFont()
            .StartBrowserAppAsync("out");

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>();
}
```

`wwwroot/index.html`: a `<div id="out"></div>` (the mount point Avalonia attaches its canvas to) plus `<script type='module' src="./main.js"></script>`.

`wwwroot/main.js` (verbatim):

```js
import { dotnet } from './_framework/dotnet.js'

const is_browser = typeof window != "undefined";
if (!is_browser) throw new Error(`Expected to be running in a browser`);

const dotnetRuntime = await dotnet
    .withDiagnosticTracing(false)
    .withApplicationArgumentsFromQuery()
    .create();

const config = dotnetRuntime.getConfig();
await dotnetRuntime.runMain(config.mainAssemblyName, [globalThis.location.href]);
```

Rendering model (from [Avalonia docs](https://docs.avaloniaui.net/docs/platform-specific-guides/webassembly) and [Chapter 20 of the Avalonia Book](https://wieslawsoltes.github.io/AvaloniaBook/Chapters/Chapter20.html)):

- **Skia-backed pipeline**; three rendering modes, preferred in this order: **WebGL2 → WebGL1 → Software2D**. Not DOM; Avalonia paints every pixel itself onto a `<canvas>` that is appended inside the `<div id="out">` mount point.
- Requires `<div id="out"></div>` (or caller-chosen id) in the hosting HTML.
- `ISingleViewApplicationLifetime` is the lifetime contract; no `Window`s, no menus, no tray icons.
- File dialogs use the browser's **File System Access API** (with a polyfill for older browsers). Networking is normal `HttpClient` subject to CORS. Clipboard is restricted to user-gesture events and text.
- Multi-threading requires **COOP/COEP** response headers and browser support; single-threaded is the default.
- Production-recommended config adds `<RunAOTCompilation>true</RunAOTCompilation>` and `<InvariantGlobalization>true</InvariantGlobalization>`. NativeAOT can be enabled with `<PublishAot>true</PublishAot>` in Release.

Payload surface (rough, version-dependent but order-of-magnitude accurate):

| Item | Compressed | Uncompressed |
|---|---:|---:|
| Carbide baseline (`@carbide/core` M4) | ~25–30 MB | ~90–110 MB |
| Avalonia managed assemblies (Avalonia.\*, Skia interop) | ~8–15 MB | ~25–45 MB |
| Avalonia native (Skia-on-WASM, Harfbuzz) | ~5–10 MB | ~15–30 MB |
| Reference pack entries for Avalonia (compile-time only) | +3–5 MB | +10–15 MB |

Rough total: adding Avalonia pushes the bundle to ~40–55 MB compressed / ~140–200 MB uncompressed. This is above `@carbide/core`'s declared budget (`P.8`: ≤ 40 MB compressed, ≤ 120 MB uncompressed) but comparable to many modern WASM-based IDEs.

### 3.3 XamlPlayground — the existing public precedent

This is the critical inventory entry. [`AvaloniaUI/XamlPlayground`](https://github.com/AvaloniaUI/XamlPlayground) is a production Avalonia-on-WebAssembly application that **already does what the request asks for**, modulo delivery-as-npm-package. It is an existence proof that the technical pattern works end-to-end in a browser.

Key source points from the repo:

- [`src/XamlPlayground.Browser/XamlPlayground.Browser.csproj`](https://github.com/AvaloniaUI/XamlPlayground/blob/main/src/XamlPlayground.Browser/XamlPlayground.Browser.csproj) — `net10.0-browser`, `Microsoft.NET.Sdk.WebAssembly`, `Avalonia.Browser`, Release sets `RunAOTCompilation=true`, `InvariantGlobalization=true`, `TrimmerRootAssembly` entries for `Avalonia.Base`, `Avalonia.Controls`, `Avalonia.Markup`, `Avalonia.Markup.Xaml`, and `Avalonia.Markup.Xaml.Loader`.
- [`src/XamlPlayground/XamlPlayground.csproj`](https://github.com/AvaloniaUI/XamlPlayground/blob/main/src/XamlPlayground/XamlPlayground.csproj) — references `Avalonia`, `Avalonia.Themes.Fluent`, **`Avalonia.Markup.Xaml.Loader`** (runtime XAML), and **`Microsoft.CodeAnalysis.CSharp`** + `.CSharp.Scripting` (Roslyn in the same process as the Avalonia app).
- [`src/XamlPlayground/Services/CompilerService.cs`](https://github.com/AvaloniaUI/XamlPlayground/blob/main/src/XamlPlayground/Services/CompilerService.cs):

```csharp
private static void LoadReferences()
{
    var assemblies = AppDomain.CurrentDomain.GetAssemblies();
    var appDomainReferences = new List<PortableExecutableReference>();
    foreach (var assembly in assemblies)
    {
        if (!string.IsNullOrWhiteSpace(assembly.Location))
        {
            appDomainReferences.Add(MetadataReference.CreateFromFile(assembly.Location));
        }
        else
        {
            unsafe
            {
                if (assembly.TryGetRawMetadata(out var blob, out var length))
                {
                    var moduleMetadata = ModuleMetadata.CreateFromMetadata((IntPtr)blob, length);
                    var assemblyMetadata = AssemblyMetadata.Create(moduleMetadata);
                    appDomainReferences.Add(assemblyMetadata.GetReference());
                }
            }
        }
    }
    s_references = appDomainReferences.ToArray();
}
```

This uses the public [`Assembly.TryGetRawMetadata(out byte* blob, out int length)`](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.runtimereflectionextensions.trygetrawmetadata) API to harvest PE metadata **from already-loaded-in-process assemblies** — the approach Avalonia.Browser apps typically use because `Assembly.Location` is empty on Mono-WASM. User code is compiled as `OutputKind.DynamicallyLinkedLibrary` and loaded with a **collectible** `AssemblyLoadContext` (no `Unload` is actually called, but the `AssemblyLoadContext` at least isolates this run from the next).

The runtime XAML loader is used to parse user-authored `axaml` text directly into an Avalonia object tree — no source generator required. This is a first-class Avalonia feature (see [Avalonia DeepWiki: "Runtime XAML Support and Helpers"](https://deepwiki.com/AvaloniaUI/Avalonia/6.2-runtime-xaml-support-and-helpers)) and side-steps Carbide's biggest source-generator gap.

**What XamlPlayground does NOT do that Carbide already provides**:

- Multi-document C# compilation (XamlPlayground is single-file per run).
- PE + Portable-PDB emission to disk (Carbide M4).
- Node-host parity (XamlPlayground is browser-only).
- A public JSON-first API surface.
- NuGet reference injection (XamlPlayground's references are hard-coded to `AppDomain.CurrentDomain.GetAssemblies()`).
- A CLI.

**What XamlPlayground does that Carbide does NOT**:

- Compile + Avalonia host in the same process.
- Runtime XAML loading.
- Collectible assembly contexts per run.
- Render pixels.

The gap is symmetric and integration-friendly.

## 4. Why this is technically feasible — the architectural overlap

Carbide and Avalonia.Browser are **both consumers of the same .NET WebAssembly runtime**. Compare:

| Dimension | `Carbide.Core.csproj` | `AvaloniaTest.Browser.csproj` | Identical? |
|---|---|---|---|
| SDK | `Microsoft.NET.Sdk.WebAssembly` | `Microsoft.NET.Sdk.WebAssembly` | **Yes** |
| RuntimeIdentifier | `browser-wasm` | `browser-wasm` (implied by `-browser` TFM) | **Yes** |
| Target framework | `net10.0` | `net10.0-browser` | Compatible (superset on the Avalonia side) |
| OutputType | `Exe` | `Exe` | Yes |
| AllowUnsafeBlocks | `true` (for Roslyn) | `true` (for Avalonia JS interop source-gen) | Yes |
| Trimming | off at M4 (re-enabled after ref-pack) | on with explicit `TrimmerRootAssembly` list | Different per head, same mechanism |
| AOT | off (user code runs interpreted) | recommended on in Release | Different per head, not mutually exclusive |

The overlap is not incidental — it is **structural**. The "host page" in an Avalonia.Browser app (`index.html` + `main.js` + `dotnet.js`) is functionally the same as Carbide's Node asset-server path + the same `dotnet.js` boot sequence, with one addition: an HTML element Avalonia mounts its canvas onto.

That means any "Carbide + Avalonia" integration has one essential design decision to make: **does the user program run inside the same runtime instance as Carbide's compiler, or in a separate one?**

Both answers are technically viable. See §7 for three concrete architectures, one per answer.

## 5. What is in Carbide today that helps

Read in priority order of how much each accelerates an Avalonia integration.

1. **`Microsoft.NET.Sdk.WebAssembly` + `browser-wasm` already wired** ([`Carbide.Core.csproj:1`](../../../packages/core/src/Carbide.Core.csproj)). No foundational SDK change needed; adding Avalonia is adding PackageReferences, not changing the build model.
2. **`ReferenceRegistry` + `session.addReference(bytes)`** ([`ReferenceRegistry.cs`](../../../packages/core/src/Services/ReferenceRegistry.cs), public TS at [`session.ts:66`](../../../packages/core/src/ts/session.ts)). Already the correct seam to hand Avalonia assembly bytes to the compiler. A `@carbide-ui/refs-avalonia` sibling package can auto-populate this registry at session-init time.
3. **Multi-document source support** (`AddSource` / `UpdateSource` / `RemoveSource`, [`ProjectCompiler.cs:110`](../../../packages/core/src/Services/ProjectCompiler.cs)). A real Avalonia app has `App.axaml.cs`, `MainView.axaml.cs`, view-models, and resources — all multi-document.
4. **PE + Portable-PDB emission** (M4, [`BuildAsync`](../../../packages/core/src/Services/ProjectCompiler.cs)). This is the minimal handoff artefact for any Sketch-B/-C architecture where the runtime is separate from the compiler.
5. **Implicit-usings synthesis** ([`ProjectCompiler.cs:37`](../../../packages/core/src/Services/ProjectCompiler.cs)). Already there; Avalonia apps normally add `using Avalonia;` etc., but the existing implicit-usings machinery is easy to extend with Avalonia-specific globals.
6. **Host adapter abstraction** ([`adapter.ts`](../../../packages/core/src/ts/host/adapter.ts)). A future `BrowserAvaloniaHostAdapter` can live next to `BrowserHostAdapter`, providing the `<div id="out">` mount seam without disturbing the core surface.
7. **`project.run()` sandboxing via `Console.SetOut` + `AppDomain.CurrentDomain.AssemblyResolve`** ([`ProjectCompiler.cs:363`](../../../packages/core/src/Services/ProjectCompiler.cs)). The AssemblyResolve hook is exactly what is needed to answer Avalonia's reflection-based type probes when the user PE references Avalonia types but Avalonia was loaded via `Assembly.Load(byte[])` rather than by the runtime's default resolver.

## 6. What is NOT in Carbide today that matters

1. **`AssemblyLoadContext.Unload()` / per-run isolation.** Carbide uses `Assembly.Load(byte[])` directly; the assemblies leak into the default `AssemblyLoadContext` and persist until session teardown. GUI runs that attach event handlers, timers, or rendering loops to the DOM would accumulate across re-runs if nothing calls `Application.Current.Shutdown()` between them. The XamlPlayground pattern (`new AssemblyLoadContext(..., isCollectible: true).LoadFromStream(stream)`) is strictly better for a GUI integration. *Ported cost: ~200 LOC in `ProjectCompiler.RunAsync`.*
2. **Source generators.** The Avalonia **XAML source generator** (`Avalonia.Build.Tasks` → `XamlX`) is what converts `.axaml` resource files to IL at build time. Carbide's M12 (Band C stretch) is the earliest honest moment to offer this. *Mitigation:* `Avalonia.Markup.Xaml.Loader` provides `AvaloniaRuntimeXamlLoader.Parse(xamlText, …)` which bypasses the source generator entirely and is the path XamlPlayground and Avalonia's own hot-reload scenarios use. So short-term, XAML-as-string is the supported shape; `.axaml`-as-file waits for M12.
3. **A canvas mount surface.** Carbide's HTML shell has no DOM element the user program can paint onto. The Browser host adapter exposes URL resolution, stdout capture, reference-pack discovery — none of which is a drawing surface. Adding one is an adapter extension, not a core change.
4. **`ISingleViewApplicationLifetime` wiring.** The `StartBrowserAppAsync("out")` call installs Avalonia as the owner of the process's lifetime. Carbide's `RunAsync` assumes the user program returns control after `EntryPoint.Invoke`. Avalonia's `StartBrowserAppAsync` returns a `Task` that resolves quickly (after setup) but the app itself keeps running via the DOM event loop. This is a *semantic* difference, not a *technical* one — Carbide's `RunResult` is still well-defined (Success/exitCode/stdOut), but the program is now painting pixels in the background until the user reloads the page.
5. **Size budget.** `@carbide/core`'s P.8 target is ≤ 40 MB compressed. A bundled-Avalonia Carbide exceeds that; a sibling package avoids the tradeoff.
6. **Carbide's vision non-goals N.2 and N.3.** "Not a .NET platform replacement" and "Not a GUI platform" are explicit, underlined in the vision document, and name Avalonia by name. The architecture path is clear; the *scope* path requires an owner call.

## 7. Three integration sketches

Ordered by delivery cost, ascending. Each sketch is independently shippable; higher-numbered sketches subsume lower ones. Pick one; the three are not a layered stack.

### 7.1 Sketch A — Sibling runtime package `@carbide-ui/avalonia` (in-process)

**Idea.** Ship a sibling npm package whose `_framework/` is **a Carbide Core + Avalonia.Browser merged runtime**. The hosting HTML has a `<div id="out"></div>`. User code is compiled by Carbide and executed in-process. Avalonia's pipeline mounts to the pre-existing `<div id="out">`.

**What gets built.** Four seams:

1. **`Carbide.Core.AvaloniaHost.csproj`** — a new C# project in `src/Carbide/packages/avalonia-host/src/` that references `Avalonia.Browser` (and dependencies) and publishes a `_framework/` directory containing everything Carbide's runtime has **plus** the Avalonia managed + native assets. Uses the same `Microsoft.NET.Sdk.WebAssembly`.
2. **`@carbide-ui/refs-avalonia`** — the compile-time reference pack for Avalonia (extract `Avalonia.Browser.nupkg`'s `ref/net10.0-browser/*.dll` and deps). Sibling to `@carbide/refs-net10.0`. Session init auto-registers the DLLs via `session.addReference`.
3. **`BrowserAvaloniaHostAdapter`** — a TS-side host adapter next to `BrowserHostAdapter` that (a) resolves the Avalonia-host `_framework/` URL, (b) ensures the host page has a `<div id="out">`, (c) exposes a `mountTargetId` config to the guest program via query string or `withApplicationArgumentsFromQuery`.
4. **`CarbideAvaloniaSession`** — the TS public surface, a thin subclass of `CarbideSession` that bundles an auto-attached Avalonia ref-pack. Adds a single method: `session.runAvalonia(project)`. Under the hood it's `project.run()` with `Assembly.Load` of the emitted PE; the user's Main calls `BuildAvaloniaApp().StartBrowserAppAsync("out")` and Avalonia takes over.

**User-visible API.**

```ts
import { CarbideAvaloniaSession } from "@carbide-ui/avalonia";

const session = await CarbideAvaloniaSession.initializeAsync({
    mountTargetId: "out",
});
const project = session.createProject();
project.addSource("App.axaml.cs", `public class App : Avalonia.Application { ... }`);
project.addSource("MainView.cs", `...`);
project.addSource("Program.cs", `
    using Avalonia;
    using Avalonia.Browser;
    [assembly: System.Runtime.Versioning.SupportedOSPlatform("browser")]
    public static class Program {
        public static System.Threading.Tasks.Task Main(string[] args) =>
            AppBuilder.Configure<App>().StartBrowserAppAsync("out");
    }
`);
await session.runAvalonia(project);   // pixels now on the canvas
```

**Delivery estimate.** 1–2 weeks of engineering, most of it build-pipeline wiring:

- Days 1–2: `Carbide.Core.AvaloniaHost` project skeleton + `dotnet publish` pipeline that produces a merged `_framework/`.
- Day 3: `@carbide-ui/refs-avalonia` ref-pack extraction from `Avalonia.Browser.nupkg`.
- Days 4–5: `BrowserAvaloniaHostAdapter`, `CarbideAvaloniaSession`, integration tests against a trivial "button with text" Avalonia app.
- Days 6–7: Runtime XAML sample (`AvaloniaRuntimeXamlLoader.Parse`), theme-assembly wiring, Fluent theme reference.
- Days 8–10: Buffer for native-asset plumbing (Skia-on-WASM), issues with `AppDomain.CurrentDomain.AssemblyResolve` handler during Avalonia's reflection probes, size-budget observations.

**Honest risks.**

- Size budget (§3.2): ~50–60 MB compressed total.
- Native Skia assets must be loaded by the .NET WASM runtime at *boot*, not at user `Assembly.Load` time. This is why the runtime has to be built with `Avalonia.Browser` as a compile-time dep (the native `*.wasm` and JS glue are emitted as part of `dotnet publish`), even though at runtime the user program is the one that *calls* Avalonia. "Avalonia in, but not started" is the published shape.
- `AssemblyLoadContext` isolation (see §6.1) needs to be added **before** running a real GUI program repeatedly, or the second run will see doubled event handlers.
- If Carbide enables NativeAOT in future (vision Q.3), the user PE loaded via `Assembly.Load` runs **interpreted** regardless; Avalonia's own code is AOT'd. Interpreted user code controlling an AOT'd UI framework is fine for interaction but has measurable UI latency. Documented, not fixed.

**Compatible with Carbide's non-goals.** Yes, if delivered as a **sibling package** with its own vision and its own size budget. No, if folded into `@carbide/core`. §9 below recommends the former.

### 7.2 Sketch B — Separate iframe runtime, Carbide emits PE (out-of-process)

**Idea.** Carbide stays exactly as it is (compiler only). A second, independent Avalonia.Browser bundle runs in an iframe. The handoff is: Carbide `project.build()` → emits PE → serialize as Blob URL → iframe fetches the PE, stitches it into its own `AssemblyLoadContext`, invokes entry point, renders on its own `<div id="out">`.

**What gets built.**

1. **`@carbide-ui/runner-avalonia`** — a *separate* WebAssembly runtime with Avalonia baked in. Identical to Sketch A's merged runtime, but no Carbide compiler inside; just the Avalonia host plus a `postMessage` listener that accepts `{ pe, pdb }` payloads and dynamically loads them.
2. **Bridge TS module in `@carbide/core`** — a helper `launchInIframe(buildResult, iframeElement)` that receives a Carbide `BuildResult` and a DOM iframe, posts the PE to the runner, and returns a promise that resolves when the iframe reports "UI alive".

**User-visible API.**

```ts
import { CarbideSession } from "@carbide/core";
import { launchInIframe } from "@carbide-ui/launcher-avalonia";

const session = await CarbideSession.initializeAsync();
const project = session.createProject();
project.addSource("Program.cs", /* ... */);
const build = await project.build();
await launchInIframe(build, document.getElementById("preview-iframe"));
```

**Delivery estimate.** 1.5–2.5 weeks. Roughly the same as Sketch A on build + runtime sides, plus the cross-frame `postMessage` protocol and origin/security story. The runtime bundle is smaller per page load (only GUI users download Avalonia), but the memory footprint is **higher** during use because two .NET WASM runtimes run concurrently.

**Upsides over Sketch A.**

- Carbide's size budget stays intact (`@carbide/core` unchanged; Avalonia runtime loaded only when the user requests GUI preview).
- Crash isolation: if the user program crashes Avalonia, the iframe is the blast radius; the Carbide compiler and the editor context survive.
- Multiple independent previews are straightforward (one iframe each).

**Downsides vs Sketch A.**

- Two runtimes = two cold-start costs. On a cold load of a page that wants both, the user waits for ~30 MB of Carbide *and* ~30 MB of Avalonia+runtime, cached independently.
- `postMessage` copies the PE bytes across origins; for typical user programs this is small, but it's still an extra serialization hop.
- The user cannot call back into Carbide from inside their running Avalonia app easily (e.g., "I want my GUI to call the Roslyn completions service for an embedded code editor"). A cross-iframe JSON-RPC protocol would be required.

**Honest risks.**

- Two-runtime coordination is a debugging tax. When something goes wrong, figuring out "who has the wrong state" is harder than in a single-runtime setup.
- Cross-origin iframe embedding needs COOP/COEP carefully in the Avalonia runner if multi-threading is ever enabled.

**Compatible with Carbide's non-goals.** Yes. Carbide itself stays strictly a compiler. GUI belongs to a cleanly separate package. This sketch aligns best with the vision document's letter.

### 7.3 Sketch C — Carbide as an offline build driver for Avalonia bundles (Node only)

**Idea.** The integration is CLI-only. `npx carbide build --target avalonia-browser --project MyApp.csproj --out ./dist` produces a ready-to-deploy Avalonia.Browser bundle: HTML + JS + `_framework/` + compiled user PE. There is no in-browser execution at all; the output is a static site. Carbide is used as the Roslyn driver for the user-PE portion; Avalonia's pre-built `_framework/` is copied from `@carbide-ui/refs-avalonia-runtime` (or equivalent).

**What gets built.**

1. **CLI subcommand** — `carbide build --target avalonia-browser` in `@carbide/cli`.
2. **`@carbide-ui/avalonia-runtime-bundle`** — an npm package carrying the pre-built Avalonia.Browser `_framework/` directory for the pinned Avalonia + .NET versions.
3. **Template writer** — the CLI emits `index.html`, `main.js`, and a wrapping `Program.cs` equivalent that loads the user-compiled PE (or, more honestly, emits a stub `Program.cs` that calls into the user code's entry point, concats it with user sources, and compiles everything together).

**User-visible API.**

```bash
# Offline build:
npx carbide build --target avalonia-browser \
    --project MyApp.csproj --out ./dist

# Host statically:
python -m http.server --directory ./dist
```

**Delivery estimate.** 1 week (all Node-side tooling; no browser runtime work).

**Upsides.**

- No in-browser runtime engineering. No size-budget pressure on `@carbide/core`.
- The output is a **deployable artifact** — the user can ship it to any static host.
- Matches Carbide's existing `M4` shape of "emit bytes to disk" and extends it naturally.

**Downsides.**

- No interactive "compile-and-see-UI" loop in the browser. The request explicitly asks for **running** GUI apps in a browser, not just building them; this sketch satisfies "build" but delegates "run" to whatever static host the user picks.
- Does not enable agent-playground scenarios (agent writes code, sees UI, iterates).

**When to pick this.** If the driving use case is "Carbide-built Avalonia app as a publishable artifact" rather than "in-browser playground". For agent-use-case §4 of Carbide's vision, Sketch A or B is a better fit.

## 8. Cross-cutting technical questions, resolved

### 8.1 How does Roslyn see Avalonia's types without `Assembly.Location`?

Two paths; both work on Mono-WASM:

- **WasmSharp/Carbide pattern**: HTTP-fetch the DLLs from a co-located `_framework/` URL into bytes, feed to `MetadataReference.CreateFromImage(bytes)`.
- **XamlPlayground pattern**: call `assembly.TryGetRawMetadata(out byte* blob, out int length)` on already-loaded assemblies and `ModuleMetadata.CreateFromMetadata((IntPtr)blob, length)`.

Both produce `PortableExecutableReference` objects that Roslyn's `CSharpCompilation` accepts. Carbide's existing `WasmMetadataReferenceResolver` ([`src/Carbide/packages/core/src/Services/WasmMetadataReferenceResolver.cs`](../../../packages/core/src/Services/WasmMetadataReferenceResolver.cs)) already implements the first path and is the integration seam for the second.

### 8.2 How does the user's compiled PE find Avalonia types at *runtime*?

Carbide's `ProjectCompiler.RunAsync` already installs an `AppDomain.CurrentDomain.AssemblyResolve` handler that answers simple-name lookups from the session's reference registry ([`ProjectCompiler.cs:363`](../../../packages/core/src/Services/ProjectCompiler.cs)). The same handler answers Avalonia's probe requests during CLR JIT/metadata binding of user code. No change required.

Complication: Avalonia performs reflection-driven discovery during `AppBuilder.Configure<App>()` (looking for `[AvaloniaResource]`, theme asset URIs, etc.). All of this operates over already-loaded assemblies, which is the case here. No new seam required.

### 8.3 What happens on a second `project.run()` call?

In Carbide M4 today, the second run **loads a second copy** of the user PE into the default `AssemblyLoadContext`. On a console program, the effect is harmless (the second run's entry point runs; the first program has already returned). On an Avalonia program, the first program has **attached DOM event handlers and a render loop** that are still live; the second run would attach a second set. The canvas would paint twice, input events would route to both, and the UI would be wrong.

Fix: adopt the XamlPlayground pattern — every run gets a new `AssemblyLoadContext(name: …, isCollectible: true)`. Before running, call `Application.Current?.Shutdown()` (or Avalonia's equivalent lifecycle teardown) to wind down the previous run. After the program returns or is told to stop, the `AssemblyLoadContext` can be `Unload()`ed — collectible contexts let the runtime reclaim the PE bytes once no references remain.

**This is a correctness-critical change for any GUI integration.** It is also a good change for Carbide in isolation: adding collectible `AssemblyLoadContext`s would let repeated console runs not leak assemblies either, which is a relevant gain for long-running Carbide sessions (agent loops, in-editor re-runs).

### 8.4 Can the user ship XAML in `.axaml` files?

Not without Carbide M12 (source-generator subset, Band C). Until then:

- **Supported**: XAML as *text strings*, parsed at runtime via `AvaloniaRuntimeXamlLoader.Parse` (or `.Load<T>(…)`) from `Avalonia.Markup.Xaml.Loader`. Most Avalonia demos, including XamlPlayground, use this. The compile-time `[AvaloniaResource]` item-group path does not apply, but the runtime path is fully featured.
- **Supported with per-run convention**: user `project.addSource("MainView.axaml", xamlText)` — Carbide stores the text but does not know how to compile XAML. Could be extended: Carbide detects `.axaml` paths and **at run time** the generated stub C# calls `AvaloniaRuntimeXamlLoader.Parse(xamlText, parentAssembly)` to reconstitute the object tree.
- **Not supported until M12**: `.axaml` files compiled into the assembly via the XAML source generator, referenced implicitly by `AvaloniaXamlLoader.Load(this)`.

### 8.5 Threading and `COOP/COEP`

Avalonia.Browser supports optional multithreading via `SharedArrayBuffer`, which requires `Cross-Origin-Opener-Policy: same-origin` and `Cross-Origin-Embedder-Policy: require-corp` response headers. Carbide's own Node asset-server currently does not set these; it would need to for Sketch A/B to run multithreaded Avalonia. Single-threaded Avalonia works fine with default headers.

In the browser host, any site embedding `@carbide-ui/avalonia` would need the same headers. For static hosts this is a server-configuration change; for sandboxed iframes (Sketch B), both the outer and inner pages need COOP/COEP aligned. **Recommend single-threaded as the v1 default**; make multithreaded a documented opt-in that also documents the header requirements.

### 8.6 NativeAOT

Avalonia.Browser release builds recommend `<RunAOTCompilation>true</RunAOTCompilation>` (Mono AOT for managed WASM) and `<PublishAot>true</PublishAot>` (NativeAOT). Neither applies to *user* code in Carbide: user code is emitted at runtime and loaded by `Assembly.Load(byte[])`, which the Mono interpreter then executes. NativeAOT pre-compiles PE → native; it cannot consume bytes at runtime.

This is *the* irreducible performance gap: a Carbide-hosted Avalonia app runs the framework AOT'd and the user code interpreted. For simple demos this is unnoticeable; for a 1000-frame-per-second animation driven by user code, this will show. Documented as a known limit. In practice, "document the gap" is almost certainly good enough — the target use cases are playgrounds and agent verification, not production UIs.

### 8.7 Identity and versioning

Any integration package should pin a **matching triple**: `(Carbide version, Avalonia version, .NET runtime version)`. Changes in any of the three should force a bump in the integration package's version. The drift document pattern in `src/Carbide/docs/drift/` already exists; extend it to include Avalonia upstream drift.

## 9. Recommendation

**Start with Sketch B (separate iframe runtime) if and only if interactive in-browser Avalonia is a real target; otherwise, start with Sketch C (CLI-only offline build).**

Reasons:

1. **Sketch B respects Carbide's vision non-goals.** Carbide itself stays strictly "compile and run console-shaped programs". GUI lives in a cleanly separate package. No vision amendment needed.
2. **Sketch B keeps the size budget intact.** `@carbide/core` users who don't want GUI don't download Avalonia. `@carbide-ui/avalonia-runner` users pay for what they use.
3. **Sketch A's in-process savings are not worth the vision tension.** Yes, one runtime is less memory than two; but Carbide's usual session is short-lived (agent verification, validator pass) and re-running within the same runtime is not a common enough workflow to justify doubling the bundle size and opening the N.2/N.3 scope question.
4. **Sketch C is cheap and complementary.** `carbide build --target avalonia-browser` can ship at the same time as Sketch B (they share the ref-pack and the runtime bundle), is useful on its own for "I want to publish an Avalonia app I wrote in Carbide", and costs ~1 week. It can ship first while Sketch B is being built.

**A staged delivery path:**

- **Stage 1 (1 week).** Ship `@carbide-ui/refs-avalonia` + `@carbide-ui/avalonia-runtime-bundle`. Ship `carbide build --target avalonia-browser` (Sketch C). No in-browser running yet; offline builds only.
- **Stage 2 (1.5–2 weeks, concurrent or after).** Land collectible `AssemblyLoadContext` in `ProjectCompiler.RunAsync` as a general Carbide improvement (§8.3). Ship `@carbide-ui/avalonia-runner` (the iframe-hosted Avalonia runtime) + `@carbide-ui/launcher` (the Carbide-side bridge). Wire `launchInIframe(buildResult, iframe)` (Sketch B).
- **Stage 3 (optional, Band B).** Support `.axaml` as source files *at runtime* via `AvaloniaRuntimeXamlLoader.Parse`, documented but without the XAML source generator. Users write Avalonia code + inline XAML strings (or `.axaml` loaded at runtime). This extends Sketch B naturally.
- **Stage 4 (Band C, tied to Carbide M12).** Plumb the Avalonia XAML source generator through Carbide's generator driver. Build-time `.axaml` files become supported. This is the point at which a real-world Avalonia codebase (MVVM + `.axaml` files) "just works" in Carbide.

Sketch A remains available as a **fallback** if size budgets change or if a compelling in-process workflow emerges later. Nothing in Stages 1–3 precludes an A-shaped merged-runtime package later.

**Vision implication.** If the owner accepts this recommendation, the Carbide vision document should gain a short §15 "Companion projects" section that names `@carbide-ui/*` as a separate family with its own non-goals (e.g., "not a production Avalonia host"), keeping `@carbide/core`'s N.2/N.3 intact. The request's ask ("GUI apps in a browser") is fulfilled by the sibling package, not by Carbide's core.

## 10. Concrete acceptance tests for Stage 1 + Stage 2

Stated up front so the delivery has a clear gate.

**Stage 1 acceptance — offline build.**

```bash
# A minimal Avalonia "hello world" — App.axaml.cs + MainView.cs + Program.cs, all code (no .axaml yet).
cd /tmp && mkdir hello-avalonia && cd hello-avalonia
# ... write three files ...
npx carbide build --target avalonia-browser --out ./dist
python -m http.server --directory ./dist &
# Open http://localhost:8000 in a browser — the Avalonia window renders a "Hello" label.
```

Must: `dist/_framework/` contains Avalonia + the user PE; `dist/index.html` has `<div id="out">`; the page loads and paints.

**Stage 2 acceptance — in-browser compile + run.**

```ts
import { CarbideSession } from "@carbide/core";
import { launchInIframe } from "@carbide-ui/launcher";

const session = await CarbideSession.initializeAsync();
const project = session.createProject();
project.addSource("App.cs", /* Avalonia App class */);
project.addSource("MainView.cs", /* Avalonia Control class */);
project.addSource("Program.cs", /* BuildAvaloniaApp().StartBrowserAppAsync("out") */);
const build = await project.build();
await launchInIframe(build, document.getElementById("preview"));
```

Must: `build.success === true`; the iframe renders the Avalonia UI; a second `launchInIframe` call (with modified sources) tears down the previous UI and mounts the new one; Carbide's own session survives both runs.

## 11. Risks and mitigations

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---:|---:|---|
| R1 | Avalonia's size pushes the total bundle past what end users tolerate. | High | Medium | Sibling package (Sketch B/C) not merged into `@carbide/core`; compression + Brotli for static hosts; document budget. |
| R2 | NativeAOT mismatch: Avalonia's framework is AOT'd, user code is interpreted, demo animations feel sluggish. | High | Low | Document limitation; target "agent verification" and "playground" shapes, not production UIs. |
| R3 | Carbide's `Assembly.Load` accumulates across runs, leaking DOM event handlers. | Certain | High | **Mandatory** collectible `AssemblyLoadContext` adoption before Stage 2 ships (§8.3). Good change for Carbide in general. |
| R4 | XAML source-generator gap forces users to write XAML-in-strings until M12. | High | Medium | Document the runtime-XAML path prominently; provide samples; XamlPlayground already proves this is pleasant enough for playground shapes. |
| R5 | Avalonia version drift breaks the pinned ref-pack. | Medium | Medium | Triple-pin (Carbide, Avalonia, .NET); CI job that rebuilds the bundle against the next Avalonia minor and reports diff; extend `src/Carbide/docs/drift/`. |
| R6 | Cross-iframe `postMessage` protocol mistake causes deadlocks in Sketch B. | Medium | High | Keep the protocol synchronous and explicit: a single "load this PE" message and a single "status" response; no streaming; versioned schema. |
| R7 | COOP/COEP header misconfiguration blocks the runner from loading. | Medium | Medium | Document required headers for multithreaded mode; ship single-threaded as default. |
| R8 | Trim-analyzer errors when bundling Avalonia+Carbide in Sketch A. | Medium | Medium | `TrimmerRootAssembly` list from XamlPlayground is a known-good starting point; adopt it verbatim. |
| R9 | User code pulling a NuGet package that references Avalonia-different-version gets mixed-version load errors. | Low | Medium | Sibling `@carbide-ui/*` packages strongly pin Avalonia version; NuGet resolver (Carbide M6) detects and refuses conflicting Avalonia versions. |
| R10 | Vision non-goal N.2/N.3 tension resurfaces during PR review. | Medium | Low | Codify in a vision §15 companion-projects section before the first merge that touches an Avalonia-named file. |

## 12. What this report deliberately does not answer

- **Whether this should happen at all.** That's an owner call. The report makes the case that *if* it happens, Sketch B (with optional Sketch C) is the cleanest path and that the technical barriers are all surmountable in ≤ 3 weeks.
- **Whether Avalonia is the right GUI framework.** Uno Platform, .NET MAUI Blazor Hybrid, or Blazor WebAssembly with a component kit are all adjacent choices. This report is scoped to Avalonia per the request.
- **Whether a Tauri or Electron path makes more sense for the "desktop+web+agent" matrix.** That is a different question — those stacks do not meet the "no dotnet SDK on the host" constraint that motivated Carbide.
- **Detailed size measurements.** The numbers in §3.2 are order-of-magnitude from published docs and NuGet package sizes; the Stage 1 acceptance test will produce exact numbers, which should then be committed as size-budget gates.

## 13. Appendix — sources and verified references

### Repository-local

- Carbide vision: [`src/Carbide/docs/carbide-vision__2026-04-17__16-16-47-000000.md`](../../carbide-vision__2026-04-17__16-16-47-000000.md)
- Carbide architecture: [`src/Carbide/docs/planning/carbide-architecture-and-implementation-plan__2026-04-17__16-16-47-000000.md`](../../planning/carbide-architecture-and-implementation-plan__2026-04-17__16-16-47-000000.md)
- Carbide M4 plan: [`src/Carbide/docs/planning/milestones/carbide-M4-detailed-plan__2026-04-18__19-45-17-979644.md`](../../planning/milestones/carbide-M4-detailed-plan__2026-04-18__19-45-17-979644.md)
- Carbide M5 plan: [`src/Carbide/docs/planning/milestones/carbide-M5-detailed-plan__2026-04-18__21-23-32-734397.md`](../../planning/milestones/carbide-M5-detailed-plan__2026-04-18__21-23-32-734397.md)
- Feasibility predecessor: [`docs/reports/csharp-build-run-without-dotnet-sdk-feasibility__2026-04-17__01-02-58-000000.md`](../../../../../docs/reports/csharp-build-run-without-dotnet-sdk-feasibility__2026-04-17__01-02-58-000000.md)
- Carbide sources (illustrative): [`src/Carbide/packages/core/src/Carbide.Core.csproj`](../../../packages/core/src/Carbide.Core.csproj), [`src/Carbide/packages/core/src/CompilationInterop.cs`](../../../packages/core/src/CompilationInterop.cs), [`src/Carbide/packages/core/src/Services/ProjectCompiler.cs`](../../../packages/core/src/Services/ProjectCompiler.cs), [`src/Carbide/packages/core/src/Services/ReferenceRegistry.cs`](../../../packages/core/src/Services/ReferenceRegistry.cs), [`src/Carbide/packages/core/src/ts/session.ts`](../../../packages/core/src/ts/session.ts), [`src/Carbide/packages/core/src/ts/runtime/boot.ts`](../../../packages/core/src/ts/runtime/boot.ts).

### External

- Avalonia marketing landing for WASM — <https://avaloniaui.net/avalonia/wasm>
- Avalonia official WebAssembly docs — <https://docs.avaloniaui.net/docs/platform-specific-guides/webassembly>
- Avalonia "How to use WebAssembly" guide — <https://docs.avaloniaui.net/docs/guides/platforms/how-to-use-web-assembly>
- Avalonia Book, Chapter 20 "Browser (WebAssembly) target" — <https://wieslawsoltes.github.io/AvaloniaBook/Chapters/Chapter20.html>
- Avalonia templates (`csharp/xplat/*.Browser`) — <https://github.com/AvaloniaUI/avalonia-dotnet-templates/tree/main/templates/csharp/xplat>
- XamlPlayground (primary precedent) — <https://github.com/AvaloniaUI/XamlPlayground>
- XamlPlayground `CompilerService.cs` (the in-process Roslyn + `Assembly.TryGetRawMetadata` pattern) — <https://github.com/AvaloniaUI/XamlPlayground/blob/main/src/XamlPlayground/Services/CompilerService.cs>
- ControlCatalog.Browser sample (canonical `Program.cs` shape) — <https://github.com/AvaloniaUI/Avalonia/blob/master/samples/ControlCatalog.Browser/Program.cs>
- Avalonia runtime XAML support (DeepWiki) — <https://deepwiki.com/AvaloniaUI/Avalonia/6.2-runtime-xaml-support-and-helpers>
- XamlX (pluggable XAML compiler Avalonia uses) — <https://github.com/kekekeks/XamlX>
- Avalonia WebAssembly discussion threads — <https://github.com/AvaloniaUI/Avalonia/discussions/19412>, <https://github.com/AvaloniaUI/Avalonia/discussions/15665>, <https://github.com/AvaloniaUI/Avalonia/discussions/18207>, <https://github.com/AvaloniaUI/Avalonia/discussions/17057>
- Register blog on Avalonia + MAUI + Linux + WebAssembly (2026-03-23) — <https://www.theregister.com/2026/03/23/maui_linux_avalonia/>
- Avalonia blog: MAUI + Avalonia preview 1 — <https://avaloniaui.net/blog/maui-avalonia-preview-1>
- Rick Strahl, "Runtime C# Code Compilation Revisited for Roslyn" — <https://weblog.west-wind.com/posts/2022/Jun/07/Runtime-C-Code-Compilation-Revisited-for-Roslyn>
- Laurent Kempé, "Dynamically compile and run code using .NET Core 3.0" — <https://laurentkempe.com/2019/02/18/dynamically-compile-and-run-code-using-dotNET-Core-3.0/>

## 14. Document change control

This is a feasibility report, not a specification. Subsequent updates should:

- **Preserve the sketch-level comparison** in §7 — additions belong as new sketches, not as edits that blur the existing three.
- **Keep the recommendation explicit** — if new data changes the call, update §9 with a dated decision line rather than silently changing the conclusion.
- **Re-run the size numbers** in §3.2 once a Stage-1 artefact exists; replace order-of-magnitude estimates with measured bytes.
- **Track upstream drift** — Avalonia major versions, .NET major versions, and Carbide milestones each invalidate some specific section; annotate invalidated sections rather than rewriting them.
- **Maintain citation discipline** — every external claim stays linkable.
