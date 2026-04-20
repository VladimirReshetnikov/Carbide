---
name: Carbide WASM JS↔C# Interop Libraries Survey
description: Landscape of libraries that can bridge browser/Node.js JavaScript and C# code compiled by src/Carbide to WebAssembly, with ClearScript-level ergonomics as the benchmark.
---

# Carbide WASM JS↔C# Interop Libraries Survey

- Created (UTC): 2026-04-18T21:43:55Z
- Repository HEAD: 0b929aad1eef7e0307cede8e6fb6b4dd1468b1d3

## 1. Context and goal

`src/Carbide` is a JS-hosted toolchain that compiles C# source to WebAssembly using the official **Mono WebAssembly build tools** (`Microsoft.NET.Sdk.WebAssembly`, `RuntimeIdentifier=browser-wasm`; see `src/Carbide/packages/core/src/Carbide.Core.csproj`). The compiled artifact already uses `System.Runtime.InteropServices.JavaScript.JSExport` at its boundary (`src/Carbide/packages/core/src/CompilationInterop.cs`), and the TypeScript side consumes those exports through `CarbideInteropExports` in `src/Carbide/packages/core/src/ts/runtime/`. So the question is not "how do we start talking between JS and .NET/WASM?" — that is solved. The question is: **what existing library can close the distance between today's JSON/base64 string-passing layer and an ergonomic object-graph bridge equivalent to Microsoft ClearScript?**

This report defines that bar, surveys the libraries available in April 2026, and ranks them against the goal of "a JS host sees compiled C# as if it were a native JS module with typed proxies, auto-marshaled primitives/collections/delegates, Promise↔Task bridging, and propagating exceptions." It then recommends a concrete path for Carbide.

## 2. Target bar — what "ClearScript-level" means

ClearScript ([microsoft/ClearScript](https://github.com/microsoft/ClearScript)) is the reference point because it remains the most ergonomic CLR↔script bridge in the .NET ecosystem. Reduced to interop requirements, it guarantees:

| Capability | Shape |
|---|---|
| JS sees a CLR object | Automatic property, method, indexer, event access: `host.Add(2, 3)`, `host.Name = "x"`, `host.Items[0]`. |
| C# sees a JS object | `dynamic`, `ScriptObject`, and delegate conversions: `(Func<int,int>)engine.Evaluate("x => x*2")`. |
| Primitives | Numbers, strings, booleans, `null`/`undefined`, and optionally `DateTime`↔`Date`. |
| Collections | `IEnumerable`/`IList`/`IDictionary` iterable from JS; JS arrays iterable from C#. |
| Delegates / callbacks | JS functions auto-wrapped to `Action`/`Func`; CLR delegates callable from JS. |
| Async | `Task`↔`Promise` (opt-in), `await` works on either side. |
| Exceptions | JS `throw` surfaces as CLR exception with JS stack trace and vice versa. |
| Generic methods | `host.Method[Int32](x)` with explicit type args. |
| Security | Attribute-based allow/deny (`[ScriptUsage]`, `[NoDefaultScriptAccess]`, `HostItemFlags`). |
| Exposure API | `engine.AddHostObject(name, obj)`, `AddHostType(name, type)`, `HostItemFlags.GlobalMembers` to flatten into the script global namespace. |

Known ClearScript limitations that any WASM replacement will inherit anyway: struct mutation does not cross the boundary, `ref`/`out` are not supported, object identity is not preserved across calls (proxies are recreated), and method dispatch is reflection-based at ~µs/call.

## 3. Carbide today: what's already there, what's missing

Direct read of the code.

### 3.1 Foundation (already solid)

- **Runtime**: Mono-based WBT (`Microsoft.NET.Sdk.WebAssembly`), single-threaded, .NET 10, `browser-wasm` RID, not trimmed (M1 note in `Carbide.Core.csproj`).
- **Boundary attribute surface**: `[JSExport]` on `CompilationInterop` methods, invoked from TS via `getAssemblyExports`. This is the officially supported, source-generator-backed `System.Runtime.InteropServices.JavaScript` pipeline.
- **Serialization**: AOT-safe `JsonSerializerContext` (`CarbideJsonContext`) for DTOs; schema versioned (`SCHEMA_VERSION=2`).
- **Dual-host**: `HostAdapter` (`src/Carbide/packages/core/src/ts/host/adapter.ts`) cleanly isolates the browser path (derive URLs from `import.meta.url`, `fetch`) from the Node path (spin up a localhost HTTP server over `_framework/`, use `fs/promises`).

### 3.2 Today's interop is string-only

Every crossing is a JSON string or a base64-encoded byte array:

```csharp
[JSExport] public static string  CreateSession(string optionsJson);
[JSExport] public static string  AddReference(string sessionId, string base64Bytes, string? name);
[JSExport] public static Task<string> BuildAsync(string projectId);   // returns JSON(BuildResultDto)
```

```ts
const sessionId = interop.CreateSession(JSON.stringify({ schemaVersion: SCHEMA_VERSION }));
const id        = interop.AddReference(this.sessionId, base64, name ?? null);
```

This is deliberate and correct for Carbide's current scope (compile & run C# code, capture stdout/stderr, return diagnostics). There is no user-visible need today for JS to reach into a CLR object graph.

### 3.3 Gap vs. ClearScript-level, concretely

The gap is the *library above* the `[JSExport]` boundary, not below it:

- No object-graph marshaling. A CLR `Project` or `Document` is not visible to JS as a typed proxy; only its scalar surface is exposed by hand-written `[JSExport]` methods.
- No delegate bridging in either direction.
- No live `Task` ↔ `Promise` — `BuildAsync` returns `Task<string>`, so it is awaitable on the JS side, but `Task<CustomDto>` without a manual JSON stage is not.
- No exception propagation — user C# exceptions are stringified into stdout (`RunResult`), and `JSException` is not surfaced as a first-class TS error.
- No code generation from a C# surface to a TS `.d.ts`.

## 4. Landscape

### 4.1 Microsoft's in-box stack

#### 4.1.1 `System.Runtime.InteropServices.JavaScript` (`[JSImport]` / `[JSExport]`) — the modern primitive

The namespace is the foundation on which every credible library below sits. The reference assembly is in this repo at `lib/dotnet/runtime/src/libraries/System.Runtime.InteropServices.JavaScript/ref/System.Runtime.InteropServices.JavaScript.cs`.

Key types:

- `[JSImport(functionName, moduleName?)]` on a `partial` C# method → source-generator-emitted stub calling into a JS module registered via `setModuleImports`.
- `[JSExport]` on a C# method → exposed on the JS side through `getAssemblyExports(assemblyName)`.
- `JSMarshalAs<JSType.{Number,String,Date,Object,Error,MemoryView,Array<T>,Promise<T>,Function<...>,Any}>` for explicit marshaling shape.
- `JSObject : IDisposable` — opaque handle to a JS value, with `GetPropertyAsX/SetProperty/HasProperty/GetTypeOfProperty`.
- `JSException : Exception` — whose `StackTrace` lazily concatenates the JS `.stack` with the managed stack (runtime source `JSException.cs`, ~lines 35–85).
- `JSHost.GlobalThis`, `JSHost.DotnetInstance`, `JSHost.ImportAsync(name, url, ct)`.

The JS side is intentionally small:

```js
import { dotnet } from './_framework/dotnet.js';
const { setModuleImports, getAssemblyExports, getConfig, runMain } =
  await dotnet.withApplicationArguments("start").create();
setModuleImports('main.js', { dom: { setText: (sel, t) => document.querySelector(sel).innerText = t } });
const exports = await getAssemblyExports(getConfig().mainAssemblyName);
exports.MyNamespace.Program.Toggle();   // sync call into C#
```

**Auto-marshaled types** (.NET 10, [ref](https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/?view=aspnetcore-10.0)):

| .NET | JS | Notes |
|---|---|---|
| `Boolean`, `Byte`, `Int32`, `Single`, `Double`, `Char`, `Int16`, `IntPtr` | primitives | direct |
| `Int64` | `Number` **or** `BigInt` | must pick with `JSMarshalAs` |
| `DateTime`, `DateTimeOffset` | `Date` | |
| `String` | `String` | |
| `Exception` | `Error` | stack-trace round-trip via `JSException.StackTrace` |
| `Span<T>` / `ArraySegment<T>` for `T ∈ {byte,int,float,double}` | `MemoryView` | zero-copy over pinned buffer |
| `Task` / `Task<T>` | `Promise` | `T` must itself be a supported type |
| `Action` / `Func<...>` (arity ≤ 3) | `Function` | |
| `object` as `JSType.Any` | opaque JS proxy | kept alive via `GCHandle`, **no property access on the JS side** |
| `JSObject` | `Object` | opaque handle on the C# side |

**Fidelity limitation — critical for this investigation.** The `JSType.Any` marshaling of an arbitrary CLR object produces a JS-visible proxy that is opaque: it keeps the CLR instance alive but exposes *no* members. Any property or method access has to go back through a dedicated `[JSExport]` stub. This is the single concrete wall between `[JSExport]` and ClearScript-level ergonomics. (Same wall on the reverse: `JSObject` in C# only has `GetPropertyAsX/SetProperty/InvokeMethod`, not dynamic binding.)

**Exceptions**: bidirectional. Managed → JS produces an `Error` with `.message` from `Exception.Message`; JS `Error` → managed produces a `JSException` with concatenated stack. Quality on the JS side is only as good as V8/SpiderMonkey WebAssembly frame symbolication (function + offset in release).

**Async**: fully wired. `Task<T>` ↔ `Promise<T>` with supported inner `T`. Nested generics in `JSMarshalAs` (e.g. `Promise<Array<Number>>`) are a compile-time error; workaround is `Task<JSObject>`.

**Outside Blazor**: first-class supported scenario. `dotnet new wasmbrowser` (browser) and `dotnet new wasmconsole` (Node). See [Andrew Lock's 2025-08-12 walk-through](https://andrewlock.net/running-dotnet-in-the-browser-without-blazor/) for the production config (bundles ~1.4 MB Brotli with `<InvariantGlobalization>`).

#### 4.1.2 `Microsoft.JSInterop` (the Blazor layer)

The higher-level interop used by Blazor components: `IJSRuntime`, `IJSObjectReference`, `DotNetObjectReference<T>`, `[JSInvokable]`. Distinguishing features:

- Transport-agnostic (SignalR for Blazor Server, `[JSImport]` under Blazor WASM, WebView IPC for Hybrid).
- **`IJSObjectReference` is a live JS-side proxy held by C#.** On it you can `InvokeAsync<T>`, `GetValueAsync<T>`, `SetValueAsync`, `InvokeConstructorAsync` (.NET 10+).
- **`DotNetObjectReference<T>.Create(x)`** lets JS call `[JSInvokable]` methods via `ref.invokeMethodAsync("Name", args)` — **method dispatch only; no property access**.
- `System.Text.Json`-based by-value marshaling for complex return types.
- Async-first by design; `IJSInProcessObjectReference` is a WASM-only sync fast path.
- Microsoft has explicitly stated: *"You can't use `DotNetObjectReference` with the JSImport API. That's the native WebAssembly specific interop mechanism, not the Blazor one"* ([aspnetcore#53866](https://github.com/dotnet/aspnetcore/discussions/53866)). The two mechanisms are parallel, not composable.

The npm companion is [`@microsoft/dotnet-js-interop`](https://www.npmjs.com/package/@microsoft/dotnet-js-interop) (v10.0.0, published late 2025).

For non-Blazor hosts, getting this layer running outside its Circuit/host plumbing is non-trivial and not advertised.

#### 4.1.3 NativeAOT-LLVM / WASI

Separate back-end in `dotnet/runtimelab` (branch `feature/NativeAOT-LLVM`). Emits true AOT-compiled WASM via LLVM rather than Mono-JIT'd `dotnet.wasm`. `[JSImport]`/`[JSExport]` work for `browser-wasm`; for pure `wasi-wasm` there is no implicit JS bridge — you export/import in WIT or WASI terms. Bundle sizes are substantially larger than Mono-AOT pre-compression, though startup latency is better. Links: [runtimelab#2434](https://github.com/dotnet/runtimelab/issues/2434), [compiling.md](https://github.com/dotnet/runtimelab/blob/feature/NativeAOT-LLVM/docs/using-nativeaot/compiling.md).

### 4.2 Third-party libraries

#### 4.2.1 Bootsharp (Elringus) — the closest existing thing

- Repo: [github.com/elringus/bootsharp](https://github.com/elringus/bootsharp). Docs: [bootsharp.com/guide](https://bootsharp.com/guide).
- v0.7.0, 2026-02-10. Last push 2026-04-18 — **active**.
- Target: `net10.0`, RID `browser-wasm`. Works in browsers, Node, Deno, Bun. Supports multi-threading, NativeAOT-LLVM, and trimming.
- Note on heritage: originally `Elringus/DotNetJS`; repo renamed to `bootsharp` in 2023 during the .NET 8 rewrite. URL redirect is in place. `askdaddy/DotNetJS` is an unrelated fork.
- Built on top of `[JSImport]`/`[JSExport]` plus extra attributes `[JSInvokable]`, `[JSFunction]`, `[JSEvent]`, a Roslyn source generator, and a Node-based build step that produces a **single ES module** with runtime and wasm bytes inlined. `import … from './bin/bootsharp/index.mjs'` gets the whole runtime.
- **Interface-based instance binding.** Declare a C# interface; Bootsharp generates JS bindings and TypeScript declarations. Method calls dispatch into live C# objects. This is the headline feature.
- TypeScript `.d.ts` generated for the full surface.

Sample shape (from docs):

```js
import bootsharp, { Program } from "./bin/bootsharp/index.mjs";
Program.getFrontendName = () => "Browser";       // JS implements a [JSFunction]
await bootsharp.boot();
Program.getBackendName();                        // calls into C# [JSInvokable]
```

Weaknesses:
- **No JS `Proxy` over CLR property access.** Instance binding covers method dispatch; field/property access still round-trips via JSON stubs generated per member.
- Small maintainer team; issue volume moderate.
- Build-time dependency on Node; Carbide already has this, so the friction is minimal.

#### 4.2.2 Uno Platform WebAssembly

- [platform.uno/docs — WASM interop](https://platform.uno/docs/articles/external/uno.wasm.bootstrap/doc/features-interop.html).
- Uno's WASM target is Mono-WASM-based, now layered on `[JSImport]`/`[JSExport]`. Also ships `WebAssemblyRuntime.InvokeJS` legacy APIs and `Uno.UI.NativeElementHosting.BrowserHtmlElement` for DOM embedding.
- State-of post-mortem: [The State of WebAssembly 2025–2026](https://platform.uno/blog/the-state-of-webassembly-2025-2026/) — describes .NET 10 improvements and multithreaded-WASM work.
- **Relevance**: nothing here that is not already available via `[JSImport]`/`[JSExport]` unless you are writing a Uno UI app. Not a usable library for Carbide.

#### 4.2.3 Transpilers (contrast, not candidates)

| Name | URL | Approach | Relevance |
|---|---|---|---|
| **Fable** | [github.com/fable-compiler/Fable](https://github.com/fable-compiler/Fable) | F# → JS/TS transpiler | No CLR at runtime → no marshaling problem, but gives up the CLR. Not applicable. |
| **Bolero** | [github.com/fsbolero/Bolero](https://github.com/fsbolero/Bolero) | Elmish F# over Blazor WASM | Blazor-dependent. |
| **Bridge.NET** | (defunct) | C# → JS transpiler | Effectively dead since ~2020. |

#### 4.2.4 Wrong direction (listed for completeness)

- **Jering.Javascript.NodeJS** ([repo](https://github.com/JeringTech/Javascript.NodeJS)) — C# process spawning Node.js subprocess; replaces `Microsoft.AspNetCore.NodeServices`. Out-of-process, JSON-marshaled. Not relevant.
- **Edge.js / `agracio/edge-js`** — in-process .NET hosted by Node via a native add-on. Not WASM. Active fork, 829 stars, pushed 2026-04-16.
- **ClearScript / Jint / YantraJS / SpiderMonkey.NET** — JS engines embedded in .NET on desktop. Exactly opposite of what we need. ClearScript defines our ergonomics bar but cannot implement it.

### 4.3 Low-level building blocks (if you wanted to roll your own)

- **Plain WASM host imports/exports + shared linear memory.** The absolute floor. Reimplementing what `dotnet.js` (`dotnet/runtime/src/mono/wasm/runtime/`) already does internally. Not recommended.
- **WebAssembly Component Model / WIT / `jco`.**
  - [component-model.bytecodealliance.org](https://component-model.bytecodealliance.org/)
  - [`componentize-dotnet`](https://github.com/bytecodealliance/componentize-dotnet) — v0.7.0-preview00010 (2025-03-20), `net10.0`, built on NativeAOT-LLVM.
  - [`jco`](https://github.com/bytecodealliance/jco) — v1.18.0 (2026-04-18), transpiles components into ES modules; `jco run`, `jco serve`.
  - **Reality check**: works today for sync, scalar/record APIs. Async is weak (waits on WASI Preview 3, expected through 2026). WIT "resources" give method dispatch but not JS-Proxy-style property access. Bundle sizes are large (NativeAOT-LLVM).
  - **Verdict**: forward-looking. Track it for 2027. Not the right substrate for Carbide today.
- **Wasmtime / Wasmer Node.js bindings.** Alternative WASM runtimes with WASI system interfaces. Orthogonal to .NET choice.
- **`wasm-bindgen` (Rust).** The design that `[JSImport]`/`[JSExport]` imitates. Rust has more automatic binding of struct methods via JS-Proxy glue than C# does. No C# equivalent outside Bootsharp's generator.

## 5. Ranked comparison against the ClearScript bar

| # | Option | JS→C# method call | Primitives / arrays / delegates / `Task` | CLR object as live JS proxy (property + method) | Exceptions w/ stack | Node | Status |
|---|---|---|---|---|---|---|---|
| 1 | **Bootsharp** | Yes (`Namespace.Class.Method`) | Yes, plus interface-based instance binding | **Partial** — method dispatch live, fields/properties by JSON stubs | Yes (inherits from `[JSImport]`) | **Yes** | Active (2026-04) |
| 2 | **Raw `[JSImport]`/`[JSExport]` + `getAssemblyExports`** | Yes | Yes for all in the supported table | **No** — `JSType.Any` yields opaque proxy; every member needs hand-authored `[JSExport]` | Yes (`JSException.StackTrace` concatenates) | **Yes** | In-box in .NET, active |
| 3 | **Microsoft.JSInterop (Blazor)** | Yes, async-first | By-value JSON; `IJSObjectReference` = JS proxy held by C#; `DotNetObjectReference` = method dispatch for JS | Method dispatch only via `[JSInvokable]`; no property access | Yes | Not designed for; Blazor-only | Active |
| 4 | **`componentize-dotnet` + `jco`** | Yes, via WIT | Primitives and records yes; delegates limited; async weak | WIT resources give methods, not property access | Yes, as component traps | Yes | Preview, active |
| 5 | **NativeAOT-LLVM raw** | Only what you wire up | Manual | No | Manual | Yes | Experimental |
| 6 | **Uno WASM** | Uno-app-only wrapper over #2 | Same as #2 | No | Same as #2 | Not designed for | Active |

**Observation.** No option today reaches ClearScript-level "CLR object appears to JS as a property-addressable `Proxy`." Every approach stops at either "method dispatch only" (Blazor, Bootsharp), "opaque handle" (raw `[JSImport]`), or "serialize everything" (JSON round-trips).

## 6. Gap analysis — what separates today's ceiling from ClearScript

Closing the remaining distance requires four pieces, none of which is individually hard but none of which is packaged in a library today:

1. **C# surface → TypeScript `.d.ts` + JS shim generator.** A Roslyn source generator that, given types decorated with a marker attribute (`[CarbideExport]`, say), emits (a) `[JSExport]` accessor/mutator/invoker stubs per public member, (b) a JS-side factory that builds an ES6 `Proxy` whose `get`/`set`/`deleteProperty` traps call those stubs, and (c) a TypeScript type declaration.
2. **Instance registry with `GCHandle` keyed by integer.** Return a stable integer id across the boundary as `JSObject` or `long`; C# holds a `GCHandle`; JS wrapper disposes it in a `FinalizationRegistry` (available in all modern runtimes, Node ≥ 14.6).
3. **Delegate/event bridging.** Already partially covered by `Action`/`Func` ≤ 3 args auto-marshaling. For higher arities or variadic, a thin wrapper over `JSObject` property reads + invoke suffices.
4. **Exception class mapping.** `[JSExport]` already surfaces managed exceptions as JS `Error`. Adding a TypeScript `CarbideError` subclass with `.inner.stack` preserved is the cosmetic step.

Bootsharp already builds roughly the first 60 % of this (points 1 and 3, on interfaces), which is why it ranks highest. A Carbide-specific "last 40 %" — the `Proxy`-trap layer over Bootsharp's instance bindings, plus a property accessor generator — is a plausible moderate-surface spike: indicatively one new `src/Carbide/packages/carbide-proxy-gen/` source-generator package (~1–2k LOC C#) plus ~300–600 LOC TS for the `Proxy`-trap layer and TS type-declaration emission.

## 7. Recommendations for Carbide

In priority order.

### 7.1 Short term — adopt Bootsharp as the application-code bridge

**Use**: Bootsharp for Carbide's *compiled user code* surface, not for Carbide's own `CompilationInterop`. That is, when a Carbide user writes C# and wants it callable from JS, the build pipeline already has the right tools via Bootsharp.

**Why**: Bootsharp builds on the same `[JSImport]`/`[JSExport]` primitives Carbide already uses. The single-ES-module output is a natural fit for Carbide's "npm package ships a runnable .NET" model. The interface-based instance binding closes the biggest ergonomic gap we currently have (method dispatch to live C# objects). TypeScript declarations drop in cleanly alongside Carbide's existing `.d.ts` story.

**Integration points to verify with a spike**:
- Does the Bootsharp build step compose with Carbide's in-WASM Roslyn compiler? Bootsharp expects to be *the* build step; Carbide needs it to be *a* step that runs inside a Carbide session.
- Exception round-trip fidelity (not documented, likely inherits `[JSImport]` semantics).
- Overhead of the single-module inlining when user code is itself small.

### 7.2 Short term — keep Carbide's own boundary on raw `[JSImport]`/`[JSExport]`

Carbide's `CompilationInterop` does not need object-graph marshaling — its payloads are strings, base64 binary, and Diagnostic arrays. JSON-over-`[JSExport]` is the right tool for that shape. Do not over-library the infrastructure that already works.

Two small wins are worth grabbing:
- **Promise bridging beyond strings.** Move `BuildAsync` to return `Task<JSObject>` over a structured object rather than `Task<string>` with a JSON stage.
- **`JSException` surface to TS.** Wrap runtime errors in a `CarbideError` TS class that carries `.managedStack` and `.jsStack` explicitly.

### 7.3 Medium term — build a thin "Proxy trap" layer on top of Bootsharp

If usage shows that Carbide needs ClearScript-true ergonomics (JS consumer of compiled C# writing `obj.Name = "x"` and `obj.Items[0].Price`), the minimal work is a source generator that emits per-member `[JSExport]` stubs plus a JS-side factory that wraps them in a `Proxy`. This is the moderate-surface spike noted above. The generator can live in `src/Carbide/packages/carbide-proxy-gen/` (new package).

### 7.4 Long term — track `componentize-dotnet` + `jco`

Once WASI Preview 3 lands async (current projection: late 2026) and `jco` stabilises the TypeScript binding generator, the Component Model becomes the most portable cross-language substrate. Keep an eye on it; do not port to it today.

### 7.5 Do not pursue

- **Jering.Javascript.NodeJS**, **Edge.js / edge-js**, **Electron.NET** — wrong direction or wrong runtime.
- **Uno WebAssembly** — no incremental value over raw `[JSImport]` unless Carbide becomes a UI framework.
- **Fable / Bolero / Bridge.NET** — transpilation gives up the CLR.
- **ClearScript** — runs JS inside .NET, the opposite of Carbide's topology.

## 8. Uncertainty / open questions

- **Bootsharp + Carbide-in-WASM**: Bootsharp assumes it drives the publish step. Whether it composes with Carbide's *in-browser* build (where Roslyn runs inside the WASM sandbox, not during `dotnet publish`) needs a concrete spike. I suspect two build modes will be needed: "SDK-side" (Bootsharp's native flow, used by Carbide's infrastructure) and "in-sandbox" (Carbide synthesises the Bootsharp bindings at compile time from user C#).
- **Bundle size under Bootsharp**: inlining the whole `.wasm` runtime as base64 inside a single ES module is great for distribution but inflates parse time. Need to measure on a realistic Carbide payload.
- **Async fidelity** across `Task<T>` with `T` = user-defined record: supported in `[JSImport]` only if you use `Task<JSObject>` + manual unwrap. Bootsharp's generator claim needs verification.
- **`JSException.StackTrace` on production builds** is only as good as V8/SpiderMonkey's Wasm frame symbolication. Typically function-name + offset; not line numbers unless DWARF is served.

## 9. Key references

- `[JSImport]`/`[JSExport]` index: https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/?view=aspnetcore-10.0
- WASM browser app template walk-through: https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/wasm-browser-app?view=aspnetcore-10.0
- .NET 7 announcement (origin of `[JSImport]`): https://devblogs.microsoft.com/dotnet/use-net-7-from-any-javascript-app-in-net-7/
- Andrew Lock, "Running .NET in the browser without Blazor" (2025-08-12): https://andrewlock.net/running-dotnet-in-the-browser-without-blazor/
- Namespace reference: https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.javascript?view=net-10.0
- `DotNetObjectReference` vs `[JSImport]` clarification: https://github.com/dotnet/aspnetcore/discussions/53866
- Bootsharp: https://github.com/elringus/bootsharp — docs https://bootsharp.com/guide
- Blazor JS interop: https://learn.microsoft.com/en-us/aspnet/core/blazor/javascript-interoperability/?view=aspnetcore-10.0
- `@microsoft/dotnet-js-interop` npm: https://www.npmjs.com/package/@microsoft/dotnet-js-interop
- ClearScript (reference bar): https://github.com/microsoft/ClearScript — tutorial https://clearscript.clearfoundry.net/Tutorial/FAQtorial
- WebAssembly Component Model: https://component-model.bytecodealliance.org/
- `componentize-dotnet`: https://github.com/bytecodealliance/componentize-dotnet
- `jco`: https://github.com/bytecodealliance/jco
- Bytecode Alliance article: https://bytecodealliance.org/articles/simplifying-components-for-dotnet-developers-with-componentize-dotnet
- Uno WASM interop doc: https://platform.uno/docs/articles/external/uno.wasm.bootstrap/doc/features-interop.html
- Uno state-of-WASM post: https://platform.uno/blog/the-state-of-webassembly-2025-2026/
- NativeAOT-LLVM branch docs: https://github.com/dotnet/runtimelab/blob/feature/NativeAOT-LLVM/docs/using-nativeaot/compiling.md
- Local runtime reference assembly: `lib/dotnet/runtime/src/libraries/System.Runtime.InteropServices.JavaScript/`

## 10. TL;DR

- Carbide already stands on the right foundation: Mono WBT + `[JSExport]`. The question is what to layer on top.
- **No library in the ecosystem today reaches ClearScript-level "CLR object as property-addressable JS `Proxy`"** — the wall is `JSType.Any`'s opacity on the JS side.
- **Bootsharp** is the closest (instance binding over interfaces, single ES module output, active maintenance, Node + browser). It covers ~60 % of what's needed. Adopt it as the user-code bridge.
- **Keep Carbide's own infrastructure boundary on raw `[JSImport]`/`[JSExport]`** — its payloads are strings and binary; JSON is correct.
- **Closing the last 40 %** to full property-proxy ergonomics is a moderate-surface source-generator spike (one new `src/Carbide/packages/carbide-proxy-gen/` package: `[CarbideExport]` marker → per-member stubs + ES6 `Proxy` + TS types; indicatively ~1–2k LOC C# + ~300–600 LOC TS). Do this only if users demand it.
- **Track `componentize-dotnet` + `jco`** as the 2027 portable substrate, but do not port today.
