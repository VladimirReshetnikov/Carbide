# Carbide — upstream drift tracking

Created (UTC): 2026-04-18T00:50:48Z
Repository HEAD: 998993db6ec3772559b081548ea6817356e4a373

Periodic upstream-drift reports live here, per [architecture §11](../carbide-architecture-and-implementation-plan__2026-04-17__16-16-47-000000.md#11-supply-chain-and-maintenance). A drift report records, at its time of writing:

- Roslyn newest released version vs. the Carbide-pinned version (currently `4.14.0`).
- .NET runtime newest LTS/STS vs. Carbide-targeted TFM (currently `net10.0` via `Microsoft.NET.Sdk.WebAssembly`).
- Any new issues filed in `dotnet/runtime` or `dotnet/roslyn` labelled against WASM/browser since the last report.
- Deltas in `wasm-tools` workload manifest used for builds (currently `10.0.200-manifests.*` on SDK `10.0.201`).

## Seed report

No report filed yet. First one to be produced after M1 lands and CI is wired up.

## Documented differences (M1)

Carbide inherits Mono-WASM's single-threaded model and the post-publish shape of the runtime. Two M1-specific deviations from "a dotnet CLI running locally" are worth surfacing to users:

- **Newline is `\n`, not `\r\n`.** `Console.WriteLine` inside Mono-WASM emits `"\n"` regardless of the host OS, because `Environment.NewLine` is `"\n"` on WASM. Carbide does not rewrite output. Tests and fixtures should expect `"\n"`.
- **AppDomain-less assembly caching.** Each call to `Project.run()` does `Assembly.Load(byte[])` and invokes the entry point. Mono-WASM has no AppDomain isolation, so types loaded by one run linger for the life of the process. Two consecutive runs with the same assembly name may see stale state. Users who need isolation between runs should dispose and reinitialise the session.
- **Trimming disabled in M1.** `IsTrimmable=false` and `PublishTrimmed=false` are set on `Carbide.Core.csproj` because the trimmer strips BCL methods that the user's compilation expects (e.g. `Console.Write`). M3 restores trimming once `@carbide/refs-net10.0` ships untrimmed reference assemblies that feed Roslyn, keeping the runtime trimmed. Tracked in [architecture §13 Q.3](../carbide-architecture-and-implementation-plan__2026-04-17__16-16-47-000000.md#13-open-questions-architecture-positions).
- **Node metadata fetches go over HTTP, not file://.** The `NodeHostAdapter` starts a localhost asset server by default because Mono-WASM's HttpClient cannot stream a file:// response body reliably (reports 0 content-length and truncates reads). The browser host serves from same-origin HTTP naturally. Revisit when the Mono-WASM file:// fetch shim exposes bytes as a proper stream.

## Documented differences (M2)

- **Document paths are byte-for-byte opaque identity.** Carbide does not lower-case, canonicalise slashes, or trim whitespace from the `path` argument to `addSource` / `updateSource` / `removeSource`. `"helper.cs"` and `"Helper.cs"` are two different documents; diagnostics echo whatever string the caller passed. Windows-vs-Unix casing is not normalised because there is no correct answer. [M2 D17.]
- **One top-level-statements file per project.** Carbide inherits Roslyn's CS8802. If two user-added files contain top-level statements, compilation fails and the diagnostic names the offending file. The hidden `Carbide.GlobalUsings.g.cs` document contains only `global using` directives, which do not count as top-level statements. [M2 D19.]
- **`Carbide.GlobalUsings.g.cs` is a reserved path.** Attempting to `addSource` / `updateSource` / `removeSource` that exact path throws. The document is owned by Carbide's implicit-usings machinery and not exposed to callers. [M2 D18.]
- **`addSource` on a duplicate path throws; `removeSource` on an unknown path is a no-op.** Accidental double-add is almost always a bug and silent overwrite hides it. Removal tolerates missing paths so teardown code doesn't need a try/catch per file. `updateSource` throws on unknown paths. [M2 D15, D16.]

## Documented differences (M3)

- **Reference bytes are validated synchronously.** `session.addReference(bytes, name)` throws before returning a handle if the bytes lack a CLR metadata directory. Roslyn's later CS0009 "PE image doesn't contain managed metadata" diagnostic is the slow path; Carbide prefers the fast one. [M3 D29.]
- **References are session-scoped; handles encode `sessionId`.** Attaching a handle from session A to a project in session B throws synchronously at the TS layer; the C# side double-checks. Handles become `disposed: true` after `session.removeReference` or `session.shutdown`. [M3 D26, D28.]
- **Base64 at the JSExport boundary, bytes in the public API.** The TS surface accepts `Uint8Array` (architecture Q.2). The C# JSExport receives base64-encoded strings and decodes internally — cheaper marshalling than `Uint8Array` today. [M3 D23, D24.]
- **`@carbide/refs-net10.0` is an opt-in sibling package.** When installed (postinstall generates `ref/net10.0/*.dll` from the pinned `Microsoft.NETCore.App.Ref` nupkg), Carbide uses it as the compile-time API surface instead of the runtime's trimmed BCL. When absent, Carbide falls back to the runtime DLLs (M1/M2 behaviour). No DLLs are committed to the repo. [M3 D31, D32, D34.]
- **Ref-pack wins: no merging with the runtime.** When the ref-pack is available, Carbide feeds Roslyn *only* the ref-pack DLLs. Mixing ref-pack (untrimmed reference assemblies) and runtime (trimmed implementations) produces duplicate type definitions and CS0518 "Predefined type not defined" errors. The runtime continues to hold its own DLLs for execution. [M3 `boot.ts` `resolveRefPackUrls` / InitAsync call site.]
- **Attached references are pre-loaded before the user's PE runs.** Mono-WASM's default AssemblyLoadContext doesn't reliably resolve Assembly.Load(byte[])'d dependencies lazily, so Carbide calls `Assembly.Load` on every attached reference and registers an `AppDomain.AssemblyResolve` handler before loading the user's PE. The handler is removed in `finally` after the entry point returns. [M3 `ProjectCompiler.RunAsync`.]
- **Trimming stays off in M3 despite the ref-pack.** The architectural position (Q.3) calls for trimmed runtime + untrimmed ref-pack, but the trimmer decides what to keep based on Carbide.Core's own usage, not the user's. Re-enabling `<PublishTrimmed>true</PublishTrimmed>` would still risk stripping BCL members the user's emitted PE binds at JIT time. The ref-pack now decouples the *compile-time* surface from runtime trim state — that's the M3 win — but restoring the runtime trim requires ILLink work deferred to a later milestone. [M3 plan §5 D35.]
