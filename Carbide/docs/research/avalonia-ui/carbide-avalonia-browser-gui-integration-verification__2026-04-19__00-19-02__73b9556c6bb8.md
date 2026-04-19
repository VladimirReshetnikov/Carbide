# Verification: `carbide-avalonia-browser-gui-integration`

- Created (UTC): 2026-04-19T00:19:02Z
- Repository HEAD: d2f6eb2b29127011a7f7d713607bdfb4861c2b5f

Status: verification / independent-research report.
Audience: repository owner and future Carbide contributors.
Scope: verifies the claims and recommendation in [the original feasibility report](./carbide-avalonia-browser-gui-integration__2026-04-18__21-52-50-185670__57c69d8c45e3.md) against the current repository `HEAD`, current upstream Avalonia sources, current XamlPlayground sources, current NuGet package layouts, and local publish measurements.

## Summary

The original report gets the big picture right: integrating Carbide with Avalonia for browser-hosted GUI execution is technically feasible, and `AvaloniaUI/XamlPlayground` is a strong public proof point.

However, several important details are wrong, overstated, or now stale:

- The report's Carbide status snapshot is historical. It was written against repository `HEAD` `0b929aad1eef7e0307cede8e6fb6b4dd1468b1d3`; current `HEAD` is `d2f6eb2b29127011a7f7d713607bdfb4861c2b5f`, and current [`src/Carbide/README.md`](../../../README.md) says M1-M6 functionality is present, not "M4 shipped, M5 in progress."
- `Avalonia.Browser` is not the right place to extract a compile-time ref-pack from. In Avalonia 12.0.1 the package has no `ref/*/*.dll` entries at all. The main `Avalonia` package supplies the `ref/net10.0/*.dll` surface.
- The size estimates in the original report are not supported by present measurements. A minimal Release `net10.0-browser` Avalonia app published locally produced 17.07 MiB of original `_framework` assets and 4.69 MiB of Brotli-compressed `_framework` assets. Current Carbide Release publish produced 61.05 MiB of original `_framework` assets. A naive additive lower bound is therefore about 78.12 MiB original, not 140-200 MiB.
- The original recommendation to prefer Sketch B over Sketch A is only partly supported after those corrections. Keeping GUI support out of `@carbide/core` still makes sense. Preferring the iframe shape specifically is now a product/isolation choice, not something the measured size data forces.

## Method

This verification used four evidence sources:

1. Current repository state at `d2f6eb2b29127011a7f7d713607bdfb4861c2b5f`, including current Carbide docs and source.
2. Current upstream Avalonia source, templates, and API/docs surfaces.
3. Current upstream XamlPlayground source.
4. Local measurements:
   - `dotnet publish -c Release src/Carbide/packages/core/src/Carbide.Core.csproj`
   - `dotnet publish -c Release` of a minimal temporary `net10.0-browser` Avalonia 12.0.1 app
   - direct inspection of `Avalonia`, `Avalonia.Browser`, `Avalonia.Skia`, and `Avalonia.HarfBuzz` NuGet packages

The goal here is not to restate the original report. The goal is to independently confirm or correct it.

## Claim Verification

| Claim from the original report | Verdict | Notes |
|---|---|---|
| Carbide and Avalonia.Browser sit on the same .NET browser-WASM substrate and are therefore architecturally compatible. | Confirmed | Carbide uses `Microsoft.NET.Sdk.WebAssembly` with `RuntimeIdentifier=browser-wasm` in [`Carbide.Core.csproj`](../../../packages/core/src/Carbide.Core.csproj). Current Avalonia browser templates also use `Microsoft.NET.Sdk.WebAssembly`, and current `Avalonia.Browser` targets `net10.0` and `net10.0-browser1.0`. |
| XamlPlayground is a live precedent for in-browser Roslyn + Avalonia + dynamic assembly loading. | Confirmed | `XamlPlayground.Browser.csproj` targets `net10.0-browser`, references `Avalonia.Browser`, and roots Avalonia/XAML loader assemblies. `CompilerService.cs` loads metadata from already-loaded assemblies and loads emitted assemblies into a collectible `AssemblyLoadContext`. |
| XamlPlayground also demonstrates runtime XAML loading, which sidesteps build-time `.axaml` compilation. | Confirmed | `MainViewModel.cs` in XamlPlayground calls `AvaloniaRuntimeXamlLoader.Load(stream, scriptAssembly, rootInstance)` and `AvaloniaRuntimeXamlLoader.Parse<Control?>(xaml, null)`. |
| Avalonia browser apps mount into a caller-provided HTML element such as `out`. | Confirmed | Current Avalonia templates call `.StartBrowserAppAsync("out")`; `BrowserAppBuilder.StartBrowserAppAsync` creates `new AvaloniaView(mainDivId)`; `AvaloniaView` resolves the DOM element by id. |
| Avalonia browser rendering is Skia-backed and prefers WebGL2, then WebGL1, then software. | Confirmed | `BrowserAppBuilder.UseBrowser()` wires `UseSkia()` and `UseHarfBuzz()`. `BrowserRenderingMode` defaults to `WebGL2`, `WebGL1`, `Software2D`. `dom.ts` creates a canvas host lazily for the selected rendering mode. |
| The browser lifetime is single-view rather than desktop-window oriented. | Confirmed, with a precision fix | `BrowserSingleViewLifetime` implements both `ISingleViewApplicationLifetime` and `ISingleTopLevelApplicationLifetime`. The original report's "single-view" description is directionally right, but the actual lifetime surface is a little broader than it implied. |
| Carbide already has the right local seams for references, multi-file compilation, PE/PDB emission, and runtime resolution. | Confirmed | Current source still supports this claim: `ReferenceRegistry`, `session.addReference(bytes)`, path-keyed multi-document source management, `BuildAsync` PE/PDB emission, and `AppDomain.CurrentDomain.AssemblyResolve` in `RunAsync`. |
| Carbide lacks per-run isolation and collectible unloading, which becomes correctness-critical for GUI reruns. | Confirmed | Current `ProjectCompiler.RunAsync` still uses `Assembly.Load(byte[])` into the default context and does not use `AssemblyLoadContext.Unload()`. The original report's concern here remains valid. |
| Runtime XAML loading is a viable short-term path while Carbide lacks Avalonia's build-time XAML pipeline. | Confirmed | Avalonia exposes `AvaloniaRuntimeXamlLoader.Load/Parse` as a public runtime API. Separately, Avalonia's build-time XAML path clearly goes through `Avalonia.Build.Tasks` and XamlX-based compiler extensions, which Carbide does not host today. |
| A compile-time Avalonia ref-pack can be built by extracting `ref/net10.0-browser/*.dll` from `Avalonia.Browser.nupkg`. | Incorrect | `Avalonia.Browser` 12.0.1 has no `ref/*/*.dll` entries. It ships `lib/net10.0*/*.dll`, build targets, and static web assets. The compile-time ref surface lives primarily in `Avalonia` 12.0.1 `ref/net10.0/*.dll` instead. |
| Browser clipboard support is basically text-only. | Not confirmed; current source is broader | Current `ClipboardImpl` supports text, bitmap, and `byte[]` clipboard items when the browser format is supported, and explicitly does not support arbitrary file clipboard items. The original report's "text only" statement is too narrow. |
| Avalonia's browser file-picker path uses the browser file-system APIs with a polyfill option. | Confirmed | `BrowserStorageProvider` and `BrowserPlatformOptions.PreferFileDialogPolyfill` explicitly implement this model. |
| NativeAOT is incompatible with the dynamic runtime-loading shape Carbide uses for user code. | Confirmed in substance | Microsoft Learn's Native AOT docs explicitly say there is no dynamic loading. Since Carbide loads emitted user assemblies dynamically, that path does not compose with NativeAOT for user code. |
| The specific size numbers in the original report are a reasonable order-of-magnitude estimate. | Not supported by current measurement | Current measurements do not support the report's 40-55 MiB compressed / 140-200 MiB uncompressed estimate as the likely order of magnitude for an integration package. |
| Sketch B is the best first interactive integration shape. | Partially supported | The "keep GUI out of `@carbide/core`" part still stands. The more specific preference for Sketch B over Sketch A is weaker once the package-layout and size claims are corrected. |

## Packaging And Size Corrections

### 1. NuGet package layout

Current NuGet package inspection produced the following facts:

| Package | Package size | `ref/*/*.dll` count | `ref` DLL bytes | `lib/net10.0*/*.dll` count | `lib` DLL bytes |
|---|---:|---:|---:|---:|---:|
| `Avalonia.Browser` 12.0.1 | 0.28 MiB | 0 | 0 | 2 | 436,224 |
| `Avalonia` 12.0.1 | 9.33 MiB | 11 | 4,475,392 | many | large |
| `Avalonia.Skia` 12.0.1 | 0.15 MiB | 0 | 0 | 1 | 123,392 |
| `Avalonia.HarfBuzz` 12.0.1 | 0.04 MiB | 0 | 0 | 1 | 22,016 |

This matters because the original report's proposed `@carbide-ui/refs-avalonia` packaging step was described as "extract `Avalonia.Browser.nupkg`'s `ref/net10.0-browser/*.dll` and deps." That exact package layout does not exist in current Avalonia 12.0.1.

The corrected packaging implication is:

- a Carbide-side Avalonia compile-time ref-pack would need to be assembled from the main `Avalonia` package plus any other compile-time assemblies actually required by the chosen UI sample shape;
- the browser package itself contributes runtime/browser-host glue and static assets, not the main compile-time ref surface.

### 2. Local publish measurements

Local publish measurements produced the following numbers:

| Artifact | Measurement | Notes |
|---|---:|---|
| Current Carbide Release `wwwroot/_framework` | 61.05 MiB original assets | Current Carbide publish has compression disabled, so there are no `.br` or `.gz` assets in this output. |
| Minimal Avalonia 12.0.1 Release `wwwroot/_framework` | 17.07 MiB original assets | This was a small browser app with `Avalonia`, `Avalonia.Browser`, `Avalonia.Fonts.Inter`, and `Avalonia.Themes.Fluent`. |
| Minimal Avalonia 12.0.1 Release `wwwroot/_framework` Brotli | 4.69 MiB | This is a lower bound for a browser-only Avalonia host, not a full Carbide integration. |
| Naive additive lower bound | 78.12 MiB original assets | `61.05 + 17.07`. This is not a real merged publish, but it is enough to show that the original report's specific 140-200 MiB uncompressed estimate is not currently supported. |

Important caveats:

- the Avalonia measurement is a minimal host, not a Roslyn-hosting playground;
- the Carbide measurement is current untrimmed Carbide, not a future merged package;
- a real integration would have overlap and duplication effects that this simple addition does not model;
- compile-time ref-pack shipping is separate from runtime publish output.

So the measured numbers do **not** prove that a merged package would be 78 MiB. They **do** show that the original report's specific high-end estimate should not be repeated as if it were established fact.

## Current-State Drift In The Original Report

Because the original report is pinned to repository `HEAD` `0b929aad1eef7e0307cede8e6fb6b4dd1468b1d3`, several of its "today" statements are no longer current-state statements:

- current [`src/Carbide/README.md`](../../../README.md) reports M1-M6 functionality, not just M4 shipped / M5 in flight;
- current TS and C# surfaces still match many of the report's identified integration seams, but the milestone framing in the report should now be read as historical context;
- any future reader should treat that report as a good feasibility snapshot, not as the current Carbide status document.

## Revised Conclusion

### What still stands

- The core feasibility conclusion stands.
- XamlPlayground is not just suggestive precedent; it is direct evidence that Avalonia browser hosting, in-process Roslyn compilation, collectible dynamic loading, and runtime XAML loading can coexist in one WebAssembly application.
- Carbide still has the right local seams for a future integration: reference injection, multi-document sources, build output, and runtime reference resolution.
- Keeping GUI support outside `@carbide/core` still makes architectural sense and remains aligned with Carbide's stated non-goals.

### What changes

- The compile-time packaging story is harder than the original report described, because `Avalonia.Browser` is not itself a ref-pack source.
- The size-pressure argument is materially weaker than the original report claimed. Size is still a concern, but the specific numbers in the original report are too high to rely on.
- Because of that, the original recommendation to prefer Sketch B over Sketch A is no longer strongly supported by the evidence gathered here.

### Updated recommendation

If the goal is an **interactive browser preview** for Avalonia code:

1. Keep it in a **sibling package family**, not in `@carbide/core`. That part of the original report remains sound.
2. Treat **Sketch A (sibling in-process package)** and **Sketch B (separate iframe runner)** as live options.
3. Prefer **Sketch A first** if the main priority is the shortest technically direct prototype:
   - one runtime instead of two;
   - no cross-frame `postMessage` protocol;
   - no duplicated runtime startup and memory footprint.
4. Prefer **Sketch B** only if preview isolation is itself a product requirement:
   - separate crash boundary;
   - easier multiple independent previews;
   - stricter separation between compiler state and UI state.
5. Keep **Sketch C** as the cleanest publish-only path for offline/static-host workflows.

In other words: the original report's **scope-separation conclusion** still holds, but its **specific A-vs-B recommendation** should be weakened and probably revised.

### Delivery-estimate note

The original report's 1-3 week delivery estimates should be treated as planning guesses, not verified facts. The corrected package-layout findings make the first-stage packaging work look more involved than the original report suggested.

## Sources

### Repository-local

- Original report: [carbide-avalonia-browser-gui-integration__2026-04-18__21-52-50-185670__57c69d8c45e3.md](./carbide-avalonia-browser-gui-integration__2026-04-18__21-52-50-185670__57c69d8c45e3.md)
- Current Carbide README: [`src/Carbide/README.md`](../../../README.md)
- Carbide core project file: [`src/Carbide/packages/core/src/Carbide.Core.csproj`](../../../packages/core/src/Carbide.Core.csproj)
- Carbide interop surface: [`src/Carbide/packages/core/src/CompilationInterop.cs`](../../../packages/core/src/CompilationInterop.cs)
- Carbide runtime/compiler implementation: [`src/Carbide/packages/core/src/Services/ProjectCompiler.cs`](../../../packages/core/src/Services/ProjectCompiler.cs)
- Carbide reference registry: [`src/Carbide/packages/core/src/Services/ReferenceRegistry.cs`](../../../packages/core/src/Services/ReferenceRegistry.cs)
- Carbide TS session surface: [`src/Carbide/packages/core/src/ts/session.ts`](../../../packages/core/src/ts/session.ts)
- Carbide Node asset server: [`src/Carbide/packages/core/src/ts/host/node/asset-server.ts`](../../../packages/core/src/ts/host/node/asset-server.ts)

### External

- Avalonia WebAssembly guide: [docs.avaloniaui.net/docs/platform-specific-guides/webassembly](https://docs.avaloniaui.net/docs/platform-specific-guides/webassembly)
- Avalonia browser template project: [AvaloniaTest.Browser.csproj](https://github.com/AvaloniaUI/avalonia-dotnet-templates/blob/main/templates/csharp/xplat/AvaloniaTest.Browser/AvaloniaTest.Browser.csproj)
- Avalonia browser template `Program.cs`: [Program.cs](https://github.com/AvaloniaUI/avalonia-dotnet-templates/blob/main/templates/csharp/xplat/AvaloniaTest.Browser/Program.cs)
- Avalonia browser template `index.html`: [index.html](https://github.com/AvaloniaUI/avalonia-dotnet-templates/blob/main/templates/csharp/xplat/AvaloniaTest.Browser/wwwroot/index.html)
- Avalonia browser template `main.js`: [main.js](https://github.com/AvaloniaUI/avalonia-dotnet-templates/blob/main/templates/csharp/xplat/AvaloniaTest.Browser/wwwroot/main.js)
- Avalonia browser runtime source: [BrowserAppBuilder.cs](https://github.com/AvaloniaUI/Avalonia/blob/master/src/Browser/Avalonia.Browser/BrowserAppBuilder.cs)
- Avalonia browser lifetime source: [BrowserSingleViewLifetime.cs](https://github.com/AvaloniaUI/Avalonia/blob/master/src/Browser/Avalonia.Browser/BrowserSingleViewLifetime.cs)
- Avalonia browser host source: [AvaloniaView.cs](https://github.com/AvaloniaUI/Avalonia/blob/master/src/Browser/Avalonia.Browser/AvaloniaView.cs)
- Avalonia browser DOM module: [dom.ts](https://github.com/AvaloniaUI/Avalonia/blob/master/src/Browser/Avalonia.Browser/webapp/modules/avalonia/dom.ts)
- Avalonia runtime XAML loader: [AvaloniaRuntimeXamlLoader.cs](https://github.com/AvaloniaUI/Avalonia/blob/master/src/Markup/Avalonia.Markup.Xaml.Loader/AvaloniaRuntimeXamlLoader.cs)
- Avalonia build-task/XamlX pipeline root: [Avalonia.Build.Tasks.csproj](https://github.com/AvaloniaUI/Avalonia/blob/master/src/Avalonia.Build.Tasks/Avalonia.Build.Tasks.csproj)
- ControlCatalog browser sample: [ControlCatalog.Browser/Program.cs](https://github.com/AvaloniaUI/Avalonia/blob/master/samples/ControlCatalog.Browser/Program.cs)
- XamlPlayground browser host: [XamlPlayground.Browser.csproj](https://github.com/AvaloniaUI/XamlPlayground/blob/main/src/XamlPlayground.Browser/XamlPlayground.Browser.csproj)
- XamlPlayground core project: [XamlPlayground.csproj](https://github.com/AvaloniaUI/XamlPlayground/blob/main/src/XamlPlayground/XamlPlayground.csproj)
- XamlPlayground compile service: [CompilerService.cs](https://github.com/AvaloniaUI/XamlPlayground/blob/main/src/XamlPlayground/Services/CompilerService.cs)
- XamlPlayground runtime XAML use site: [MainViewModel.cs](https://github.com/AvaloniaUI/XamlPlayground/blob/main/src/XamlPlayground/ViewModels/MainViewModel.cs)
- NuGet package page: [Avalonia.Browser 12.0.1](https://www.nuget.org/packages/Avalonia.Browser/)
- Microsoft Learn: [How to use and debug assembly unloadability in .NET](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability)
- Microsoft Learn: [About AssemblyLoadContext](https://learn.microsoft.com/en-us/dotnet/core/dependency-loading/understanding-assemblyloadcontext)
- Microsoft Learn: [Native AOT deployment overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- Microsoft Learn API reference: [AssemblyExtensions](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.assemblyextensions?view=net-9.0)
- Microsoft Learn API reference: [WebAssemblyComponentsEndpointOptions](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.components.webassembly.server.webassemblycomponentsendpointoptions?view=aspnetcore-8.0)

## Change-Control Note

This document is a verification report, not a new feasibility proposal. If follow-up work revises the A-vs-B recommendation again, that later document should keep this report's corrections explicit rather than silently repeating the original size or package-layout claims.
