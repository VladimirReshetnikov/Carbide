# Independent verification of the Carbide WASM JS interop libraries survey

- Created (UTC): 2026-04-19T00:10:32Z
- Repository HEAD: d2f6eb2b29127011a7f7d713607bdfb4861c2b5f

Status: independent verification / source-audit report. This document cross-checks the claims and recommendations in the existing [Carbide WASM JS interop libraries survey](./carbide-wasm-js-interop-libraries-survey__2026-04-18__21-43-55-000000__b27d950cd3b9.md) against current Carbide source code plus primary external sources.

Audience: repository owner and future Carbide maintainers deciding how much JS<->CLR interop abstraction to build on top of Carbide's current browser-WASM runtime.

Scope: the same topics as the source survey: Carbide's current boundary, .NET's in-box `[JSImport]` / `[JSExport]` stack, `Microsoft.JSInterop`, Bootsharp, ClearScript as the ergonomics benchmark, `componentize-dotnet` + `jco`, NativeAOT-LLVM, Uno's interop layer, and the "wrong-direction" comparison set.

Method: local code inspection in `src/Carbide/` and `lib/dotnet/runtime/`, plus independent web / GitHub / npm lookups performed on 2026-04-18 and 2026-04-19 UTC. Where a conclusion is an inference rather than an explicit statement from a source, I say so.

## 1. Executive verdict

The main thesis of the source survey survives verification.

- Carbide is already built on the right low-level primitive: a thin browser-WASM runtime boot plus `[JSExport]` methods over `getAssemblyExports(...)`.
- I found no credible evidence of a current browser-WASM library that gives JavaScript ClearScript-style live access to CLR objects with natural property/indexer/method projection.
- Bootsharp is still the strongest current candidate for a higher-level user-code bridge on top of the raw runtime, but it should be treated as a spike candidate rather than a proven drop-in fit for Carbide's in-browser compilation model.
- Keeping Carbide's own infrastructure boundary on raw `[JSImport]` / `[JSExport]` remains the right recommendation.
- `componentize-dotnet` + `jco` still look like a "watch this space" direction, not today's direct answer to Carbide's browser JS interop problem.

What changed in this verification is mostly tightening:

- One local Carbide citation in the source survey is stale: the runtime entry is `ts/runtime/boot.ts`, not `ts/runtime/index.ts`.
- A few statements in the source survey are stronger than the primary sources support. The strongest version of the evidence is "I found no documented/proven library that offers ClearScript-like live CLR object projection in browser-WASM JS interop," not a formal impossibility proof.
- Several implementation-detail claims in the source survey are still plausible but were not independently established here, especially effort estimates and some of the finer-grained ClearScript / component-model comparisons.

## 2. Claim matrix

| Claim from the source survey | Verdict | Verification note |
|---|---|---|
| Carbide currently exposes a thin string / base64 `[JSExport]` boundary over `getAssemblyExports(...)`. | Verified | Confirmed in [`src/Carbide/packages/core/src/CompilationInterop.cs`](../../../packages/core/src/CompilationInterop.cs), [`src/Carbide/packages/core/src/ts/runtime/boot.ts`](../../../packages/core/src/ts/runtime/boot.ts), [`src/Carbide/packages/core/src/ts/runtime/dotnet-types.ts`](../../../packages/core/src/ts/runtime/dotnet-types.ts), and [`src/Carbide/packages/core/src/ts/session.ts`](../../../packages/core/src/ts/session.ts). |
| Carbide already stands on the right primitive foundation: Mono/WebAssembly Browser Tools + native `[JSExport]`. | Verified | Carbide's current architecture is exactly this. Nothing in the independent research suggests replacing the primitive layer. |
| `Microsoft.JSInterop` and native `[JSImport]` / `[JSExport]` are distinct interop stacks. | Verified | Official docs and `aspnetcore` discussion #53866 support the separation. |
| `DotNetObjectReference` does not work with `[JSImport]`. | Verified | Explicitly stated by `aspnetcore` collaborator Javier Calvarro Nelson in discussion #53866. |
| No current library reaches ClearScript-level live CLR object exposure on the JS side. | Mostly verified | I found no evidence of such a library. Bootsharp documents interface-based instance binding, not live property/indexer projection. This remains a strongest-supported market conclusion, not a mathematical proof of absence. |
| Bootsharp is the closest current candidate for a higher-level bridge. | Verified, with caveats | Active, current release, browser + Node support, generated TS declarations, interface-based instance binding. Caveat: Bootsharp assumes a publish/build-driven workflow that Carbide has not yet proven compatible with its in-browser dynamic compilation model. |
| Carbide should keep its own infrastructure boundary on raw `[JSImport]` / `[JSExport]`. | Verified as a sound recommendation | Fits Carbide's existing narrow payload shape and avoids depending on a higher-level library for internal control-plane operations. |
| `componentize-dotnet` + `jco` are worth tracking long-term, but not a direct answer today. | Directionally verified | Both projects are active, but their center of gravity is WIT / components / WASI portability, not today's browser JS interop ergonomics. |
| A custom proxy/source-generator layer is likely a 4-8 week effort. | Not independently verified | This may be a reasonable estimate, but I found no source that could validate it; it should be treated as planning judgment, not fact. |

## 3. Local Carbide verification

The local Carbide references in the source survey were mostly accurate, and the current implementation is indeed thin and explicit.

- [`src/Carbide/packages/core/src/Carbide.Core.csproj`](../../../packages/core/src/Carbide.Core.csproj) uses `Microsoft.NET.Sdk.WebAssembly` with `RuntimeIdentifier=browser-wasm`.
- [`src/Carbide/packages/core/src/ts/runtime/boot.ts`](../../../packages/core/src/ts/runtime/boot.ts) dynamically imports `dotnet.js`, creates the runtime, reads `mainAssemblyName`, gets assembly exports, and calls `interop.InitAsync(...)`.
- [`src/Carbide/packages/core/src/CompilationInterop.cs`](../../../packages/core/src/CompilationInterop.cs) exports the current control plane as static `[JSExport]` methods. The public boundary is JSON strings and base64 strings, not live object graphs.
- [`src/Carbide/packages/core/src/ts/runtime/dotnet-types.ts`](../../../packages/core/src/ts/runtime/dotnet-types.ts) types the exported surface as string-returning / `Promise<string>` functions.
- [`src/Carbide/packages/core/src/ts/session.ts`](../../../packages/core/src/ts/session.ts) serializes session creation payloads as JSON and binary references as base64.

One correction matters:

- The source survey references `src/Carbide/packages/core/src/ts/runtime/index.ts`. That file is not the current runtime entry. The relevant files are `ts/runtime/boot.ts` and `ts/runtime/dotnet-types.ts`.

That correction does not change the survey's conclusion; it only updates the local citation.

## 4. Topic-by-topic verification

### 4.1 Native `[JSImport]` / `[JSExport]`

Official Microsoft docs still describe the native `[JSImport]` / `[JSExport]` model as the browser-WASM interop mechanism for running .NET from JavaScript without depending on Blazor. The canonical browser app flow remains `dotnet.create()` / `setModuleImports(...)` / `getAssemblyExports(...)` / `run()` or `runMain(...)`. See:

- [JavaScript `[JSImport]` / `[JSExport]` interop with a WebAssembly Browser App project](https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/wasm-browser-app?view=aspnetcore-10.0)
- [JavaScript `[JSImport]` / `[JSExport]` interop in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/?view=aspnetcore-10.0)

The runtime sources vendored in this repository support the survey's "primitive-plus-handles" characterization:

- [`lib/dotnet/runtime/src/libraries/System.Runtime.InteropServices.JavaScript/src/System/Runtime/InteropServices/JavaScript/JSMarshalerType.cs`](../../../../../lib/dotnet/runtime/src/libraries/System.Runtime.InteropServices.JavaScript/src/System/Runtime/InteropServices/JavaScript/JSMarshalerType.cs) documents `JSMarshalerType.Object` as mapping to a "ManagedObject proxy on JavaScript side".
- [`lib/dotnet/runtime/src/libraries/System.Runtime.InteropServices.JavaScript/src/System/Runtime/InteropServices/JavaScript/Marshaling/JSMarshalerArgument.Object.cs`](../../../../../lib/dotnet/runtime/src/libraries/System.Runtime.InteropServices.JavaScript/src/System/Runtime/InteropServices/JavaScript/Marshaling/JSMarshalerArgument.Object.cs) shows that arbitrary managed-object fallback is GCHandle-based and rejects several shapes in dynamic `object` marshalling.
- [`lib/dotnet/runtime/src/libraries/System.Runtime.InteropServices.JavaScript/src/System/Runtime/InteropServices/JavaScript/JSException.cs`](../../../../../lib/dotnet/runtime/src/libraries/System.Runtime.InteropServices.JavaScript/src/System/Runtime/InteropServices/JavaScript/JSException.cs) confirms that `JSException.StackTrace` augments the managed stack with the JS `stack` string when available.

The source survey's broad conclusion is therefore correct: raw native interop is explicit, low-level, and good at primitives, tasks, functions, and handles.

One tightening is important. The source survey states, in effect, that `JSType.Any` / managed-object marshalling yields an opaque JS-side object with no property access. The primary sources justify the weaker version of that statement:

- the runtime clearly distinguishes "managed object proxy" / handle-based marshalling from rich member projection; and
- I found no public documentation or library evidence for ClearScript-style automatic property/indexer exposure over that path.

That last sentence is an inference from the available evidence, not an explicit Microsoft statement.

### 4.2 `Microsoft.JSInterop`

The source survey's separation between native browser-WASM interop and the Blazor-oriented `Microsoft.JSInterop` stack is verified.

- [`IJSObjectReference`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.jsinterop.ijsobjectreference?view=aspnetcore-10.0) exposes async invocation plus explicit property get/set/constructor calls on JavaScript objects from .NET.
- [`DotNetObjectReference<TValue>`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.jsinterop.dotnetobjectreference-1?view=aspnetcore-10.0) is documented as wrapping a .NET object so it is passed by reference rather than JSON serialization, with explicit disposal requirements.
- In [aspnetcore discussion #53866](https://github.com/dotnet/aspnetcore/discussions/53866), Javier Calvarro Nelson states directly: "You can't use `DotNetObjectReference` with the JSImport API. That's the native webassembly specific interop mechanism, not the Blazor one."

That is enough to support the survey's main point: `Microsoft.JSInterop` is a different abstraction layer with different trade-offs, and it does not collapse the distinction between Blazor interop and native `[JSImport]` / `[JSExport]`.

For completeness, the npm companion package is current and active:

- `npm view @microsoft/dotnet-js-interop version time --json` reported version `10.0.0`, published on 2025-11-14T22:43:57Z.

I did not find anything here that changes the Carbide recommendation.

### 4.3 Bootsharp

Bootsharp remains the strongest confirmation of the source survey's central recommendation.

Independent checks:

- `gh api repos/elringus/bootsharp --jq "{full_name,pushed_at,updated_at,default_branch,stargazers_count}"` reported an actively updated repository (`pushed_at` 2026-04-18T15:39:11Z, 759 stars at lookup time).
- `gh api repos/elringus/bootsharp/releases/latest --jq "{tag_name,published_at,name}"` reported current release `v0.7.0`, published 2026-02-10T18:01:22Z.
- `gh api repos/elringus/bootsharp/contributors --jq '.[0:5] | map({login, contributions})'` showed a very concentrated maintainer profile, which is relevant as a risk note but not a blocker by itself.

The docs materially support the survey's feature claims:

- [Getting Started](https://bootsharp.com/guide/getting-started) documents a generated `bin/bootsharp` package containing `types/`, `index.mjs`, and `package.json`, and shows browser plus Node/Deno/Bun consumption.
- [Type Declarations](https://bootsharp.com/guide/declarations) documents automatic TS declaration generation and type crawling across records/interfaces used in interop signatures.
- [Interop Instances](https://bootsharp.com/guide/interop-instances) documents that interface arguments / return values become instance bindings instead of value serialization, with explicit limitations.
- [Sideloading Binaries](https://bootsharp.com/guide/sideloading) confirms that the default build embeds DLLs and the .NET WASM runtime into the generated JS module, but also exposes `BootsharpEmbedBinaries=false` and sideloading as an alternative. The docs explicitly call out about 30% size overhead from base64 embedding.

That supports the source survey's practical conclusion: Bootsharp is the nearest thing in today's ecosystem to a higher-level JS-facing bridge over .NET WASM.

However, two caveats matter:

1. Bootsharp's documented model is interface-oriented instance binding, not ClearScript-style arbitrary live CLR object projection. The example and limitations are method-shaped. I found no evidence of generated ES `Proxy`-style property/indexer bridging over arbitrary CLR objects.
2. The public docs have visible version drift. The site header says `v0.7.0`, but the [NativeAOT-LLVM page](https://bootsharp.com/guide/llvm) already says "Starting with Bootsharp 0.8.0 no extra project configuration is required." This means version-specific claims should be pinned to the release tag or release notes, not inferred from the live docs site alone.

So the source survey's recommendation "Bootsharp is the closest candidate" is verified. The stronger statement "Bootsharp is ready to adopt for Carbide user code" is still a hypothesis that needs a concrete integration spike, because Bootsharp's documented flow is publish/build driven while Carbide compiles user code inside an already-running WASM sandbox.

### 4.4 ClearScript as the benchmark

The source survey is on solid ground in using ClearScript as the ergonomics bar.

The ClearScript FAQ explicitly documents:

- [`AddHostObject` / `AddHostType`](https://clearscript.clearfoundry.net/Tutorial/FAQtorial) for exposing host values and types;
- direct access to public members (`uri.Query`, `uri.ToString()`, `Console.Title = ...`);
- generic-method usage by passing host types as explicit generic arguments (`Enumerable.Empty(Int32)`);
- indexer-style array / `IList` access (`uriArray[0]`);
- delegate / callback interop (`new TimerCallback(function (...) { ... })`);
- host-to-script direct access through `engine.Script`.

That is enough to validate ClearScript as the comparison target: it really does represent the "natural host object in script" experience the source survey is measuring against.

I would, however, correct one detail from the source survey's capability table. The table uses `host.Method[Int32](x)` as the generic-method example. The ClearScript FAQ documents explicit generic arguments differently: by supplying host types as arguments, for example `Enumerable.Empty(Int32)`. The high-level point stands, but the exact example syntax in the source survey should be updated.

I did not independently re-verify some finer-grained ClearScript details from the source survey, such as any implied performance numbers or all of the corner-case behavioral comparisons. Those should be treated as illustrative unless re-sourced.

### 4.5 `componentize-dotnet`, `jco`, and NativeAOT-LLVM

The source survey's long-term watch recommendation is directionally correct.

Independent checks:

- `gh api repos/bytecodealliance/componentize-dotnet --jq "{full_name,pushed_at,updated_at,default_branch}"` showed an active repository.
- The [`componentize-dotnet` README](https://github.com/bytecodealliance/componentize-dotnet) says the project exists to simplify C# Wasm components, specifically WASI 0.2 components with WIT imports/exports, and says the build output is "fully AOT compiled".
- The same README requires a ".NET 10+ preview SDK" and centers the workflow on WIT files and component composition.
- `gh api repos/bytecodealliance/jco/releases/latest --jq "{tag_name,published_at,name}"` reported `jco-v1.18.0`, published 2026-04-18T10:40:21Z.
- The [`jco` README](https://github.com/bytecodealliance/jco) describes it as "JavaScript tooling for WebAssembly Components" and explicitly highlights transpiling components into ES modules for Node and browsers.
- The [NativeAOT-LLVM compiling guide](https://github.com/dotnet/runtimelab/blob/feature/NativeAOT-LLVM/docs/using-nativeaot/compiling.md) still documents an experimental toolchain with manual setup friction.

That all supports the strategic conclusion:

- this line of work is real and active;
- it matters for future portability and packaging;
- it is not the same problem as "give JavaScript a ClearScript-like view over live CLR objects inside today's browser-WASM Carbide runtime".

I did not independently validate several more specific claims from the source survey in this area, especially around async limitations or the exact shape of resource/member ergonomics across the component boundary. Those should be treated as informed hypotheses unless re-sourced directly from component-model and toolchain docs.

### 4.6 Uno, Edge.js, and the "not actually the same problem" set

The source survey's classification of the comparison set is broadly correct.

- [Uno's JS interop page](https://platform.uno/docs/articles/external/uno.wasm.bootstrap/doc/features-interop.html) explicitly presents two techniques: generated .NET 7+ interop features (`[JSImport]` / `[JSExport]`) and legacy `InvokeJS` / `mono_bind_static_method`. That supports the source survey's view that Uno is wrapping the native substrate, not introducing a new general-purpose CLR-object projection model.
- The [`edge-js` README](https://github.com/agracio/edge-js) describes Edge.js as ".NET and Node.js in-process" and centers on CLR/V8 interop inside Node-hosted processes. That confirms the source survey's "wrong topology" classification for Carbide's browser-WASM scenario.
- The source survey's dismissal of transpilers such as Fable / Bolero / Bridge.NET as "not runtime bridging solutions" is directionally sound and did not show any obvious factual problem in this verification pass.

## 5. What should be updated in the source survey

If the original survey is kept as a design document, I would update these points:

- Replace the stale local path `src/Carbide/packages/core/src/ts/runtime/index.ts` with [`boot.ts`](../../../packages/core/src/ts/runtime/boot.ts) and [`dotnet-types.ts`](../../../packages/core/src/ts/runtime/dotnet-types.ts).
- Soften any absolute wording around `JSType.Any` / managed-object opacity to: "I found no documented ClearScript-like member projection over this path."
- Pin Bootsharp claims to release-tagged docs or release metadata where possible, because the live docs site currently mixes `v0.7.0` labeling with at least one `0.8.0` behavior note.
- Correct the ClearScript generic-method example syntax.
- Mark planning estimates such as "~60%" coverage or "4-8 weeks" implementation effort as engineering judgment rather than source-backed fact.

## 6. Final verified conclusion

No independent research I performed changes the practical recommendation for Carbide.

Keep Carbide's infrastructure boundary on raw native `[JSImport]` / `[JSExport]` plus explicit JSON / binary payloads. That matches Carbide's current architecture, keeps the control plane obvious, and avoids introducing another abstraction where Carbide does not need one.

If Carbide wants a higher-level user-code bridge for developers writing code against the runtime, Bootsharp remains the best current candidate to spike. It is active, it materially improves ergonomics, and it gets closer to the desired "JavaScript sees C# APIs as a module" story than anything else I found. But it still does not close the entire ClearScript gap, and there is still no verified evidence of a browser-WASM library that gives JavaScript live, natural, property-addressable access to arbitrary CLR objects the way ClearScript gives embedded script code inside a .NET host.

`componentize-dotnet` + `jco` remain worth tracking for the longer-term component-model future, but they are not a direct substitute for Carbide's present browser interop layer.

## 7. Sources and reproduction notes

Local source files reviewed:

- [`src/Carbide/packages/core/src/Carbide.Core.csproj`](../../../packages/core/src/Carbide.Core.csproj)
- [`src/Carbide/packages/core/src/CompilationInterop.cs`](../../../packages/core/src/CompilationInterop.cs)
- [`src/Carbide/packages/core/src/ts/runtime/boot.ts`](../../../packages/core/src/ts/runtime/boot.ts)
- [`src/Carbide/packages/core/src/ts/runtime/dotnet-types.ts`](../../../packages/core/src/ts/runtime/dotnet-types.ts)
- [`src/Carbide/packages/core/src/ts/session.ts`](../../../packages/core/src/ts/session.ts)
- [`lib/dotnet/runtime/src/libraries/System.Runtime.InteropServices.JavaScript/src/System/Runtime/InteropServices/JavaScript/JSMarshalerType.cs`](../../../../../lib/dotnet/runtime/src/libraries/System.Runtime.InteropServices.JavaScript/src/System/Runtime/InteropServices/JavaScript/JSMarshalerType.cs)
- [`lib/dotnet/runtime/src/libraries/System.Runtime.InteropServices.JavaScript/src/System/Runtime/InteropServices/JavaScript/Marshaling/JSMarshalerArgument.Object.cs`](../../../../../lib/dotnet/runtime/src/libraries/System.Runtime.InteropServices.JavaScript/src/System/Runtime/InteropServices/JavaScript/Marshaling/JSMarshalerArgument.Object.cs)
- [`lib/dotnet/runtime/src/libraries/System.Runtime.InteropServices.JavaScript/src/System/Runtime/InteropServices/JavaScript/JSException.cs`](../../../../../lib/dotnet/runtime/src/libraries/System.Runtime.InteropServices.JavaScript/src/System/Runtime/InteropServices/JavaScript/JSException.cs)

Primary external sources reviewed:

- [Microsoft Learn: JS `[JSImport]` / `[JSExport]` interop](https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/?view=aspnetcore-10.0)
- [Microsoft Learn: WebAssembly Browser App interop](https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/wasm-browser-app?view=aspnetcore-10.0)
- [Microsoft Learn: `IJSObjectReference`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.jsinterop.ijsobjectreference?view=aspnetcore-10.0)
- [Microsoft Learn: `DotNetObjectReference<TValue>`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.jsinterop.dotnetobjectreference-1?view=aspnetcore-10.0)
- [`aspnetcore` discussion #53866](https://github.com/dotnet/aspnetcore/discussions/53866)
- [Bootsharp docs](https://bootsharp.com/guide/getting-started), [Type Declarations](https://bootsharp.com/guide/declarations), [Interop Instances](https://bootsharp.com/guide/interop-instances), [Sideloading Binaries](https://bootsharp.com/guide/sideloading), [NativeAOT-LLVM](https://bootsharp.com/guide/llvm)
- [ClearScript FAQ](https://clearscript.clearfoundry.net/Tutorial/FAQtorial)
- [`componentize-dotnet`](https://github.com/bytecodealliance/componentize-dotnet)
- [`jco`](https://github.com/bytecodealliance/jco)
- [Uno interop docs](https://platform.uno/docs/articles/external/uno.wasm.bootstrap/doc/features-interop.html)
- [`edge-js`](https://github.com/agracio/edge-js)

Representative commands used during verification:

```powershell
git rev-parse HEAD
gh api repos/elringus/bootsharp --jq "{full_name,pushed_at,updated_at,default_branch,stargazers_count}"
gh api repos/elringus/bootsharp/releases/latest --jq "{tag_name,published_at,name}"
gh api repos/elringus/bootsharp/contributors --jq '.[0:5] | map({login, contributions})'
npm view @microsoft/dotnet-js-interop version time --json
gh api repos/bytecodealliance/componentize-dotnet --jq "{full_name,pushed_at,updated_at,default_branch}"
gh api repos/bytecodealliance/jco/releases/latest --jq "{tag_name,published_at,name}"
```
