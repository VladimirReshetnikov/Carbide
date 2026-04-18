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
