# Feasibility: running major PE parsing and rewriting libraries on `src/Carbide`

- Created (UTC): 2026-04-20T01:07:50Z
- Repository HEAD: f704d10238980f1fcfbda8577ba51a844919e068

Status: feasibility / architecture report. Evaluates whether major managed PE and .NET metadata libraries can run inside Carbide's current browser-WASM host, and what Carbide or library adaptations are reasonable when they do not work out of the box.

Audience: repository owner and future implementers.

Scope: evidence from [`src/Carbide/`](../../README.md), local library snapshots under [`lib/cecil`](../../../../../lib/cecil/README.md), [`lib/asmresolver`](../../../../../lib/asmresolver/README.md), and [`lib/dnlib`](../../../../../lib/dnlib/README.md), selected comparison notes under [`docs/lib/`](../../../../../docs/lib/), official NuGet package metadata, and direct Carbide runtime probes executed locally in this checkout.

## 1. Request

> Evaluate feasibility of running any major library for PE parsing/re-writing (Mono.Cecil, AsmResolver, etc.) on `src/Carbide` (with a reasonable extension of Carbide, and/or forking and adaptation of the library, if necessary). Create a detailed report.

## 2. Executive summary

The short answer is: **yes, major managed PE parsing and rewriting libraries are feasible on Carbide today**, but the answer depends on which of these three questions is being asked:

1. Can Carbide host a prebuilt managed PE library at runtime?
2. Can Carbide ingest that library through its current `PackageReference` -> `@carbide/nuget` path?
3. Can Carbide build the library's own source tree without forking/adapting it?

Those answers are different.

### 2.1 Bottom-line verdicts

- **Mono.Cecil 0.11.6:** feasible today through Carbide's current package path and runtime host.
- **dnlib 4.5.0:** feasible today through Carbide's current package path and runtime host for the managed-only in-memory path.
- **AsmResolver:** feasible on Carbide, but very version-sensitive at the current NuGet/package-policy layer.
  - `5.5.1` stable: **runtime path is feasible**, but the current Carbide package resolver rejects the package closure because `System.Text.Json 6.0.8` carries analyzer payloads.
  - `6.0.0-beta.5`: **works today** through Carbide's package path.
  - `6.0.0-rc.1`: **currently blocked** by Carbide's package safety rules because of `IsExternalInit 1.0.3` shipping `build/*.props`.
- **System.Reflection.Metadata / PEReader / ManagedPEBuilder:** always a viable lower-level fallback, but this is not a drop-in replacement for Cecil/AsmResolver/dnlib. It trades dependency friction for a much larger Carbide-owned implementation burden.

### 2.2 Recommendation in one paragraph

If the goal is "get a serious PE rewrite library running inside Carbide with minimal friction," use **Mono.Cecil** first. If the goal is "get the richest long-term PE + metadata surface," **AsmResolver** is also viable, but do not treat "AsmResolver" as a single version-agnostic answer: a pinned `6.0.0-beta.5`-style line or direct DLL references work materially better on Carbide today than `5.5.1` stable or `6.0.0-rc.1`. **dnlib** is also viable, but it carries more optional runtime, Reflection.Emit, and Windows-PDB baggage than Cecil, so it should be treated as a managed-subset integration, not as a frictionless universal answer.

### 2.3 Verdict matrix

| Library / line | Direct DLL refs on Carbide | `PackageReference` on Carbide today | Library source tree under Carbide today | Feasible with reasonable extension or fork? | Short verdict |
|---|---|---|---|---|---|
| `Mono.Cecil 0.11.6` | Yes | Yes | Not as-is | Yes | Best low-friction choice. |
| `AsmResolver.DotNet 5.5.1` | Yes | No | Not as-is | Yes | Core runtime path is fine; current package closure collides with Carbide safety policy. |
| `AsmResolver.DotNet 6.0.0-beta.5` | Yes | Yes | Not as-is | Yes | Best current AsmResolver line for Carbide, based on direct probe. |
| `AsmResolver.DotNet 6.0.0-rc.1` | Likely yes | No | Not as-is | Yes | Current package closure collides with Carbide safety policy for a different reason. |
| `dnlib 4.5.0` | Yes | Yes | No | Yes | Works for managed-only in-memory load/write; treat optional native/Windows lanes as out of scope. |
| `System.Reflection.Metadata` + `ManagedPEBuilder` | N/A (in-box substrate) | N/A | N/A | Yes | Lowest dependency risk, highest implementation cost. |

## 3. Why Carbide can host these libraries at all

The important Carbide facts are friendlier to managed PE libraries than they first look:

- [`Carbide.Core.csproj`](../../../packages/core/src/Carbide.Core.csproj) is a `Microsoft.NET.Sdk.WebAssembly` project targeting `browser-wasm`, with `AllowUnsafeBlocks=true`.
- [`Carbide-Current-State-Guide.md`](../../Carbide-Current-State-Guide.md) explicitly documents a **single-threaded Mono-WASM runtime**.
- [`ReferenceRegistry`](../../../packages/core/src/Services/ReferenceRegistry.cs) already accepts raw assembly bytes and validates them with `PEReader.HasMetadata`.
- [`ProjectCompiler`](../../../packages/core/src/Services/ProjectCompiler.cs) already emits PE/PDB bytes in memory and executes via `Assembly.Load(byte[])`.
- [`CompilationInterop`](../../../packages/core/src/CompilationInterop.cs) exposes exactly the shape a managed library integration wants: create project, add references, compile, run.

In other words, Carbide already has the most important primitive for these libraries: **managed in-memory byte ingestion**.

For the core scenario in this report, the hard part is not "can Mono-WASM host a PE reader?" It can. The hard parts are:

- package ingestion policy (`@carbide/nuget`),
- project ingestion fidelity (`@carbide/msbuild-lite`),
- and avoiding optional library code paths that assume real files, native helpers, Windows-only symbol stacks, or dynamic codegen.

## 4. Carbide constraints that actually matter

### 4.1 Runtime constraints

Upstream WebAssembly runtime constraints matter, but mostly at the edges:

- [`lib/dotnet/runtime/src/mono/wasm/features.md`](../../../../../lib/dotnet/runtime/src/mono/wasm/features.md) documents optional multithreading, main-thread restrictions, and the broader Mono-WASM hosting model.
- `System.Console` on browser-hosted .NET throws `PlatformNotSupportedException` for multiple interactive console APIs in the browser implementation; this matters for host-heavy libraries, not for pure PE readers.

For PE libraries, the runtime constraints only become first-order problems when a library assumes:

- blocking background threads,
- memory-mapped files or OS handles as the default path,
- interactive console features,
- unmanaged symbol helpers,
- or runtime IL generation.

### 4.2 Carbide package-safety constraints

Carbide's package path is deliberately stricter than ordinary `dotnet restore`:

- [`@carbide/nuget` README](../../../packages/nuget/README.md) rejects packages carrying:
  - `runtimes/<rid>/native/` (`MSNUGET015`),
  - `build/*.targets`, `build/*.props`, `buildTransitive/*.targets`, `buildTransitive/*.props` (`MSNUGET016`),
  - `analyzers/` (`MSNUGET017`).
- [`safety.ts`](../../../packages/nuget/src/safety.ts) enforces these refusals at resolve time, before any bytes are handed to the reference registry.

This design is the main reason the answer for AsmResolver differs by version. The library itself is not the blocker; the package closure often is.

### 4.3 Carbide project-ingestion constraints

`@carbide/msbuild-lite` is intentionally a bounded evaluator:

- it supports `<TargetFramework>` / `<TargetFrameworks>` with first-listed selection,
- `Compile Include/Remove` globs,
- imports and `Directory.Build.props`,
- and `<EnableDefaultCompileItems>`,
- but not arbitrary MSBuild execution.

The key subtlety for compatibility work is that the supported property is currently [`EnableDefaultCompileItems`](../../../packages/msbuild-lite/README.md), not the broader SDK property `EnableDefaultItems`. That matters for `lib/cecil`.

## 5. Evaluation criteria

I evaluated each library on five axes:

1. **Managed-only runtime fit**
   - Can the core read/modify/write path stay inside managed code and in-memory streams?
2. **Byte[] / `Stream` first-class support**
   - Can the happy path avoid filesystem requirements?
3. **Package closure compatibility**
   - Does the published NuGet package graph survive Carbide's current safety rules?
4. **Source-tree compatibility**
   - Could Carbide plausibly build the library's own source tree without broad new project-system features?
5. **Adaptation cost**
   - If the answer is "not directly," is the smallest reasonable fix a small Carbide extension, a small package fork, or a larger architecture effort?

## 6. Empirical probes I ran

I ran focused end-to-end Carbide probes using the built CLI at [`packages/cli/dist/bin/carbide.js`](../../../packages/cli/dist/bin/carbide.js). Each successful probe used the same pattern:

- embed `MyHelper.dll` from [`packages/core/test/fixtures/helper-dll`](../../../packages/core/test/fixtures/helper-dll/MyHelper.dll) as base64,
- load it from a `byte[]` / `MemoryStream`,
- parse it with the candidate library,
- write the image back to a `MemoryStream`,
- print a compact success marker.

### 6.1 Probe results

| Probe | Outcome | What it proves |
|---|---|---|
| `Mono.Cecil 0.11.6` via `PackageReference` | Success: `OK|MyHelper|6|4608` | Carbide's current package resolver and runtime host already support Cecil's core in-memory read/write path. |
| `dnlib 4.5.0` via `PackageReference` with `TryToLoadPdbFromDisk=false` | Success: `OK|MyHelper|6|4608` | Carbide can already host dnlib's core managed module read/write path. |
| `AsmResolver.DotNet 5.5.1` via `PackageReference` | Failure before user code | Current Carbide package safety rejects the `System.Text.Json 6.0.8` analyzer payload in the package closure. |
| `AsmResolver.DotNet 6.0.0-beta.5` via `PackageReference` | Success: `OK|MyHelper|6|4608` | A newer AsmResolver line already works today through Carbide's package path. |
| `AsmResolver.DotNet 6.0.0-rc.1` via `PackageReference` | Failure before user code | Current Carbide package safety rejects `IsExternalInit 1.0.3` because it carries `build/IsExternalInit.props`. |
| Source-built `AsmResolver` `net8.0` DLLs attached with `--ref` | Success: `OK|MyHelper|6|4608` | AsmResolver's core object model and writer work on Carbide even when the package line does not. |
| `carbide validate --project lib/cecil/Mono.Cecil.csproj` | Failure | The current Carbide project evaluator does not honor `EnableDefaultItems=false`, so the project pulls test/resource sources it was not meant to compile in that mode. |
| `carbide validate --project lib/dnlib/src/dnlib.csproj --allow-list-mode off` | Failure | dnlib's source tree is currently blocked by package-safety refusal on `Microsoft.SourceLink.GitHub.props`. |

## 7. Mono.Cecil assessment

### 7.1 Why Cecil fits Carbide well

Cecil is the cleanest immediate fit:

- [`Mono.Cecil.csproj`](../../../../../lib/cecil/Mono.Cecil.csproj) is structurally simple.
- The official `0.11.6` package has **no NuGet dependencies** for either `.NETFramework 4.0` or `.NETStandard 2.0`:
  - [NuGet version index](https://api.nuget.org/v3-flatcontainer/mono.cecil/index.json)
  - [NuGet nuspec for `0.11.6`](https://api.nuget.org/v3-flatcontainer/mono.cecil/0.11.6/mono.cecil.nuspec)
- Cecil's core API is stream-oriented:
  - [`AssemblyDefinition.ReadAssembly(Stream)`](../../../../../lib/cecil/Mono.Cecil/AssemblyDefinition.cs)
  - [`ModuleDefinition.ReadModule(Stream)`](../../../../../lib/cecil/Mono.Cecil/ModuleDefinition.cs)
  - `Write(Stream, WriterParameters)`

Architecturally, that is exactly what Carbide wants: raw bytes in, managed object graph in memory, raw bytes out.

### 7.2 What worked

The direct Carbide probe using `PackageReference Include="Mono.Cecil" Version="0.11.6"` succeeded without special handling. No project-system workaround, package fork, or direct-reference bypass was required.

### 7.3 Real caveats

Cecil still has optional code paths that are less Carbide-friendly:

- file-path-based overloads and resolver probing,
- symbol-provider integration,
- strong-name signing helpers in `Mono.Security.Cryptography`,
- and some Windows-metadata/projection support.

Those are real caveats, but they are not on the critical path for:

- parse existing managed PE bytes,
- inspect metadata,
- modify IL/metadata,
- write the image back to a stream.

### 7.4 Source-tree feasibility

The current source tree is **close**, but not Carbide-buildable as-is:

- [`lib/cecil/Directory.Build.props`](../../../../../lib/cecil/Directory.Build.props) uses `EnableDefaultItems=false`.
- Carbide currently understands `EnableDefaultCompileItems`, not `EnableDefaultItems`.
- As a result, `carbide validate --project lib/cecil/Mono.Cecil.csproj` pulled in the `Test/`, `rocks/`, and `symbols/` trees and produced collisions unrelated to Cecil's core library code.
- A normal SDK `dotnet build` of `Mono.Cecil.csproj` got much further and then failed on a missing signing key file (`cecil.snk`), which confirms the current project shape is not wildly incompatible; it is just not drop-in ready for Carbide.

### 7.5 Cecil verdict

**Cecil is the best immediate choice for Carbide.**

It already works through Carbide's package path, its core API matches Carbide's runtime shape, and the remaining caveats are optional feature lanes rather than fundamental blockers.

## 8. AsmResolver assessment

### 8.1 Why AsmResolver is attractive

AsmResolver is the strongest long-term candidate if the goal is not just ".NET assembly editing" but broader PE + metadata control:

- [`AsmResolver.DotNet`](../../../../../lib/asmresolver/src/AsmResolver.DotNet/AsmResolver.DotNet.csproj) sits on top of lower PE layers rather than hiding them.
- The library exposes direct byte[] / stream entry points:
  - [`AssemblyDefinition.FromBytes`](../../../../../lib/asmresolver/src/AsmResolver.DotNet/AssemblyDefinition.cs)
  - [`ModuleDefinition.FromBytes`](../../../../../lib/asmresolver/src/AsmResolver.DotNet/ModuleDefinition.cs)
  - [`PEImage.FromBytes`](../../../../../lib/asmresolver/src/AsmResolver.PE/PEImage.cs)
  - [`PEFile.FromBytes`](../../../../../lib/asmresolver/src/AsmResolver.PE.File/PEFile.cs)
- It also supports direct `Write(Stream)` on the module path.

That is an excellent fit for Carbide's runtime model.

### 8.2 What the successful probes prove

Two separate probes succeeded:

1. `AsmResolver.DotNet 6.0.0-beta.5` via **ordinary Carbide `PackageReference` resolution**.
2. A **direct `--ref` closure** built from the local AsmResolver source snapshot (`net8.0` outputs).

Together these prove something important:

- AsmResolver's **runtime path is not the blocker** on Carbide.
- The blockers are instead:
  - package-line-specific dependency choices,
  - package-safety policy,
  - and source-tree build assumptions.

### 8.3 Why `5.5.1` stable fails today

The official `5.5.1` package metadata says:

- [NuGet version index](https://api.nuget.org/v3-flatcontainer/asmresolver.dotnet/index.json)
- [NuGet nuspec for `5.5.1`](https://api.nuget.org/v3-flatcontainer/asmresolver.dotnet/5.5.1/asmresolver.dotnet.nuspec)

Its dependency closure includes:

- `AsmResolver.PE 5.5.1`
- `System.Text.Json 6.0.8`

Carbide's package resolver rejected that closure before compile/run, because `System.Text.Json 6.0.8` carries analyzer payloads and [`safety.ts`](../../../packages/nuget/src/safety.ts) rejects any package containing `analyzers/`.

This is a **Carbide package-ingestion problem**, not a "AsmResolver cannot run on Mono-WASM" problem.

### 8.4 Why `6.0.0-beta.5` succeeds today

The official `6.0.0-beta.5` package line is materially better aligned with Carbide. The local nuspec in the package cache showed that `AsmResolver.DotNet 6.0.0-beta.5` depends only on `AsmResolver.PE 6.0.0-beta.5` and does **not** pull `System.Text.Json` in the published package metadata.

That version resolved and ran successfully through Carbide's package path.

This makes `6.0.0-beta.5` the most interesting "works today" AsmResolver line from a Carbide point of view.

### 8.5 Why `6.0.0-rc.1` regresses again

The official `6.0.0-rc.1` nuspec says:

- [NuGet nuspec for `6.0.0-rc.1`](https://api.nuget.org/v3-flatcontainer/asmresolver.dotnet/6.0.0-rc.1/asmresolver.dotnet.nuspec)

That line depends on:

- `AsmResolver.PE 6.0.0-rc.1`
- `IsExternalInit 1.0.3`

`IsExternalInit 1.0.3` is a source-only helper package whose mechanism is a `build/IsExternalInit.props` file plus content files:

- [NuGet nuspec for `IsExternalInit 1.0.3`](https://api.nuget.org/v3-flatcontainer/isexternalinit/1.0.3/isexternalinit.nuspec)

Carbide currently refuses such packages at resolve time because it refuses `build/*.props` and does not ingest `contentFiles` as source.

So `6.0.0-rc.1` is not "AsmResolver fails on Carbide"; it is "this current package line uses a package-distribution technique that Carbide does not currently model."

### 8.6 Source-tree feasibility

AsmResolver's source tree is not Carbide-buildable as-is today:

- [`src/Directory.Build.props`](../../../../../lib/asmresolver/src/Directory.Build.props) multi-targets a wide framework matrix.
- [`AsmResolver.DotNet.csproj`](../../../../../lib/asmresolver/src/AsmResolver.DotNet/AsmResolver.DotNet.csproj) references the source-generator project as an analyzer.
- A normal SDK `dotnet build` of `AsmResolver.DotNet.csproj` succeeded for `net8.0`.
- The same build failed for `netstandard2.0` in the local snapshot because [`ModuleDefinition.cs`](../../../../../lib/asmresolver/src/AsmResolver.DotNet/ModuleDefinition.cs) now calls `ArgumentNullException.ThrowIfNull`, which is not available on that older target the way the current source is written.

That combination implies:

- **prebuilt AsmResolver binaries are feasible on Carbide right now;**
- **self-host building the current upstream source tree on Carbide is not yet feasible without project/fork work.**

### 8.7 AsmResolver verdict

**AsmResolver is feasible on Carbide and is probably the best long-term technical fit if PE control depth matters.** The catch is that the package story is currently **version- and packaging-line-sensitive**, so a production Carbide integration would need one of:

- a pinned known-good published version,
- direct DLL reference bundling,
- a small Carbide package-ingestion improvement,
- or a small library/package fork.

## 9. dnlib assessment

### 9.1 Why dnlib is still a real candidate

dnlib's core module APIs are also well aligned with Carbide's runtime model:

- [`ModuleDefMD.Load(byte[])`](../../../../../lib/dnlib/src/DotNet/ModuleDefMD.cs)
- [`ModuleDefMD.Load(Stream)`](../../../../../lib/dnlib/src/DotNet/ModuleDefMD.cs)
- `Write(Stream)`

The official package metadata is also modest:

- [NuGet version index](https://api.nuget.org/v3-flatcontainer/dnlib/index.json)
- [NuGet nuspec for `4.5.0`](https://api.nuget.org/v3-flatcontainer/dnlib/4.5.0/dnlib.nuspec)

For `.NETStandard 2.0`, it depends on:

- `System.Reflection.Emit 4.7.0`
- `System.Reflection.Emit.Lightweight 4.7.0`

That package closure passed Carbide's current package-safety rules.

### 9.2 What worked

The direct Carbide probe using `PackageReference Include="dnlib" Version="4.5.0"` succeeded when I explicitly disabled disk-based PDB probing:

- `new ModuleCreationOptions { TryToLoadPdbFromDisk = false }`

That shows the core managed read/write path works under Carbide today.

### 9.3 Real caveats

dnlib carries more optional baggage than Cecil:

- actual usage of `System.Reflection.Emit` / dynamic method machinery,
- Windows PDB paths,
- COM-heavy symbol reader/writer support,
- stronger platform-sensitive writing paths,
- memory-mapped and native-adjacent readers/writers.

Those are all real risks on Carbide if someone expects "the full dnlib feature surface" to just work.

The right reading of the successful probe is:

- **dnlib's core managed module load/write path is viable on Carbide;**
- **dnlib's entire feature matrix is not automatically Carbide-safe.**

### 9.4 Source-tree feasibility

dnlib's source tree is substantially less Carbide-friendly than Cecil's:

- [`dnlib.csproj`](../../../../../lib/dnlib/src/dnlib.csproj) imports external props conditionally,
- carries package references such as `Microsoft.SourceLink.GitHub`,
- includes strong-name settings,
- and conditionally brings in `System.Reflection.Emit` packages for `netstandard2.0`.

The direct Carbide project probe failed immediately on `Microsoft.SourceLink.GitHub.props`, which Carbide's current package-safety rules reject.

So dnlib should be treated as:

- **runtime-feasible as a prebuilt library,**
- **not self-host-feasible as an unmodified source tree under current Carbide.**

### 9.5 dnlib verdict

**dnlib is viable on Carbide for the managed-only subset, but it is not the cleanest first integration.**

Use it when dnlib-specific behavior matters. Otherwise Cecil is simpler and AsmResolver is richer.

## 10. Lower-level fallback: `System.Reflection.Metadata`

If the goal is "avoid package-line churn entirely," the built-in runtime metadata stack is always available as a fallback substrate:

- [`system-reflection-metadata-ecma335-emit-architecture.md`](../../../../../docs/lib/dotnet/runtime/system-reflection-metadata-ecma335-emit-architecture.md)
- [`managed-metadata-toolkits-architecture.md`](../../../../../docs/lib/managed-metadata-toolkits-architecture.md)

This path has two strong advantages:

- no third-party package-distribution surprises,
- and a direct mapping to ECMA-335 table/heap semantics.

But it also has the biggest downside:

- Carbide would have to own far more of the rewrite/edit pipeline itself.

So SRM is the right fallback if the project values dependency minimization over library ergonomics. It is **not** the lowest-effort path to "have a real rewrite library running."

## 11. What "reasonable extension of Carbide" would unlock

### 11.1 Asset-aware NuGet safety pruning

Current Carbide safety checks are package-content-based, not selected-asset-based.

That is stricter than necessary for some cases:

- `AsmResolver.DotNet 5.5.1` declared `exclude="Build,Analyzers"` on the problematic dependency, yet Carbide still rejected the package because the raw nupkg contains analyzer assets.

A reasonable Carbide improvement would be:

- select the effective asset classes for the chosen TFM and dependency edge,
- then apply safety rules only to the asset classes Carbide would actually consume.

This looks like a **small-to-medium Carbide change**, roughly on the order of **~150-350 LOC across ~3-5 files** in `packages/nuget`, plus tests.

That change would likely unlock `AsmResolver 5.5.1` immediately.

### 11.2 Better support for SDK default-item suppression

Carbide currently supports `EnableDefaultCompileItems`, but `lib/cecil` uses `EnableDefaultItems=false`.

A small `@carbide/msbuild-lite` extension that treats `EnableDefaultItems=false` as also disabling default `Compile` discovery would materially improve compatibility with existing SDK-style libraries.

This looks like a **small change**, roughly **~20-60 LOC across ~1-2 files** plus tests.

### 11.3 First-class multi-DLL reference bundles

Carbide already supports raw `--ref` DLL attachments, and that was enough to prove AsmResolver's core runtime path. A more ergonomic variant would be:

- a manifest or directory-based "reference bundle" that lets a user point Carbide at a prebuilt managed closure.

This would not be required for feasibility, but it would make package-policy workarounds much less awkward.

### 11.4 Source-only helper package support

`IsExternalInit 1.0.3` demonstrates a trickier class of package:

- no runtime DLLs,
- but source delivered through `build/*.props` + `contentFiles`.

Supporting this safely would require more than "ignore props." It would require Carbide to model at least a narrow, explicit subset of source-only package ingestion.

That is possible, but it is a **larger project-system extension** than the analyzer-pruning idea above and is not the first thing I would build just to get PE tooling working.

## 12. What "reasonable library fork or adaptation" would unlock

### 12.1 Mono.Cecil

A Carbide-oriented Cecil fork would be lightweight:

- set `EnableDefaultCompileItems=false` explicitly in a way Carbide already understands, or patch Carbide to understand `EnableDefaultItems`,
- disable or replace strong-name signing in the local build,
- optionally split the core library from tests / rocks / symbol helpers more cleanly for Carbide self-hosting.

This is a **small fork surface**, mostly project-file and packaging cleanup rather than deep code changes.

### 12.2 AsmResolver

For AsmResolver, the most reasonable adaptation is package-line control, not deep runtime surgery:

- pin a Carbide-friendly published line such as `6.0.0-beta.5`, or
- publish a Carbide-specific package variant with a cleaner dependency closure, or
- keep using direct DLL references for the PE tooling layer.

If self-host building under Carbide becomes a goal, a fork would also need:

- either a `net8.0` / `net10.0`-centered target story,
- or a fix for the current `netstandard2.0` drift,
- and a plan for the analyzer/source-generator project reference.

That is a **moderate fork surface**, but still much smaller than writing a new PE toolkit.

### 12.3 dnlib

dnlib can also be forked, but the payoff is weaker:

- the runtime subset already works as a package,
- while the source-tree compatibility problems are broader,
- and the library carries more optional Windows/native/symbol baggage than Cecil.

So dnlib is forkable, but it is not the library I would choose for a Carbide-first fork unless dnlib-specific behavior is the actual requirement.

## 13. Recommended strategy

### 13.1 If the goal is "ship a PE library on Carbide soon"

Do this:

1. Start with **Mono.Cecil 0.11.6**.
2. Keep the first Carbide integration explicitly on the in-memory path:
   - `byte[]` / `Stream` input only,
   - `Stream` output only,
   - no symbol providers,
   - no strong-name re-signing,
   - no file-based default resolvers.

This is the lowest-risk path.

### 13.2 If the goal is "use the strongest long-term PE toolkit"

Do this:

1. Use **AsmResolver**, but pin the line deliberately.
2. Treat the current package result as:
   - `5.5.1`: blocked by current package safety,
   - `6.0.0-beta.5`: works,
   - `6.0.0-rc.1`: blocked by current package safety again.
3. Decide early whether you want:
   - a pinned published version,
   - direct prebuilt DLL references,
   - or a Carbide-aware package fork.

AsmResolver is the best technical ceiling; it just has a noisier distribution story.

### 13.3 If the goal is "minimize dependency volatility"

Use **System.Reflection.Metadata** and build the required rewrite surface in Carbide-owned code, but only if the project explicitly prefers that trade-off. It is the safest dependency story and the largest implementation story.

## 14. Final answer

Major managed PE parsing and rewriting libraries are feasible on Carbide.

The most important conclusion is that **Mono-WASM itself is not the main blocker**. The main blockers are:

- Carbide's intentionally strict package-safety rules,
- bounded project-system support,
- and optional library lanes that assume files, native helpers, or full desktop/runtime behavior.

If I had to rank practical choices for Carbide right now:

1. **Mono.Cecil** — best immediate integration target.
2. **AsmResolver** — best long-term technical target, but version/package choice must be deliberate.
3. **dnlib** — viable managed-only subset, but not the cleanest first choice.
4. **SRM** — safest dependency story, highest implementation burden.

That is a strong "yes" on feasibility, with the main nuance being that **package and source-tree feasibility are not the same thing as runtime feasibility**.
