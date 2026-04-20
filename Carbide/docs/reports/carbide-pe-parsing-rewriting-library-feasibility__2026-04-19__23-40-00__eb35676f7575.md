# Feasibility: Running Major PE Parsing/Re-Writing Libraries On Carbide

- Status: Informational (feasibility report)
- Audience: Carbide maintainers, consumers planning to bring IL-tooling workloads to Carbide, and reviewers of a future allow-list expansion
- Scope: running **Mono.Cecil**, **AsmResolver** (and its sub-packages), **dnlib**, and the in-box **`System.Reflection.Metadata` / `System.Reflection.PortableExecutable`** stack against `src/Carbide` today, with a reasonable extension of Carbide and/or a lightly forked consumer wrapper where necessary
- Created (UTC): 2026-04-19T23:40:00Z
- Repository HEAD: f704d10238980f1fcfbda8577ba51a844919e068
- Related code:
  - `../../packages/core/src/Services/ProjectCompiler.cs`
  - `../../packages/core/src/Services/ReferenceRegistry.cs`
  - `../../packages/core/src/Services/WasmMetadataReferenceResolver.cs`
  - `../../packages/nuget/src/allowlist.ts`
  - `../../packages/nuget/src/safety.ts`
  - `../../packages/nuget/src/tfm-compat.ts`
  - `../../../../lib/cecil/Mono.Cecil.csproj`
  - `../../../../lib/cecil/Mono.Cecil.nuspec`
  - `../../../../lib/asmresolver/src/Directory.Build.props`
  - `../../../../lib/asmresolver/src/AsmResolver/Shims/MemoryMappedFileShim.cs`
  - `../../../../lib/asmresolver/src/AsmResolver.DotNet/ModuleDefinition.cs`
  - `../../../../lib/dnlib/src/dnlib.csproj`
  - `../../../../lib/dnlib/src/IO/DataReaderFactoryFactory.cs`
- Related docs:
  - [Carbide current-state guide](../Carbide-Current-State-Guide.md)
  - [Carbide drift notes](../drift/README.md)
  - [Carbide usability report](Carbide-Usability-Report.md)

## Executive summary

**Short answer:** yes. Running Mono.Cecil, AsmResolver, or dnlib inside Carbide is feasible today for **read, analyze, and re-write** workloads operating on `byte[]` input and `byte[]` output, with two concrete Carbide-side changes and one user-facing Carbide extension if binary PE output needs to flow back out cleanly. The in-box `System.Reflection.Metadata` stack is already working inside Carbide and can be used right now with **zero** Carbide changes.

The important splits:

- **Pure managed, no-native, byte-in/byte-out code paths work on Mono-WASM today.** Roslyn already runs in Carbide on top of `System.Reflection.Metadata` and `System.Reflection.PortableExecutable`; those two live in the ref-pack and in the shipped WASM runtime DLL set. Carbide validates PE input via `PEReader` in `WasmMetadataReferenceResolver.cs`. Mono.Cecil, AsmResolver, and dnlib each have code paths that stay entirely inside this "managed, `byte[]`-backed" envelope.
- **Each of the three libraries also has native-bound code paths that must be avoided.** Memory-mapped file loaders (AsmResolver, dnlib), native Windows PDB writers (Cecil's `Mono.Cecil.Pdb`, dnlib's `Dss` DIA symbol writer), and AsmResolver.DotNet.Dynamic's dynamic-method bridge all assume a real Windows/Unix runtime and will throw on browser-WASM. These paths are opt-in and easy to sidestep — but they must be sidestepped, not unknowingly triggered.
- **Carbide's NuGet allow-list needs to grow, but the existing safety filters do not need to be loosened.** `Mono.Cecil`, `AsmResolver` / `AsmResolver.PE` / `AsmResolver.PE.File` / `AsmResolver.DotNet` / `AsmResolver.PE.Win32Resources` / `AsmResolver.Symbols.Pdb`, and `dnlib` are all managed-only nupkgs with no `runtimes/<rid>/native/`, no `build/*.targets`, and no `analyzers/` payloads in their published artifacts; they will pass `@carbide/nuget`'s safety gate as-is.
- **The binary-output gap is real but contained.** Carbide's stdout capture is `Console.SetOut`-based, and its JSExport boundary marshals user PE bytes as base64 only on the *input* side today. Returning rewritten PE bytes out of a user program cleanly is the single piece that needs a small new interop surface. Shape is described in §5.

Recommended sequencing: start with **`System.Reflection.Metadata`** (no changes needed), then allow-list **Mono.Cecil** as the easiest full library (single nupkg, zero deps, netstandard2.0), then **AsmResolver** (richer API, higher churn, more interesting test matrix — also the library `src/CilTools` already uses, so cross-repo synergy), then **dnlib** if there is a concrete pull. `Mono.Cecil.Pdb`, `AsmResolver.DotNet.Dynamic`, and dnlib's `Dss` PDB writer are the explicit non-goals and should not be allow-listed.

## Why Carbide makes this specifically interesting

`src/Carbide` is a Mono-WASM-hosted Roslyn-plus-runtime bundle that accepts user-supplied PE bytes (`session.addReference(bytes)`), compiles and emits new PE bytes (`project.build()`), and optionally loads-and-runs the emitted PE in-process. PE parsing and re-writing libraries are, by design, the exact complement: they accept PE bytes and produce PE bytes.

Three properties of Carbide make this a non-trivially strong fit:

1. **A Mono-WASM runtime is already shipped.** The hard part — booting `.NET` without an SDK — is solved. A PE-rewriting library hosted on top of that runtime inherits the whole "npm install, no SDK, works in Node and browser" story for free.
2. **Roslyn is already on the compile-time surface.** A user can compile a program that *uses* Mono.Cecil against the ref-pack and then have that program execute against Cecil at run time inside Carbide. No additional compiler path needed.
3. **Carbide's reference registry already accepts arbitrary managed PE bytes.** `ReferenceRegistry.Add` validates the PE, stores the bytes, and hands a `MetadataReference` to Roslyn. A cached Mono.Cecil DLL from NuGet, or a DLL the caller ships via `session.addReference`, lands in the exact same pipe.

## Carbide constraints recap (what the libraries have to respect)

From `packages/core/src/Carbide.Core.csproj`, `packages/core/src/Services/*.cs`, and `packages/nuget/src/*.ts`:

- **Runtime.** `Microsoft.NET.Sdk.WebAssembly` on `net10.0`, `RuntimeIdentifier=browser-wasm`, trimming disabled (`IsTrimmable=false`, `PublishTrimmed=false`). Mono-WASM, single-threaded.
- **Reference surface.** Compiles against `@carbide/refs-net10.0` (untrimmed ref-pack) when present; falls back to runtime DLLs. `System.Reflection.Metadata.dll`, `System.Reflection.Emit.dll`, and `System.IO.MemoryMappedFiles.dll` are all present in the ref-pack (`packages/refs-net10.0/ref/net10.0/`).
- **Execution.** `Assembly.Load(byte[])` + `AppDomain.CurrentDomain.AssemblyResolve` bridge in `ProjectCompiler.RunAsync`. No AppDomain isolation between consecutive runs.
- **Stdio capture.** `Console.SetOut(StringWriter)` catches text writes; bytes written via `Console.OpenStandardOutput()` bypass the capture and surface as raw bytes on the Node process's own stdout (documented sharp edge; U1 added a sentinel line before the JSON trailer to let callers locate the payload even when mixed).
- **NuGet policy.** Strict allow-list by default (`packages/nuget/src/allowlist.ts`). `--allow-list-mode advisory` warns-and-continues, `off` disables the gate. Safety refusals are unconditional for `runtimes/<rid>/native/`, `build/*.targets`, `build/*.props`, and `analyzers/` payloads (codes `MSNUGET015–017`).
- **TFM resolution.** `packages/nuget/src/tfm-compat.ts` parses only `net{M}.{N}` and `netstandard{M}.{N}`. Framework-era labels (`net40`, `net45`, `net462`, `net35`, `netcoreapp3.1`) are not accepted as candidate `lib/<tfm>/` folders. `net10.0` consumers walk down `net10.0 → net9.0 → … → net6.0 → netstandard2.1 → netstandard2.0`.
- **Package deps are by selected TFM group.** The nuspec's per-TFM `<group>` dictates transitive deps. When the net10.0 lib folder is picked but the nuspec has no `net10.0` dep group, the resolver falls down to the highest-score compatible group.
- **PE input validation.** `WasmMetadataReferenceResolver.HasManagedMetadata` uses `System.Reflection.PortableExecutable.PEReader` on every user-supplied DLL. This is direct proof that `System.Reflection.Metadata` works on Mono-WASM in Carbide's configuration.
- **WASM "what throws" envelope.** `[UnsupportedOSPlatform("browser")]` applies broadly. The ones relevant here: `System.IO.MemoryMappedFiles.*`, most of `System.Console` input/resize/color, `Process.Start`, parts of `System.Runtime.InteropServices` loading path. Reflection (including `Reflection.Emit` for non-collectible assemblies) works on the interpreted path, but `System.Reflection.Emit` under browser-WASM has historical gaps and should be assumed best-effort rather than production-grade unless proven by a fixture.

This is the envelope. Anything that stays inside it — managed, `byte[]`- or `Stream`-backed, no mmap, no Win32 PDB — is green.

## The four candidates

### 1. `System.Reflection.Metadata` / `System.Reflection.PortableExecutable` (already in-box)

**Status on Carbide today: works.** This is not hypothetical — it is the baseline that everything else is measured against.

- `System.Reflection.Metadata.dll` ships in `@carbide/refs-net10.0` and in the runtime DLL set, and `Carbide.Core`'s own `WasmMetadataReferenceResolver.HasManagedMetadata` already uses `PEReader` to validate every user-supplied DLL.
- Roslyn's `Compilation.Emit` in Carbide produces PE bytes via `System.Reflection.Metadata.Ecma335.MetadataBuilder` / `System.Reflection.PortableExecutable.ManagedPEBuilder` underneath the hood. That path is exercised on every `project.build()` and is the backbone of Carbide's deterministic-build contract (M5 D53).
- Portable-PDB emission is exercised on the same path (`ProjectCompiler.EmitPeAndPdb`).

**Implication.** A Carbide consumer who wants to do read-only PE analysis or low-level PE writing today can import `System.Reflection.Metadata` directly. No allow-list change, no safety-filter concession, no new Carbide surface. The only cost is that the API is lower-level than Cecil/AsmResolver/dnlib: no convenient object model for types/methods/instructions, no peer-and-paste IL editing, no transparent symbol handling. For simple passes (inspecting metadata tables, reading custom attributes, writing new signature blobs, producing a small PE), it is enough. For anything that wants a mutable object graph, the higher-level libraries win on ergonomics.

Recommendation: document this as the "zero-cost" option in a Carbide usage note, then proceed to allow-list the libraries below as the usability-forward option.

### 2. Mono.Cecil

**Status on Carbide today: blocked only by the allow-list.**

**Package shape.** `lib/cecil/Mono.Cecil.nuspec` targets `net40` and `netstandard2.0`. Both dep groups are empty (zero transitive NuGet deps). Carbide's TFM matcher rejects `net40` but accepts `netstandard2.0`, so the resolver picks `lib/netstandard2.0/Mono.Cecil.dll`. `netstandard2.0` consumes a `net10.0` host at runtime without additional facades.

**Payload shape.** The main `Mono.Cecil` nupkg carries only the main DLL (plus PDB). The three sibling nupkgs are distinct:
- `Mono.Cecil.Rocks` — pure managed extensions; allow-listable.
- `Mono.Cecil.Pdb` — **contains Windows DIA / native PDB P/Invoke (`ole32.dll` plus `CLSIDFromString`).** Not usable on WASM. Non-goal.
- `Mono.Cecil.Mdb` — Mono debugger symbols. Managed, unlikely to be interesting on Carbide.

**WASM-dangerous API surface inside `Mono.Cecil` itself.** Two places:
- `BaseAssemblyResolver.GetCorlibPath` reads `AppDomain.CurrentDomain.GetData("TRUSTED_PLATFORM_ASSEMBLIES")` and falls back to `Directory.GetParent(...)` walks. On Mono-WASM the TPA string is typically empty / missing, so the resolver would return null for external references. **Mitigation:** construct `ModuleDefinition.ReadModule(stream)` *without* a resolver, or pass a user-supplied `IAssemblyResolver` that satisfies references from `session.addReference` bytes. Local reading and re-writing of a single module do not need the resolver.
- `ModuleDefinition.ReadModule(string filePath)` opens a `FileStream`. Works in Node if the caller can write the bytes to `/tmp`, but there is no Node fs that Mono-WASM can write to without help. **Mitigation:** prefer the `Stream` / `byte[]` overload (`ModuleDefinition.ReadModule(new MemoryStream(bytes))`).

**Writing path.** `ModuleDefinition.Write(Stream)` is the canonical output. With `WriterParameters { SymbolWriterProvider = new PortablePdbWriterProvider() }`, portable PDB writing stays in the main assembly (no DIA) and is known to work. The native `Mono.Cecil.Pdb` provider is the sole Win32-bound path and should not be used.

**What Carbide needs to do.** Add `Mono.Cecil` (and optionally `Mono.Cecil.Rocks`) to `packages/nuget/src/allowlist.ts`, with a new fixture program under `packages/core/test/fixtures/` that demonstrates reading a Carbide-built DLL, adding a method, and writing it back as `byte[]`. Expected allow-list entry size: ~1 file touched, ~10 LOC added; fixture size: ~1 new `.cs` file plus a NuGet lock snapshot. No safety-filter change required.

**Out-of-scope inside the allow-list entry.** `Mono.Cecil.Pdb` and `Mono.Cecil.Mdb` should stay unlisted until a credible WASM story exists for them (portable PDB already covers the useful subset).

### 3. AsmResolver (six-ish nupkgs)

**Status on Carbide today: the managed core is clean; one sibling package is out of scope.**

**Package shape.** From `lib/asmresolver/src/Directory.Build.props`:

```
TargetFrameworks = net10.0 ; net9.0 ; net8.0 ; net6.0 ; net462 ; net472 ; net35
                    ; netcoreapp3.1 ; netstandard2.0 ; netstandard2.1
```

For a `net10.0` Carbide consumer, `lib/net10.0/` is the exact-match folder. The nuspec's `net10.0` dep group is empty for every sibling nupkg (the only real PackageReferences — `MonoMod.Backports`, `System.Runtime.InteropServices.RuntimeInformation` — are gated by `.NETFramework` / `net462` via `<ItemGroup Condition="…">`). No transitive deps fire for Carbide's TFM.

**Relevant sibling nupkgs.**

| Nupkg | WASM fitness | Notes |
|---|---|---|
| `AsmResolver` | Green | Byte-level primitives, `DataSegment`, `SegmentBuilder`, the shared IO layer. Has `Shims/MemoryMappedFileShim.*` internally but only used from `MemoryMappedInputFile` / `MemoryMappedFileService`, which are only entered if you open a file by path. |
| `AsmResolver.PE.File` | Green | PE container model (headers, sections, data directories). Byte-backed. |
| `AsmResolver.PE` | Green | High-level PE image model (import/export/reloc/tls/resources). Byte-backed. |
| `AsmResolver.PE.Win32Resources` | Green | Managed-only win32 resource walker; no OS calls. |
| `AsmResolver.DotNet` | Green | `ModuleDefinition.FromBytes(byte[])` / `.FromStream(Stream)` / `.Write(Stream)` bypass mmap; `FromFile(string)` / `FromModuleBaseAddress(IntPtr)` go through mmap / native module handles and are non-goals for WASM. |
| `AsmResolver.Symbols.Pdb` | Green (portable) | Windows PDB model; pure managed. Does not require `Microsoft.DiaSymReader.Native`. |
| `AsmResolver.DotNet.Dynamic` | **Red** | Targets runtime-dynamic method rewriting via reflection-emit. Not a PE-rewriting need; assumes a non-browser runtime. Keep out of the allow-list. |

**WASM-dangerous paths to route around inside the green sub-packages.**
- `MemoryMappedFileService` (opened on `ModuleDefinition.FromFile(string)`): kernel32 / libc P/Invoke inside `Shims/MemoryMappedFileShim.Windows.cs` / `MemoryMappedFileShim.Unix.cs`. On Mono-WASM these resolve to unavailable native libraries and throw on the *call*, not on load. AsmResolver has both a `MemoryMappedFileService` and a `ByteArrayFileService` (`UncachedFileService` uses `ByteArrayInputFile`), so the byte-array path is an explicit first-class citizen. Prescribe it in docs.
- `DotNetCorePathProvider` / `DotNetFrameworkAssemblyResolver` walk `Environment.GetFolderPath(SpecialFolder.ProgramFiles)` and `Directory.GetDirectories`. Only fires if a caller wires an `AssemblyResolver` into the reader parameters. Byte-in/byte-out flows do not trigger it.
- `StrongNameSigner` uses `System.Security.Cryptography.RSA`. Works on Mono-WASM (pure managed fallback) but has been a historical source of friction; treat as "likely works, verify with a fixture."

**Source-generator risk (checked, benign).** `AsmResolver.DotNet.csproj`, `AsmResolver.PE.csproj`, `AsmResolver.PE.File.csproj`, `AsmResolver.PE.Win32Resources.csproj`, `AsmResolver.Symbols.Pdb.csproj` each carry a `ProjectReference` to `AsmResolver.SourceGenerators` with `ReferenceOutputAssembly="false"` + `OutputItemType="Analyzer"`. That consumes the generator at *AsmResolver's* build time only; the published nupkgs do not include the generator DLL under `analyzers/dotnet/cs/`. (The `AsmResolver.SourceGenerators.csproj` itself does pack under `analyzers/dotnet/cs`, but that is a standalone internal nupkg not shipped as a release artifact — and if it ever appeared, Carbide's `MSNUGET017` filter would refuse it unconditionally, which is the right behavior.) Verify this by hashing the real `lib/net10.0/*.dll` set against an empty-`analyzers/` listing when the fixture is authored.

**What Carbide needs to do.** Add five entries to the allow-list: `AsmResolver`, `AsmResolver.PE.File`, `AsmResolver.PE`, `AsmResolver.PE.Win32Resources`, `AsmResolver.DotNet`, and (if symbol support is desired) `AsmResolver.Symbols.Pdb`. Author a fixture that uses `AsmResolver.DotNet` on a Carbide-emitted PE, adds a type, re-emits, and validates via `PEReader`. Expected allow-list entry: ~6 file-LOC-level entries in `allowlist.ts`; fixture size: ~1 new `.cs` file plus a NuGet lock snapshot; test coverage: parallel to `packages/core/test/node/corpus/*`. No safety-filter change required.

**Why this is especially attractive.** `src/CilTools` (same repo) already consumes `AsmResolver.DotNet` / `AsmResolver.PE.File` — so allow-listing AsmResolver on Carbide is a direct bridge for "let the CilTools API run inside Carbide with no additional Carbide changes." That bridge is not free on other libraries.

### 4. dnlib

**Status on Carbide today: works with an allow-list entry and the `byte[]` loader; full-fidelity PDB is constrained.**

**Package shape.** `lib/dnlib/src/dnlib.csproj` targets `netstandard2.0`, `net45`, `net6.0` (and optionally `net35`). Carbide's TFM matcher rejects `net45` and picks `net6.0` (scores higher than `netstandard2.0`). The `netstandard2.0` dep group carries `System.Reflection.Emit 4.7.0` and `System.Reflection.Emit.Lightweight 4.7.0`; `net6.0` carries **none**. Picking `net6.0` is therefore the clean path and avoids both packages. Still allow-listable as a single `dnlib` entry because its own `net6.0` group is deps-free.

**PE loading.** `ModuleDefMD.Load(byte[] data, ...)` exists alongside `ModuleDefMD.Load(string fileName, ...)`. The byte path goes through `MetadataFactory.Load(byte[])` and never touches `DataReaderFactoryFactory`, so the mmap code is dead for bytes. When `Load(string)` is used, `DataReaderFactoryFactory.Create` **already has a graceful fallback** to `ByteArrayDataReaderFactory.Create(File.ReadAllBytes(fileName))` when the platform-specific mmap factory returns null — which it will on Mono-WASM because `kernel32` / `libc` P/Invokes fail. So path-based loading also works, just via the fallback.

**PDB situation.**
- **Portable PDB** reading and writing are in-box in dnlib's main assembly; zero native dependency.
- **Windows native PDB** (`dnlib.DotNet.Pdb.Dss`) imports `Microsoft.DiaSymReader.Native.{x86,amd64,arm,arm64}.dll` P/Invokes. These are only called if the user explicitly chooses the Dss writer. The README calls `Microsoft.DiaSymReader.Native` out as an *optional* companion package, not a dependency. Its absence is a functional limitation (no Windows PDB write), not a load-time failure.
- **Net throw risk.** On Mono-WASM, the `Dss` writer constructor calls `DllImport("Microsoft.DiaSymReader.Native.x86.dll")`. The call throws `DllNotFoundException` at invocation. Do not initialize that writer.

**Reflection.Emit dependency.** `dnlib.DotNet.Emit.MethodTableToTypeConverter` uses `AppDomain.CurrentDomain.DefineDynamicAssembly(...)` — pure `Reflection.Emit`. Invoked only when converting a runtime `System.Type` back to a metadata token through the heuristic path. Not on the common `byte[] → ModuleDefMD → mutate → Write(byte[])` route. Plays along with Mono-WASM's interpreter if the path is ever hit; do not rely on it in production without a fixture.

**What Carbide needs to do.** Add `dnlib` to the allow-list. Because dnlib's `.nupkg` is ~1 MB (vs. Cecil's ~300 KB), callers should be aware of the payload cost. Author a fixture that covers the same "round-trip Carbide-emitted DLL" flow as Cecil, but with a representative dnlib API (`ModuleDefMD.Load(byte[])` + `module.Write(Stream)`). No safety-filter change required.

**Out of scope for the allow-list entry.** `Microsoft.DiaSymReader.Native` must not be allow-listed; it is an explicitly native payload and would be refused by `@carbide/nuget`'s safety filter anyway (`MSNUGET015`).

## The binary-output gap

The only genuine Carbide-shape limitation is getting rewritten **binary** PE bytes out of a user program cleanly.

Today's paths:

1. **Text-only output.** User program writes a base64-encoded PE via `Console.WriteLine(Convert.ToBase64String(bytes))`. Stdout capture in `ProjectCompiler.RunAsync` catches the string, the CLI emits it as part of the JSON trailer, the caller `atob`s it. Works today with zero Carbide changes. ~33% payload overhead and one extra base64 hop per direction.
2. **Raw byte bypass.** User program writes via `Console.OpenStandardOutput().Write(bytes)`. Bypasses `Console.SetOut` capture; raw bytes appear on the CLI's own stdout and can corrupt a `--format json` consumer. U1 added the `\x1E\x1Ecarbide-json\x1E\x1E\n` sentinel so consumers can locate the JSON trailer, but this still assumes the caller is willing to parse a mixed-stream payload. Not ergonomic for binary PE return.
3. **No first-class binary return.** `RunResult` today carries `stdOut`, `stdErr`, `exitCode`, `uncaughtException`, diagnostics, and duration — no `outputBytes`.

**Proposed Carbide extension (one-time, ~150 LOC):**

- A session-scoped, session-side byte buffer — `session.createOutputSink(): SinkHandle` plus a C# counterpart accessed from user code via `[JSImport]` (e.g., `CarbideInterop.WriteOutputBytes(byte[])`).
- `project.run(sinkHandle)` returns `outputBytes: Uint8Array` alongside the usual `RunResult`.
- Base64 on the JSExport boundary, consistent with how references and PE bytes already cross today (M3 D24, M4 D38).

This single addition unlocks clean binary round-trips for *any* rewriter, not just Cecil/AsmResolver/dnlib. It is additive, does not require schema-version churn beyond a single bump (likely interop `SCHEMA_VERSION = 4`), and reuses existing base64 plumbing. If the binary-output story is deferred, the base64-in-text workaround is perfectly workable for fixtures and acceptance tests.

## What extensions Carbide needs, summarized

| Need | Status | Size estimate | Notes |
|---|---|---|---|
| `System.Reflection.Metadata` usage | **No change needed** | 0 files | Already proven by `WasmMetadataReferenceResolver` |
| Allow-list entries for `Mono.Cecil` | Additive | ~1 file, ~10 LOC | `packages/nuget/src/allowlist.ts` entry plus test fixture |
| Allow-list entries for `AsmResolver` family (5 nupkgs) | Additive | ~1 file, ~30 LOC | One allow-list entry per shipped nupkg; same fixture pattern |
| Allow-list entry for `dnlib` | Additive | ~1 file, ~10 LOC | Same shape as Cecil |
| Fixture programs + golden tests | Additive | 3 fixtures × ~1 `.cs` + `carbide.lock.json` | Mirrors existing `packages/core/test/node/corpus/*` |
| `session.createOutputSink()` + byte-return interop | New surface (optional, one-time) | ~150 LOC across `@carbide/core` C# + TS + `@carbide/cli` | Enables clean binary PE output from user programs |
| Carbide documentation | Additive | 1 guide doc | "Using PE-rewriting libraries inside Carbide" note |

Nothing on this list requires a safety-filter concession. Nothing requires forking any of the libraries. Nothing requires a new TFM to be added to `tfm-compat.ts`. The Carbide footprint is dominated by the optional binary-output sink; without it, the feasibility is already unblocked.

## Potential forks and when to consider them

**When a fork is not needed.** For byte-in/byte-out PE rewriting on Mono-WASM, none of the three libraries need a fork. Their managed cores are already positioned for this shape.

**When a fork might help.**
- **Mono.Cecil.** If the `BaseAssemblyResolver` fallback to `AppDomain.CurrentDomain.GetData("TRUSTED_PLATFORM_ASSEMBLIES")` becomes a common footgun, a small patch to route through a Carbide-aware default resolver (that queries the session's `ReferenceRegistry`) would remove friction. ~1 file, < 50 LOC. Carbide's consumer wrapper in `@carbide/core` can also inject a `DefaultAssemblyResolver` override without touching Cecil, which is almost certainly the right place to fix it.
- **AsmResolver.** If `MemoryMappedFileService` ends up being the default under some API path a consumer must use, a Carbide-side wrapper that forces `UncachedFileService` is a one-liner in the consumer; no fork needed. A more invasive change — e.g. making `MemoryMappedFileShim` gracefully fall back to `File.ReadAllBytes` under browser-WASM — would be friendly upstream but optional.
- **dnlib.** The existing `DataReaderFactoryFactory.Create` already falls back to bytes. No fork needed.

In short: **the consumer wrapper layer inside Carbide's eventual `carbide-il-tooling` fixture (or a future dedicated package) is where sharp edges should be sanded, not in the upstream libraries.**

## Test and fixture recommendations

Each new allow-list entry should be backed by a fixture and a test that exercises a realistic round-trip. Prescribed shape:

1. **Input.** A deterministically built Carbide fixture DLL (can reuse or adapt `packages/core/test/fixtures/helper-dll/MyHelper.dll`).
2. **Pass.** User program loads the fixture DLL via `session.addReference` bytes, opens it with the target library (Cecil `ModuleDefinition.ReadModule`, AsmResolver `ModuleDefinition.FromBytes`, dnlib `ModuleDefMD.Load`).
3. **Mutation.** A small, token-clean change — add a public static method that returns a string, or insert a `DebuggerDisplayAttribute` on an existing type.
4. **Output.** Re-emit to `byte[]`, base64-encode, print once on stdout.
5. **Verification.** Node-side test decodes the base64, validates via `PEReader`, and (optionally) feeds the PE back into Carbide and invokes the new method, asserting the expected return value.

Three such fixtures — one per library — is enough coverage to claim the allow-list entries are functional. A fourth "System.Reflection.Metadata" fixture is useful as a low-level regression shield even though it does not need an allow-list entry.

**Fixture count:** 3 (or 4) new `.cs` files plus their small metadata. **CI time:** each run is a compile-plus-execute inside an existing Carbide session — no additional boot cost beyond what `packages/core/test/node/corpus/*` already pays.

## Risks and explicit non-goals

**Risks to track.**
1. **Upstream source-generator shape drift.** If a future AsmResolver release accidentally bundles the internal source generator under `analyzers/dotnet/cs` in a consumer-facing nupkg, Carbide's safety filter will refuse the whole resolution with `MSNUGET017`. The filter is correct; the right response is to flag it upstream. Keep an eye on per-version drift; version-pinning is already the norm for Carbide lock files.
2. **Reflection.Emit-shaped edges in dnlib's `MethodTableToTypeConverter`.** Do not promise support for flows that convert runtime `System.Type`s to metadata tokens via dnlib until a fixture proves it on Mono-WASM.
3. **`StrongNameSigner` / RSA on Mono-WASM.** Strong-name signing is advertised but rarely exercised on browser-WASM. If a consumer depends on it, add a dedicated fixture.
4. **Trimmer interactions.** Carbide keeps the runtime untrimmed today (`IsTrimmable=false`, drift M3). If Carbide ever re-enables trimming (plan Q.3), reflection-heavy libraries like dnlib and AsmResolver.DotNet will need rooted in `TrimmerRootDescriptor`. Treat that as a re-validation gate, not a blocker today.
5. **Binary output without the sink.** The base64-through-stdout workaround is fine for fixtures, but consumers writing multi-MB rewritten PEs per call will notice the overhead. Address with the optional sink extension when the pull becomes real.

**Explicit non-goals.**
- **`Mono.Cecil.Pdb` (native Windows PDB writer).** Depends on `ole32.dll`. Do not allow-list.
- **`AsmResolver.DotNet.Dynamic`.** Runtime dynamic-method rewriting is not a PE-rewriting shape. Do not allow-list.
- **`dnlib.DotNet.Pdb.Dss`** (as a user-selected writer). Depends on `Microsoft.DiaSymReader.Native`. Do not allow-list.
- **`Microsoft.DiaSymReader.Native`** itself. Native payload. Already auto-refused by `MSNUGET015` — no action needed.
- **Large disassemblers as a Carbide bundle (ILSpy, dnSpy).** These are UI apps. Not in scope for this report; out of Carbide's "bounded console program" envelope as articulated in the current-state guide.

## Recommendation

1. **Document that `System.Reflection.Metadata` already works** inside Carbide. Zero code change; one short usage note.
2. **Add `Mono.Cecil` to the allow-list first**, with one round-trip fixture. Smallest delta, clearest payoff, directly validates the byte-in/byte-out contract.
3. **Add the five core `AsmResolver` nupkgs to the allow-list next**, with a `CilTools`-style fixture. This is the highest-leverage entry because it unlocks cross-pollination with `src/CilTools`.
4. **Add `dnlib` only on concrete demand.** Its payload is larger and its PDB story is more constrained; there is no shortage of PE-rewriting options before it becomes essential.
5. **Defer the `session.createOutputSink()` interop surface** until a consumer hits the binary-output gap in practice. The base64-through-stdout workaround is adequate for fixtures and for first-wave experimentation.
6. **Treat `Mono.Cecil.Pdb`, `AsmResolver.DotNet.Dynamic`, `dnlib.DotNet.Pdb.Dss`, and `Microsoft.DiaSymReader.Native` as hard non-goals.** Record them in the allow-list's refused-by-policy note so future allow-list PRs do not relitigate.

**Overall verdict.** Carbide's existing architecture is, almost coincidentally, a strong fit for managed PE parsing and re-writing. The three major libraries can run inside it today with a minimum of three allow-list entries, three fixtures, and an optional binary-output sink for the last mile.
