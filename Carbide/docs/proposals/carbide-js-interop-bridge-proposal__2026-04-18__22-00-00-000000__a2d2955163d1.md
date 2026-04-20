# Carbide — JS↔C# interop bridge proposal

- Created (UTC): 2026-04-18T22:00:00Z
- Repository HEAD: 0b929aad1eef7e0307cede8e6fb6b4dd1468b1d3

Status: first-pass design proposal for a ClearScript-level object-graph bridge between the JavaScript host and Carbide-compiled C#. Companion to the landscape survey [*JS↔C# WASM interop libraries survey*](../research/js-interop/carbide-wasm-js-interop-libraries-survey__2026-04-18__21-43-55-000000__b27d950cd3b9.md).

Audience: repository owner and future contributors picking up the data-plane interop workstream.

Scope: the *what* and *how* for an interop layer that lets JS see compiled C# objects as typed proxies with auto-marshaled members, async, and exceptions — without breaking Carbide's in-sandbox compilation model. The *survey* is the *why*; this is the *how*.

This document takes positions on every design axis. Where a decision is ambiguous, the document picks one and states the alternative alongside it with a short rationale. Re-opening a decision requires editing §15 (Open questions) rather than silently reshaping the architecture. Decision markers use the convention `B#` (Bridge decisions) to stay out of the way of the M-milestone decision numbers.

## 1. Purpose

Carbide today has a **control-plane** interop surface (`CompilationInterop.cs`): JSON and base64 strings that tell Carbide *"compile this C#"*, *"run it"*, *"give me its diagnostics"*. That surface is correct for what it does and should not change.

What it does **not** have is a **data-plane** interop surface — a way for the JavaScript host to see the compiled C# program as an object graph it can reach into: call methods, read and write properties, subscribe to events, `await` a `Task`, catch an `Exception`. Today a user's C# compiled by Carbide can print to stdout and that is the complete observable surface from JS.

This proposal designs that data-plane bridge, calling it **`@carbide/bridge`**. The target ergonomics bar is Microsoft ClearScript: JS sees a CLR object as a typed proxy, primitives/collections/delegates auto-marshal, `Task`↔`Promise` bridge, exceptions propagate with stack traces.

Why invest here:

- **Composability.** Today Carbide is a closed loop (compile → run → stdout). A bridge makes Carbide a reusable C# runtime for JS applications, not a C# sandbox.
- **Interactive tooling.** Playgrounds, REPLs, and notebook hosts need the object graph, not the console.
- **Agent tooling.** `cs-agent-tools` currently inspects Carbide output by parsing text; a typed bridge replaces that with structured access.

## 2. Non-goals

- **Not a replacement for `CompilationInterop`.** The control-plane boundary (`CreateSession`, `BuildAsync`, `RunAsync`, etc.) stays on JSON strings + base64 bytes. Don't bridgify it.
- **Not multi-VM.** Only Carbide's single shared Mono WBT runtime. A future multi-tenant isolation story (separate wasm instances per tenant) is out of scope.
- **Not a DOM bridge.** Calls into `window.document` are the consumer's concern via `[JSImport]`. The bridge exposes the *C# side* to JS, not the other way around. (§9 covers optional host-object exposure; that is in scope and limited.)
- **Not cross-thread.** Mono WBT is single-threaded; we inherit that constraint.
- **Not source-level C#-in-JS.** We bridge compiled objects, not C# source. `eval("1+1")` remains `project.addSource` + `build` + `run`.
- **Not a GC-across-boundaries story beyond `FinalizationRegistry`.** See §8.
- **Not a security sandbox.** Default visibility is opt-in allow-list (§10); the bridge is not an isolation mechanism.

## 3. Design constraints specific to Carbide

These constraints distinguish Carbide from the libraries surveyed in the companion report. They constrain the design space significantly.

### 3.1 In-sandbox compilation

Carbide's distinguishing feature is that user C# is compiled by Roslyn running *inside* the same WASM instance that will execute it. There is no `dotnet publish` step for the user code; there is only `project.build()` at runtime. Any "SDK-time source generator" model (Bootsharp's default flow) does not apply to the user-code path by construction.

Two derived positions:

- **`B1` — user-code bindings must be produceable at compile time (inside the WASM), not publish time.** The generator, if we use a generator, runs as part of `project.build()` / `project.run()`, not as part of `dotnet publish` of `Carbide.Core.csproj`.
- **`B2` — SDK-time generation is still useful for a separate case: Carbide-aware *library* authors shipping DLLs into Carbide.** Treat it as an orthogonal, optional feature.

### 3.2 Schema-versioned JSON boundary

`CompilationInterop` already carries `schemaVersion: 2` on `ProjectOptions` and `BuildResult`. Any new JSON payload (`B1` manifest, method-call envelopes) must version itself the same way, and the TS runtime must enforce version compatibility (`CarbideSchemaError`).

### 3.3 AOT-safe JSON

Carbide uses `JsonSerializerContext` (`CarbideJsonContext`) for AOT safety. Any new DTOs must be registered in a source-gen context, not reflect-serialized.

### 3.4 Pinned runtime + untrimmed BCL

`Carbide.Core.csproj` is untrimmed. Reflection is free; we can use it without tripping trim warnings. This matters for the runtime-reflection paths in §4.

### 3.5 Single WASM module

Everything ships inside `@carbide/core`. A separate npm package `@carbide/bridge` is a **TypeScript-only** surface; its C# side lives inside `Carbide.Core`.

## 4. Terminology

- **Host** — JavaScript environment running Carbide (browser tab, Node process).
- **Guest** — C# running inside the Carbide WASM instance.
- **Host object** — a JS value made visible inside the guest.
- **Guest object** — a CLR value made visible in the host.
- **Proxy** — an ES6 `Proxy`-based JS wrapper presenting a guest object as if it were a JS object.
- **Handle** — an integer id used across the boundary to refer to a live guest object kept alive by a `GCHandle`.
- **Manifest** — the JSON description of a compilation's exported surface.
- **Stub** — a generated per-member method for get/set/invoke dispatch.
- **Generic dispatcher** — a runtime-reflection `[JSExport]` method that handles any member of any registered type.
- **Bridge module** — the TS layer that implements the proxy factory and the handle registry on the host side.
- **Control plane / data plane** — as used in §1.

## 5. Design space

Five axes define the design space. Every approach in §6 picks a point along each.

### 5.1 Axis A — where is type metadata produced?

- **A.1 SDK-time** (Roslyn source generator at `dotnet publish`). Only works for pre-compiled DLLs.
- **A.2 Compile-time in-sandbox** (walk Roslyn `SemanticModel` at `project.build()`). Works for user code.
- **A.3 Runtime reflection** (`Type.GetMembers()` on demand).

### 5.2 Axis B — how are per-member calls dispatched on the C# side?

- **B.1 Per-member `[JSExport]` stubs.** Requires code generation. Fast, statically typed, each `[JSExport]` is a compact generated method.
- **B.2 Generic reflective dispatcher.** A small fixed set of `[JSExport]`s (`GetProperty`, `SetProperty`, `InvokeMember`, etc.) that reflect at runtime. No generation. Slower.
- **B.3 Hybrid**: reflective dispatcher for the in-sandbox path, stubs for the SDK path.

### 5.3 Axis C — how does the host present the guest object?

- **C.1 Plain JS class with methods.** Bootsharp-style. No property access; methods only.
- **C.2 ES6 `Proxy` with `get`/`set`/`deleteProperty` traps.** Near ClearScript parity.
- **C.3 Raw opaque handle.** What `[JSImport]`/`[JSExport]` gives you today with `JSType.Any`.

### 5.4 Axis D — how is handle lifetime managed?

- **D.1 Explicit `.dispose()` only.** Simple, leaks-prone.
- **D.2 `FinalizationRegistry` auto-release.** Supported in all modern runtimes (Node ≥ 14.6). Nondeterministic timing.
- **D.3 Hybrid — registry + optional explicit `dispose`.** Recommended default.

### 5.5 Axis E — what is the async model?

- **E.1 `Task<T>` ↔ `Promise<T>`** where `T` is a directly supported primitive. Built in.
- **E.2 `Task<guest-object>` via `Task<long>` handle returns.** Manual unwrap in TS.
- **E.3 `Task<JSObject>`** with a generated JS-side thenable wrapper. Cleanest.

## 6. Approaches considered

Five credible approaches. Each stated against the five axes with tradeoffs.

### 6.1 Approach α — raw `[JSImport]` / `[JSExport]`, hand-authored per type

| Axis | Choice |
|---|---|
| A. Metadata | — (none; hand-authored) |
| B. Dispatch | B.1 stubs (hand-written) |
| C. Host shape | C.3 opaque handle + hand-written wrapper |
| D. Lifetime | D.1 explicit |
| E. Async | E.1 only |

**Pros.** Zero infrastructure. Uses only in-box APIs. No new dependencies. Stable and predictable.

**Cons.** Does not scale past ~5 types. Developer writes every `[JSExport]` and every TS wrapper by hand. Not ClearScript-level by any measure. We already know this does not reach the goal; it is the baseline we compare against.

**Verdict.** Do not adopt; keep it in the control plane (`CompilationInterop` is exactly this, and correctly so).

### 6.2 Approach β — Bootsharp end-to-end

| Axis | Choice |
|---|---|
| A. Metadata | A.1 SDK-time Roslyn source generator |
| B. Dispatch | B.1 per-member stubs |
| C. Host shape | C.1 class with method dispatch (interface-based instance binding) |
| D. Lifetime | explicit dispose on instances |
| E. Async | E.1 for supported T |

**Pros.** Battle-tested. Already ships TypeScript `.d.ts`. Works in Node and browser. Single ES module output is an excellent distribution shape. Solves ~60% of the ClearScript bar.

**Cons.**
- **Assumes SDK-time build flow.** `B1` says our user-code path cannot use that. Bootsharp would at best cover the *Carbide infrastructure's* surface (`CompilationInterop`), which does not need bridging, or pre-compiled library DLLs (§6.3).
- No property access, only method dispatch via interface.
- Bootsharp is an external dependency with a small maintainer team; adopting it as the whole of our surface trades Carbide's self-containedness for someone else's release cadence.
- Bootsharp's "single ES module with inlined runtime" collides with Carbide's "ship `_framework/` under `@carbide/core`" model — we'd either duplicate runtime or re-architect distribution.

**Verdict.** Do not adopt as the whole bridge. Consider for the SDK-side library-authoring subset only (§6.3).

### 6.3 Approach γ — Bootsharp for SDK-side libraries only

A variant of β scoped tightly: Carbide-aware library authors who ship a DLL to be consumed inside Carbide run Bootsharp at *their* build time. Carbide then loads the produced binding module on `project.addReference(bytes, { bridge: bootsharpBindingsJs })`.

**Pros.** Reuses Bootsharp's generator without making it Carbide-native. Author opt-in; no impact on typical Carbide users.

**Cons.** Narrow value: the main want is in-sandbox user code, not libraries. If pursued, it adds a second binding model to maintain alongside the primary one. The JSON-over-interface bindings also still aren't ClearScript-level.

**Verdict.** Defer as a post-v1 optional affordance; not part of the primary proposal.

### 6.4 Approach δ — custom Carbide source generator, SDK-time

Attribute-driven Roslyn generator — `[CarbideExport]` on types and members — that emits at `dotnet publish`:

- per-member `[JSExport]` stubs on a generated partial class;
- a TypeScript `.d.ts`;
- a JS factory `createFooProxy(handle)` returning an ES6 `Proxy` whose traps call the stubs.

| Axis | Choice |
|---|---|
| A. Metadata | A.1 SDK-time |
| B. Dispatch | B.1 stubs |
| C. Host shape | C.2 ES6 Proxy |
| D. Lifetime | D.3 hybrid |
| E. Async | E.3 |

**Pros.** Closest to ClearScript-level ergonomics. Zero runtime reflection. Fast dispatch. Compile-time type safety on both sides.

**Cons.** `B1` — this is exactly what we cannot use for user-code, because user-code compilation does *not* run the Roslyn generator (it runs Roslyn's compiler inside WASM, without the generator pipeline wired in). We could port the generator to run in-sandbox, but at that point we are closer to approach ε anyway.

**Verdict.** Keep as the SDK-side library path (superseding γ). Not the primary mechanism.

### 6.5 Approach ε — metadata manifest + generic reflective dispatcher (the proposed core)

This is the approach this proposal recommends. It addresses the in-sandbox-compilation constraint directly.

| Axis | Choice |
|---|---|
| A. Metadata | A.2 compile-time in-sandbox (walk Roslyn `SemanticModel` at `project.build()`) |
| B. Dispatch | B.2 generic reflective (supplemented by B.1 for hot paths at v1.1+) |
| C. Host shape | C.2 ES6 Proxy |
| D. Lifetime | D.3 hybrid |
| E. Async | E.3 |

The flow:

1. During `project.build()`, Carbide walks the user's `Compilation` with a surface analyzer. It finds types marked `[CarbideExport]` (or, under the opt-out model, all public types not marked `[CarbideHide]`). It emits a **manifest** JSON describing their members.
2. The manifest rides back to the host as part of the `BuildResult` (additive field; `SCHEMA_VERSION` bumps to 3).
3. On the host side, `@carbide/bridge` consumes the manifest and builds a **proxy factory** per exported type. The factory produces ES6 `Proxy`s whose traps dispatch to a small fixed set of generic `[JSExport]`s on `Carbide.Core.Bridge.Dispatcher`.
4. At runtime, JS calls `proxy.Name` → `Dispatcher.GetProperty(handle, typeName, "Name")` → C# reflects over the target CLR instance and returns the value. Return values that are themselves guest objects are re-wrapped as new proxies (recursion bounded by handle caching).

**Pros.**
- Works for in-sandbox user code (the primary case).
- No per-type code generation at runtime — the manifest is data, not code.
- ES6 Proxy traps give the "`foo.Bar = baz`" feel ClearScript has.
- TS typings are emitted from the manifest by `@carbide/bridge-tsgen` alongside the build.
- The C# dispatcher surface is small and fixed (≈10 `[JSExport]` methods), so it goes through the source-gen `[JSExport]` pipeline at SDK time for `Carbide.Core` itself — no in-sandbox code generation needed.

**Cons.**
- Runtime reflection dispatch is slower than generated stubs. Acceptable at v1 and mitigatable by caching `MemberInfo` lookups in a concurrent dictionary keyed by `(Type, memberName)`.
- Strings-as-member-names means typos at runtime, not compile time. TS typings emitted from the manifest substantially mitigate this on the JS side.
- Generics are hard — see §13.

**Verdict.** **Adopt as the primary bridge mechanism.**

### 6.6 Approach ζ — WIT + `jco` + `componentize-dotnet`

Ahead-of-time AOT to a WebAssembly Component with WIT-typed bindings, transpiled by `jco` to an ES module.

**Pros.** Language-neutral. Future-proof. Typed contracts.

**Cons.**
- Preview on the .NET side (`componentize-dotnet` 0.7.0-preview, March 2025).
- NativeAOT-LLVM only; a completely different toolchain from Mono WBT; bundle sizes much larger; Carbide's "ship one runtime" model doesn't fit.
- Async story waits on WASI Preview 3.
- WIT resources give method dispatch but not `Proxy`-style property access anyway.

**Verdict.** Track for a future Band-C variant (`@carbide/core-wasi` territory). Not the v1 mechanism.

## 7. Recommended approach

**ε (manifest + reflective dispatcher + Proxy host)** as the primary mechanism for all user code. **δ (custom source generator)** as the optional SDK-time path for library authors who want generated stubs at publish time. No β, no ζ at v1.

One diagram, end-to-end for ε:

```text
┌─────────────────────────────────────────────────────────────────────┐
│ Host (browser / Node)                                               │
│                                                                     │
│   import { bootCarbide, Bridge } from '@carbide/core';              │
│                                                                     │
│   const sess = await CarbideSession.initializeAsync();              │
│   const proj = sess.createProject({});                              │
│   proj.addSource("Lib.cs", csharpSource);                           │
│   const { success, bridge } = await proj.build();                   │
│   const lib = bridge.module("MyLib");       // typed facade         │
│   const calc = lib.Calculator.new(10);      // static + ctor        │
│   calc.Value += 5;                          // property write       │
│   const n   = await calc.ComputeAsync();    // Task<int> → Promise  │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
             │                                       ▲
             │ GetProperty / SetProperty             │ proxy factory
             │ InvokeMember / CreateInstance         │ from manifest
             ▼                                       │
┌─────────────────────────────────────────────────────────────────────┐
│ @carbide/bridge (TS) — manifest-driven proxy factory                │
│                                                                     │
│   • HandleRegistry<T> with FinalizationRegistry                     │
│   • MarshalTable (primitives, arrays, Date, JSObject, handles)      │
│   • ProxyFactory per type from manifest                             │
│   • TS .d.ts generator emits `MyLib.d.ts` alongside build           │
└─────────────────────────────────────────────────────────────────────┘
             │ [JSExport] generic surface
             ▼
┌─────────────────────────────────────────────────────────────────────┐
│ Carbide.Core.Bridge (C#, compiled into Carbide.Core.wasm)           │
│                                                                     │
│   • Dispatcher: GetProperty, SetProperty, InvokeMember,             │
│                 CreateInstance, InvokeStatic, Dispose,              │
│                 AttachHostObject, DetachHostObject                  │
│   • HandleTable<GCHandle> keyed by long; monotonic id allocator     │
│   • MemberCache<(Type,string), MemberInfo>                          │
│   • SurfaceAnalyzer: walks Compilation, emits manifest JSON         │
│   • MarshalEngine: by-value/by-ref policy, delegate wrap/unwrap     │
│                                                                     │
│   Runs on top of System.Runtime.InteropServices.JavaScript:         │
│   all [JSExport] methods source-gen'd at Carbide.Core's publish.    │
└─────────────────────────────────────────────────────────────────────┘
```

## 8. Proposed architecture — layer by layer

### 8.1 C# side — `Carbide.Core.Bridge`

New namespace `Carbide.Core.Bridge` inside `Carbide.Core.csproj`. Components:

#### 8.1.1 `HandleTable`

```csharp
internal sealed class HandleTable
{
    private readonly ConcurrentDictionary<long, GCHandle> _live = new();
    private long _next = 1;

    public long Pin(object o) {
        var h = GCHandle.Alloc(o, GCHandleType.Normal);
        var id = Interlocked.Increment(ref _next);
        _live[id] = h;
        return id;
    }

    public object? Resolve(long id) => _live.TryGetValue(id, out var h) ? h.Target : null;

    public bool Release(long id) {
        if (_live.TryRemove(id, out var h)) { h.Free(); return true; }
        return false;
    }
}
```

`B3` — **Handles are `long`, not `int`.** A long-running session must not exhaust the id space. Carbide's JS boundary marshals `long` as `BigInt` per the `[JSImport]`/`[JSExport]` table (§4.1.1 in the survey). Alternative: `int` + recycling — rejected because id recycling is a debugging nightmare.

#### 8.1.2 `Dispatcher` (the generic `[JSExport]` surface)

About ten `[JSExport]` methods, all `partial` on a single partial class, source-gen'd by the standard `JSImportGenerator` when `Carbide.Core` publishes:

```csharp
public static partial class BridgeDispatcher
{
    [JSExport] public static long   CreateInstance(string typeName, string argsJson);
    [JSExport] public static object? GetProperty(long handle, string name);
    [JSExport] public static void   SetProperty(long handle, string name, object? value);
    [JSExport] public static object? InvokeMember(long handle, string name, string argsJson);
    [JSExport] public static object? InvokeStatic(string typeName, string name, string argsJson);
    [JSExport] public static void   Dispose(long handle);
    [JSExport] public static long   AttachHostFunction(JSObject fn, string descriptorJson);
    [JSExport] public static void   DetachHostFunction(long cookie);
    [JSExport] public static Task<object?> AwaitTaskHandle(long handle);
    [JSExport] public static string ExportManifest(string assemblyName);
}
```

`B4` — **`object?` parameter/return relies on `JSType.Any` with runtime type tagging.** The parameter is actually `object?` box; `[JSMarshalAs<JSType.Any>]` keeps it opaque, and marshaling is delegated to `MarshalEngine` (§8.1.3). Alternative: strongly-typed overloads per primitive type (four methods each: `GetPropertyAsInt32`, etc.) — rejected because it explodes the export surface with low value; reflection already knows the type.

`B5` — **Arg lists ride as JSON strings through `System.Text.Json`.** Simple, debuggable, AOT-safe via `JsonSerializerContext`. Alternative: a custom binary envelope over `MemoryView` — rejected at v1; revisit if microbenchmarks show JSON is the bottleneck. Handles embedded in the JSON are encoded as `{"$h": 123}` sentinel objects.

#### 8.1.3 `MarshalEngine`

A static class that converts between CLR and bridge-wire representations. Rules:

| Direction | CLR | Wire | JS-side |
|---|---|---|---|
| value | `int`, `long`, `double`, `bool`, `string` | primitive JSON | primitive |
| value | `DateTime` | `{"$iso":"..."}` | `Date` |
| value | `byte[]` | `{"$b64":"..."}` | `Uint8Array` |
| value | `IEnumerable<T>` (closed) | JSON array | `Array<T'>` |
| value | `null` | `null` | `null` |
| ref  | any class instance | `{"$h":n}` | proxy |
| ref  | `Task<T>` | `{"$task":n}` | `Promise<T'>` |
| ref  | `Delegate` | `{"$del":n,"sig":"..."}` | function |
| ref  | `JSObject` | pass-through | the original JS object |
| ref  | `Exception` | marshaled then thrown | `CarbideError` |

`B6` — **`IEnumerable<T>` is marshaled by value by default.** Prevents surprising live-enumerator semantics across the boundary and keeps JS-side usage idiomatic. Alternative: return a handle-backed iterator proxy. Offered as opt-in via `[CarbideExportAsRef]` in v1.1.

`B7` — **Structs marshal by value (copy).** Known ClearScript constraint; do not fight it. Mutations to a struct on the JS side do not reach the CLR original.

`B8` — **`ref`/`out` parameters are not bridged in v1.** The `[CarbideExport]` analyzer diagnoses them (`CBR003`). Workaround: return a tuple or a DTO. Alternative: wrapper objects — deferred to v1.1.

#### 8.1.4 `SurfaceAnalyzer`

A Roslyn-based walker that runs inside `Carbide.Core` at `BuildAsync` time. Given a `Compilation`:

- collect types decorated `[CarbideExport]` (or, under §10's opt-out mode, all public types minus `[CarbideHide]`);
- emit a `BridgeManifest` DTO with:
  - `schemaVersion: 3`
  - `assemblyName`
  - `types: { fullName, kind, members: { name, kind, signature, attrs } [] }`
  - `staticExports: { typeName, members }`
- serialize via `CarbideJsonContext` into the `BuildResult`'s new `bridgeManifest` field.

The analyzer lives in `Carbide.Core.Bridge.Analysis`, not in a Roslyn-generator assembly, because it runs at **runtime** inside the WASM — not at SDK build time.

`B9` — **The analyzer runs inside the WASM at `project.build()` time.** This is the crucial in-sandbox move.

### 8.2 TS side — `@carbide/bridge`

New package `packages/bridge/` under `src/Carbide/`. Peer of `@carbide/core`. TypeScript only; no C#. Components:

#### 8.2.1 `BridgeClient`

Thin object owning the `Carbide.Core.Bridge.BridgeDispatcher` `[JSExport]` methods, surfaced via `getAssemblyExports`. Exposes `getProperty`, `setProperty`, `invokeMember`, etc. as `Promise`-returning wrappers with manifest-driven type conversion on the way in and out.

#### 8.2.2 `HandleRegistry`

```ts
class HandleRegistry {
  private readonly finalizer = new FinalizationRegistry<bigint>((h) => this.dispatcher.dispose(h));
  private readonly live = new Map<bigint, WeakRef<object>>();

  register(proxy: object, handle: bigint) {
    this.live.set(handle, new WeakRef(proxy));
    this.finalizer.register(proxy, handle, proxy);
  }
  explicitDispose(proxy: object, handle: bigint) {
    this.finalizer.unregister(proxy);
    this.dispatcher.dispose(handle);
    this.live.delete(handle);
  }
}
```

`B10` — **`FinalizationRegistry` + optional explicit `dispose`.** Nondeterministic auto-release is good enough for most cases. For large-GC-pressure scenarios the user can `.dispose()` manually. Alternative: require explicit dispose — rejected because it forces users to treat CLR objects like files, which they are not in spirit.

#### 8.2.3 `ProxyFactory`

Given a manifest type `T`, produces an ES6 `Proxy`-backed class:

```ts
function buildProxy(typeInfo: TypeInfo, handle: bigint): object {
  return new Proxy(Object.create(null), {
    get: (_, prop) => {
      if (prop === '$handle') return handle;
      if (prop === 'dispose') return () => registry.explicitDispose(this, handle);
      if (prop in typeInfo.methods) {
        return (...args: unknown[]) => client.invokeMember(handle, String(prop), args);
      }
      if (prop in typeInfo.properties) {
        return client.getProperty(handle, String(prop));
      }
      throw new TypeError(`${typeInfo.fullName}: no member '${String(prop)}'`);
    },
    set: (_, prop, value) => {
      if (prop in typeInfo.properties) {
        client.setProperty(handle, String(prop), value);
        return true;
      }
      throw new TypeError(`${typeInfo.fullName}: no writable property '${String(prop)}'`);
    },
  });
}
```

The real implementation is slightly richer: it handles `Symbol.asyncIterator` for `IEnumerable`, `Symbol.dispose` (TC39) for resource management, and overloads method properties with a `.signatures` introspection helper.

#### 8.2.4 `TsTypeGenerator`

Given a manifest, emits a `.d.ts` string. Node-side adapter writes it to disk adjacent to the PE output; browser-side exposes it as a `string` on `bridge.typesText`.

### 8.3 `@carbide/bridge-tsgen` (CLI addition to `@carbide/cli`)

Extends the `carbide build` CLI with `--emit-types <path>` — writes the manifest-derived `.d.ts` next to the PE. Useful in tooling workflows where the compiled library is consumed by an IDE-aware JS project.

## 9. Surface example — what a user writes

### 9.1 C# — user code

```csharp
using Carbide.Bridge;     // new attributes live here

[CarbideExport]
public sealed class Calculator
{
    private int _value;
    public Calculator(int initial) => _value = initial;

    public int Value
    {
        get => _value;
        set => _value = value;
    }

    public int Add(int x) => _value += x;

    public async Task<int> ComputeAsync(int multiplier)
    {
        await Task.Delay(10);
        return _value * multiplier;
    }

    public event Action<int>? ValueChanged;

    [CarbideHide]
    internal void DebugOnly() { /* not exposed */ }
}
```

### 9.2 TypeScript / JS — consumer

```ts
import { CarbideSession } from '@carbide/core';
import '@carbide/bridge';   // side-effect: attaches `bridge` to build result

const sess = await CarbideSession.initializeAsync();
const proj = sess.createProject({ assemblyName: 'MyLib' });
proj.addSource('Calculator.cs', calculatorSource);

const { success, bridge } = await proj.build();
if (!success) throw new Error('build failed');

const lib = bridge.module('MyLib');

const calc = lib.Calculator.new(10);       // constructor → handle
calc.Value = 25;                           // setter
const seven = calc.Add(7);                 // method → 32
const answer = await calc.ComputeAsync(3); // Task<int> → Promise<number>
calc.ValueChanged.add((v) => console.log('now', v));  // event subscribe
calc.dispose();                            // optional; GC also releases
```

### 9.3 Generated `.d.ts` (excerpt)

```ts
declare module 'carbide:MyLib' {
  export class Calculator implements Carbide.IDisposable {
    static new(initial: number): Calculator;
    Value: number;
    Add(x: number): number;
    ComputeAsync(multiplier: number): Promise<number>;
    readonly ValueChanged: Carbide.Event<[value: number]>;
    dispose(): void;
  }
}
```

## 10. Security and visibility

`B11` — **Opt-in default.** Only types decorated `[CarbideExport]` are visible. A `[CarbideHide]` member within an exported type is excluded. Alternative: opt-out (default-expose all public) — rejected because it makes the bridge surface a moving target of the user's code, and because the bridge crosses a trust boundary between the host and untrusted guest code.

`B12` — **No `[CarbideExport]` by-access-level toggle in v1.** Only public members are bridged. `internal`/`protected` require explicit public exposure. Alternative: analogue of ClearScript's `HostItemFlags.PrivateAccess` — deferred to v1.2; low demand expected.

`B13` — **Per-member allow/deny attribute (`[CarbideAccess(Read | Write | Invoke)]`) is v1.1.** v1 grants full access to exposed members.

`B14` — **Sandbox boundary is preserved.** The bridge does not give the host any new ability to escape Carbide's existing sandbox (no filesystem, no network beyond what `[JSImport]` already allows). Host objects attached via `AttachHostFunction` go *inward*; they cannot be invoked by guest code that wasn't given a reference.

## 11. Async, exceptions, events

### 11.1 Async

Built on `[JSExport]`'s existing `Task`↔`Promise` marshaling:

- `Task<T>` where `T` is a bridge-primitive: returns a real `Promise<T>` in JS. No bridge work needed.
- `Task<T>` where `T` is a guest object: `Dispatcher` returns `Task<long>` (handle), and `BridgeClient` wraps it in a `Promise<Proxy>` that materializes the proxy on resolve. (`B15`)
- `ValueTask<T>`: converted to `Task<T>` at the dispatcher boundary. (`B16`)
- `IAsyncEnumerable<T>`: v1.1; in v1 the user must `ToListAsync` on the C# side. (`B17`)

### 11.2 Exceptions

```ts
export class CarbideError extends Error {
  readonly managedType: string;
  readonly managedStack: string;
  readonly jsStack: string;
  constructor(m: string, managedType: string, managedStack: string, jsStack: string) {
    super(m);
    this.managedType = managedType;
    this.managedStack = managedStack;
    this.jsStack = jsStack;
  }
}
```

`B18` — **Managed exceptions become `CarbideError` on the host.** The dispatcher catches `Exception` at every `[JSExport]` entry, serializes `{message, typeName, stackTrace}`, and throws a JS `Error` subclass `CarbideError` the bridge client rehydrates.

`B19` — **Host exceptions thrown from an attached `JSObject` function become a managed exception.** The `JSImport`-wrapped function is invoked inside a `try/catch` on the C# side; a caught `JSException` surfaces to the guest as `Carbide.BridgeException` with the `.stack` concatenated.

### 11.3 Events

`B20` — **`.NET events become objects with `add(listener)`, `remove(listener)`, `once(listener)`.** The bridge's proxy detects `EventInfo` members and substitutes an event-accessor object. Under the hood, `add` generates a proxy delegate via `Delegate.CreateDelegate` over a host-function handle and calls `EventInfo.AddEventHandler`.

## 12. Schema and versioning

- `B21` — **Introduces `SCHEMA_VERSION = 3`.** `ProjectOptions.schemaVersion`, `BuildResult.schemaVersion`, and the new `BridgeManifest.schemaVersion` all advance together. `CompilationInterop.ValidateSchemaVersion` accepts 1, 2, 3.
- `B22` — **All new DTOs live in `CarbideJsonContext`.** No reflect-serialize.
- `B23` — **Manifests carry a per-type hash** (SHA-256 of the full member list + signatures). The host uses this as a cache key for proxy-factory memoization.

## 13. Known hard spots

These are not show-stoppers but they are where bugs will land; call them out explicitly so nobody stumbles.

### 13.1 Generics

- `Calculator<T>` with T bound to a guest type: the JS consumer cannot "give" a CLR generic argument easily. Treat guest generics as opaque: JS calls `lib.Calculator$Int32.new(...)` where the `$Int32` is a mangled closed form produced at manifest time *only* for the instantiations the analyzer detects in the user's code. Free-form construction of closed generics from the JS side is v1.1.
- Generic methods: analogous; expose only detected closed instantiations at v1.
- `B24` — **No open generic construction in v1.**

### 13.2 Overloads

- C# overload resolution is Roslyn's, not reachable from a JS call. The dispatcher picks the first matching overload by arity and best-fit marshaling (`B25`). Ambiguities surface as a runtime `CarbideError` with the candidate list.
- Alternative: compile-time disambiguation via name-mangling (`Add__i32`, `Add__str`) — rejected at v1 for ergonomics; revisit if ambiguous overloads become common.

### 13.3 Object identity

- Two JS proxies for the same CLR instance `calc === calc` only if the host caches handles. `B26` — **handles are cached WeakMap-keyed per `(assemblyName, handle-id)`, so identity is preserved for the lifetime of the JS proxy.** Not preserved across explicit dispose.

### 13.4 GC coupling

- If a CLR object holds a `Delegate` wrapping a `JSObject` host function, and the JS side drops its last reference to the function, the `JSObject` becomes unreachable but the CLR side still holds it pinned. `B27` — **`JSObject` references from C# to the host keep the JS target alive; rely on the `FinalizationRegistry` on the C# side when the CLR holder becomes unreachable.** This is the inverse direction of §8.2.2 and uses the same pattern.

### 13.5 Reentrancy

- Host calls into guest that calls into host that calls into guest… all on a single thread. The dispatcher is reentrant-safe by design (no shared mutable state outside the handle table, which is `Concurrent*`). (`B28`)

### 13.6 Startup cost of manifest emission

- The surface analyzer walks every `Compilation` on every build. For large projects this could dwarf the actual compile. `B29` — **analyzer caches by `Compilation.AssemblyName + symbol-ID-hash` between incremental `project.build()` calls in the same session.** Full rebuild for `project.reset()` or source additions.

## 14. Phasing — proposed milestones

The survey's new-surface estimate — a Carbide-owned source generator plus a handle-table / dispatcher / marshal-engine trio, roughly ~1.5–3k LOC C# + ~500 LOC TS — maps to three Carbide milestones.

### 14.1 M7.1 — C# side: `HandleTable`, `Dispatcher`, `MarshalEngine` (v0 primitives only)

Ships:
- `Carbide.Core.Bridge.HandleTable` with tests.
- `BridgeDispatcher` with `CreateInstance`, `GetProperty`, `SetProperty`, `InvokeMember`, `Dispose` — primitives only.
- `MarshalEngine` supporting `int`, `long`, `double`, `bool`, `string`, `null`, handles.
- No TS side yet; C# tests exercise the `[JSExport]` surface through a test shim.

### 14.2 M7.2 — TS side: `@carbide/bridge`, `ProxyFactory`, `HandleRegistry`

Ships:
- `packages/bridge/` scaffolding (package.json, tsconfig, minimal README).
- `BridgeClient`, `HandleRegistry`, `ProxyFactory` over the M7.1 surface.
- First end-to-end test: create an instance, read/write a property, invoke a method, dispose.

### 14.3 M7.3 — `SurfaceAnalyzer` + manifest

Ships:
- `SurfaceAnalyzer` walking `Compilation` at `BuildAsync` time.
- `BridgeManifest` DTO in `CarbideJsonContext`.
- `BuildResult.bridgeManifest` field; `SCHEMA_VERSION` → 3.
- `ProxyFactory` consumes the manifest (no more hand-constructed `TypeInfo`).
- TS type generator: `bridge.typesText` as a `string`; `--emit-types` CLI flag.

### 14.4 M7.4 — Async, exceptions, events

Ships:
- `Task<T>` auto-bridging for guest-object `T` (handle-return pattern).
- `CarbideError` with managed-type/stack propagation.
- Event accessor objects on proxies (`add`/`remove`/`once`).
- Full BCL collection marshaling rules (`IEnumerable<T>`, `IList<T>`, `IDictionary<K,V>`) — by value in v1.

### 14.5 M7.5 — `[CarbideAccess]`, delegate round-trip, generic closed-form detection

Post-v1 polish. Not part of the v1 scope.

## 15. Open questions

- **Q1 — Attribute namespace.** `Carbide.Bridge.CarbideExportAttribute` vs `Carbide.CarbideExportAttribute`. Prefer the former for discoverability but it requires user code to `using Carbide.Bridge;`. An implicit-using could be added via `ImplicitUsings` support once M5 lands.
- **Q2 — `Span<T>` round-trip.** `MemoryView` is a natural home for `Span<byte>`. Whether to surface this as a JS-side `Uint8Array` backed by WASM memory (zero-copy, requires pinning) or as a marshaled copy (safe, slower) is a meaningful perf decision deferred to v1.1.
- **Q3 — Debugger line numbers in `CarbideError.managedStack`.** PDB is present (we emit it); can we resolve it to source lines on the host? Probably yes via a small PDB reader in TS, but out of scope for v1.
- **Q4 — `ValueTuple` vs positional destructuring.** `(int a, string b) Foo()` — does JS see `[n, s]` or `{a, b}`? Recommend `{a, b}` using the element names Roslyn knows; needs a decision.
- **Q5 — Do we need a `HostItemFlags.GlobalMembers` analogue** — flattening a single `[CarbideExport(Global=true)]` type's members onto `bridge.module(...)` itself? Low cost; decide before M7.3.
- **Q6 — Delegate round-trip identity.** `delegate(host function a) → pass to guest → guest passes back → is it the same a?` Preserve identity via delegate-wrapper interning keyed by `JSObject` hash. Deferred to M7.5.

## 16. Risks

- **R1 — Reflection hot-path cost.** A tight loop that calls `calc.Add(1)` 1M times goes through reflection on every call. Mitigation: `MemberCache<(Type,string), MemberInfo>` plus, if needed, a `DynamicMethod`-based fast path (Mono WBT supports `System.Reflection.Emit` in .NET 10). Measure in M7.2.
- **R2 — Runtime reflection and future trimming.** If Carbide ever enables trimming, reflective dispatch on user types will break. Mitigation: generator-based fast path (approach δ) for the SDK library case; user-code case remains untrimmed by necessity.
- **R3 — Bundle-size growth.** Adding `System.Text.Json.Nodes` usage on the hot path would balloon the runtime. Mitigation: strictly source-gen'd `JsonSerializerContext` with only the DTOs we actually marshal.
- **R4 — `FinalizationRegistry` determinism.** GC timing is not guaranteed; a pathological app could accumulate handles before the JS-side GC runs. Mitigation: periodic proxy sweep triggered from the bridge client every N calls; escape hatch `bridge.collect()`.
- **R5 — Schema-bump friction.** Every DTO change bumps `SCHEMA_VERSION`. Mitigation: settle DTO shapes before M7.3 lands.
- **R6 — Bootsharp converges.** If Bootsharp adds property-access proxies before we ship, we've built the same thing twice. Mitigation: accept the risk; Carbide's in-sandbox path isn't solved by Bootsharp regardless of its feature set.

## 17. Out of scope for v1

Explicitly deferred:

- Cross-thread guest code. Mono WBT is single-threaded.
- By-reference value types.
- `ref`/`out` parameters.
- Open generic construction from JS.
- `IAsyncEnumerable<T>`.
- Attribute-granular `[CarbideAccess]`.
- `internal`/`protected` member exposure.
- JIT-emitted reflection fast path (only if R1 measures badly).
- Span-as-zero-copy `Uint8Array`.
- PDB-to-source-line rehydration in `CarbideError.managedStack`.
- Component-Model / WIT output. Future Band-C.

## 18. Success criteria

The v1 bridge is done when every item below is green.

- **S1.** A user can write a C# class with public methods, properties, and `async Task<T>` members; decorate it `[CarbideExport]`; compile with `project.build()`; receive a typed proxy on the host; call methods, read/write properties, `await` tasks, subscribe to events — all without hand-authoring a single `[JSExport]` or TS wrapper.
- **S2.** The generated `.d.ts` passes `tsc --strict` and matches the runtime proxy's shape exactly.
- **S3.** A round-trip of a CLR object `A → JS → A` preserves identity via handle caching.
- **S4.** A managed exception thrown inside a bridged method surfaces to JS as `CarbideError` with `.managedStack` and `.jsStack` populated.
- **S5.** Bundle size growth vs M5 under 5% uncompressed, under 3% brotli-compressed, on the `@carbide/core` npm tarball.
- **S6.** Zero regressions in the M1–M6 test matrix.
- **S7.** Microbenchmarks: property-get < 10µs, method-call < 20µs, `Task<int>` round-trip < 100µs on a modern laptop.
- **S8.** Parity fixtures: 30+ bridge scenarios (primitive marshaling, collections, delegates, events, exceptions, async, identity, disposal) pass on both browser (Playwright) and Node hosts.

## 19. References

### Carbide docs

- [Carbide — architecture and implementation plan](../planning/carbide-architecture-and-implementation-plan__2026-04-17__16-16-47-000000.md)
- [Carbide — vision](../carbide-vision__2026-04-17__16-16-47-000000.md)
- [JS↔C# WASM interop libraries survey](../research/js-interop/carbide-wasm-js-interop-libraries-survey__2026-04-18__21-43-55-000000__b27d950cd3b9.md) — the landscape that motivates this proposal.

### Primary technical references

- `System.Runtime.InteropServices.JavaScript` (.NET 10 namespace reference): https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.javascript?view=net-10.0
- `[JSImport]` / `[JSExport]` concept guide: https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/?view=aspnetcore-10.0
- Running .NET in the browser without Blazor (Andrew Lock, 2025-08-12): https://andrewlock.net/running-dotnet-in-the-browser-without-blazor/
- `DotNetObjectReference` ≠ `[JSImport]`: https://github.com/dotnet/aspnetcore/discussions/53866
- Local runtime reference: `lib/dotnet/runtime/src/libraries/System.Runtime.InteropServices.JavaScript/`

### Comparative references

- Microsoft ClearScript (ergonomics bar): https://github.com/microsoft/ClearScript
- Bootsharp (closest third-party bridge): https://github.com/elringus/bootsharp
- WebAssembly Component Model: https://component-model.bytecodealliance.org/
- `componentize-dotnet`: https://github.com/bytecodealliance/componentize-dotnet
- `jco`: https://github.com/bytecodealliance/jco

## 20. TL;DR

- Build an ES6-Proxy-based object bridge on top of Carbide's existing Mono WBT + `[JSExport]` foundation.
- Primary mechanism: **metadata manifest emitted by a Roslyn surface analyzer at `project.build()` time, consumed by a TS proxy factory on the host.**
- Dispatcher is a small fixed set of `[JSExport]` methods with runtime reflection on the C# side.
- Lifetime via `GCHandle` + `FinalizationRegistry`; handle caching preserves identity.
- Ergonomics reach ClearScript-level: property access, method calls, events, `Task` ↔ `Promise`, managed exceptions as `CarbideError`.
- Opt-in visibility via `[CarbideExport]`.
- Phased across M7.1 → M7.4. M7.5 adds polish.
- Optional SDK-time source generator (approach δ) for library authors, deferred until demand materializes.
- Keep `CompilationInterop` on JSON strings — do not bridgify the control plane.
