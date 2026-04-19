# Feasibility: running a useful `lib/pwsh` subset on Carbide in Node.js

- Created (UTC): 2026-04-19T20:23:22Z
- Repository HEAD: b27148c824028d42a26852cdff6db1bd0cebfcb3

Status: feasibility / architecture report. Evaluates whether a deliberately reduced fork of `lib/pwsh` can be made Carbide-compatible for Node-hosted execution, and separately whether that fork can itself become Carbide-buildable.

Audience: repository owner and future implementers.

Scope: local evidence from `src/Carbide/`, `lib/pwsh/`, and `docs/lib/pwsh/`, plus upstream .NET WebAssembly runtime constraints only where they materially change the local conclusion.

## 1. Request

> Evaluate feasibility of forking a limited (but useful) subset of `lib/pwsh` and making necessary modifications to be able to compile and run it using Carbide in Node.js (perhaps, after implementing some additional features in Carbide). The goal is to be able to run some useful subset of PowerShell scripts on Node.js. Create a report with your finding and conclusions.

## 2. Executive summary

The short answer is:

- **Running a deliberately reduced PowerShell-derived engine inside Carbide's Node host is feasible.**
- **Building the current `lib/pwsh` source tree with current Carbide is not feasible.**
- **Making a new, intentionally smaller fork Carbide-buildable later is plausible, but only if it stops looking like today's `pwsh` project graph and starts looking like a new Carbide-oriented project.**

The most credible target is **not** "PowerShell on Carbide" in the full `pwsh` sense. It is a **persistent, synchronous, automation-oriented subset**:

- parser + AST + script execution,
- one long-lived runspace-like session,
- `UseCurrentThread`-style execution only,
- a small custom initial session state,
- a curated cmdlet surface,
- `Variable`, `Function`, `Alias`, and `Environment` providers first,
- `FileSystem` provider only after a host/filesystem spike proves it is worth carrying.

The things that make full `pwsh` implausible for a first Carbide target are exactly the things one would expect:

- build-time dependence on MSBuild targets/tasks/analyzers/source generators,
- a broad package graph with native/platform-heavy edges,
- runtime dependence on threads and async pipeline machinery,
- native-command/process integration,
- large feature families that assume Windows, remoting, CIM, eventing, or a rich interactive host,
- several engine fast paths that assume runtime code generation.

### Verdict matrix

| Question | Verdict | Why |
|---|---|---|
| Can current Carbide build unmodified `lib/pwsh/src/System.Management.Automation`? | **No** | The project graph depends on `.targets`, analyzer/source-generator wiring, and a package/dependency surface Carbide explicitly refuses today. |
| Can Carbide host a prebuilt PowerShell-subset engine DLL in Node.js? | **Yes** | Carbide can already run managed code in Node-hosted Mono-WASM; the missing pieces are a persistent host surface and a Carbide-compatible subset engine. |
| Can a new reduced fork eventually become Carbide-buildable from source? | **Yes, with substantial surgery** | Only if it uses a new project file, new dependency policy, no shared pwsh build props/targets, and a much smaller source allow-list. |
| Is near-`pwsh` compatibility a realistic v1? | **No** | Threading, native commands, remoting, rich host semantics, and provider/module breadth are too cross-cutting. |

## 3. Two different feasibility questions

This request actually contains **two different engineering questions**:

1. **Runtime-hosting feasibility**: can Carbide's Node-hosted Mono-WASM runtime *run* a PowerShell-derived managed engine?
2. **Source-build feasibility**: can Carbide itself *build* the relevant forked sources?

Those answers are different:

- **Runtime-hosting is the easier half.** A prebuilt managed engine assembly can, in principle, be shipped beside Carbide and hosted inside the same runtime.
- **Source-build is the harder half.** Current `pwsh` project files are structurally incompatible with Carbide's intentionally bounded MSBuild and NuGet model.

That distinction matters because the best first milestone is:

- build a **Carbide-compatible subset engine with normal `dotnet` first**,
- prove it runs inside Carbide in Node,
- only then decide whether self-hosting that fork's source build inside Carbide is worth the extra work.

## 4. Why current `pwsh` does not fit current Carbide

### 4.1 Build-time blockers are immediate and explicit

The shared PowerShell build props already conflict with Carbide's build model:

- [`lib/pwsh/PowerShell.Common.props`](../../../../../lib/pwsh/PowerShell.Common.props) imports `Analyzers.props`.
- The same file defines a `GetPSCoreVersionFromGit` target and runs `git describe` via `<Exec ...>`.

That alone is enough to fail under Carbide's current documented rules:

- [`src/Carbide/packages/msbuild-lite/README.md`](../../../packages/msbuild-lite/README.md) explicitly refuses `<Target>` (`MSBLITE020`), `<Task>` (`MSBLITE021`), and `<UsingTask>` (`MSBLITE022`).
- [`src/Carbide/docs/Carbide-Current-State-Guide.md`](../../Carbide-Current-State-Guide.md) likewise documents no broad target/task execution and no general MSBuild parity.

`System.Management.Automation` itself also carries Carbide-incompatible build assumptions:

- [`lib/pwsh/src/System.Management.Automation/System.Management.Automation.csproj`](../../../../../lib/pwsh/src/System.Management.Automation/System.Management.Automation.csproj) imports `..\..\PowerShell.Common.props`.
- The same project references `SourceGenerators\PSVersionInfoGenerator\PSVersionInfoGenerator.csproj` as an analyzer (`OutputItemType="Analyzer"`).

Carbide's package policy collides with that project graph in multiple ways:

- [`src/Carbide/packages/nuget/README.md`](../../../packages/nuget/README.md) refuses native binaries (`MSNUGET015`), build `.props`/`.targets` (`MSNUGET016`), analyzers (`MSNUGET017`), and source generators (`MSNUGET018`).
- [`src/Carbide/packages/nuget/src/safety.ts`](../../../packages/nuget/src/safety.ts) says the quiet part out loud: Carbide's Mono-WASM runtime cannot load native binaries, and unsafe package contents are rejected before they reach the reference registry.
- [`src/Carbide/packages/nuget/src/allowlist.ts`](../../../packages/nuget/src/allowlist.ts) contains only a small curated allow-list. It includes `Newtonsoft.Json`, but not the broader set of packages pulled by `System.Management.Automation`.

`System.Management.Automation.csproj` also proves the baseline dependency surface is broad and platform-attached. Its package references include, among others:

- `Microsoft.ApplicationInsights`
- `Microsoft.Win32.Registry.AccessControl`
- `System.Diagnostics.EventLog`
- `System.DirectoryServices`
- `System.Management`
- `System.Security.Cryptography.Pkcs`
- `Microsoft.Management.Infrastructure`
- `Microsoft.PowerShell.Native`
- `Microsoft.Security.Extensions`
- `System.Windows.Extensions`

Some of those are merely "large and awkward"; some are much more fundamental. The important point is this: **the current pwsh engine project is not a small managed-only library that Carbide merely needs to point at.**

There is also a target-framework mismatch:

- [`lib/pwsh/PowerShell.Common.props`](../../../../../lib/pwsh/PowerShell.Common.props) targets `net9.0`.
- Carbide's supported compile-time story is centered on `net10.0` and [`@carbide/refs-net10.0`](../../../packages/refs-net10.0/README.md), as documented in the [current-state guide](../../Carbide-Current-State-Guide.md).

### 4.2 Carbide's runtime model conflicts with key `pwsh` assumptions

Carbide today is a `browser-wasm` runtime packaged for both browser and Node hosts:

- [`src/Carbide/packages/core/src/Carbide.Core.csproj`](../../../packages/core/src/Carbide.Core.csproj) uses `Microsoft.NET.Sdk.WebAssembly` with `RuntimeIdentifier=browser-wasm`.
- [`src/Carbide/docs/Carbide-Current-State-Guide.md`](../../Carbide-Current-State-Guide.md) states plainly that **Mono-WASM is single-threaded**.
- Upstream [`dotnet/runtime` WebAssembly features](https://github.com/dotnet/runtime/blob/main/src/mono/wasm/features.md) says multithreading is disabled by default and that `[JSExport]` / `[JSImport]` remain main-thread-limited even when multithreading is enabled.

PowerShell's engine and host layers assume a richer threading model:

- [`lib/pwsh/src/System.Management.Automation/engine/hostifaces/Connection.cs`](../../../../../lib/pwsh/src/System.Management.Automation/engine/hostifaces/Connection.cs) defines `PSThreadOptions.Default`, `UseNewThread`, `ReuseThread`, and `UseCurrentThread`, with `UseCurrentThread` explicitly documented as invalid for asynchronous calls.
- [`lib/pwsh/src/System.Management.Automation/engine/hostifaces/LocalPipeline.cs`](../../../../../lib/pwsh/src/System.Management.Automation/engine/hostifaces/LocalPipeline.cs) creates new `Thread` instances for `UseNewThread`, maintains a reusable worker thread for `ReuseThread`, and uses `AutoResetEvent`.
- [`lib/pwsh/src/System.Management.Automation/engine/hostifaces/PowerShell.cs`](../../../../../lib/pwsh/src/System.Management.Automation/engine/hostifaces/PowerShell.cs) exposes a large `BeginInvoke` / `InvokeAsync` surface and validates thread settings against those modes.

This does **not** mean "nothing PowerShell-like can run." It means the subset boundary must be explicit:

- **synchronous invocation only,**
- **one authoritative execution lane,**
- **no async pipeline APIs in v1,**
- **no worker-thread-based runspace policy.**

### 4.3 Host shape is another mismatch

Carbide's public control plane today is oriented around **compile, run entry point, collect text output**:

- [`src/Carbide/packages/core/src/CompilationInterop.cs`](../../../packages/core/src/CompilationInterop.cs) exports `CreateSession`, `CreateProject`, `BuildAsync`, and `RunAsync`.
- [`src/Carbide/packages/core/src/Services/ProjectCompiler.cs`](../../../packages/core/src/Services/ProjectCompiler.cs) compiles, `Assembly.Load(byte[])`, invokes the entry point, captures stdout/stderr via `Console.SetOut`/`Console.SetError`, and restores them afterward.

That is enough for "compile a program and run it to completion." It is **not** enough for a good PowerShell host, which wants:

- a persistent runspace/session,
- repeated command submission into that same session,
- structured results rather than only final console text,
- eventually completion, cancellation, host callbacks, and provider-aware state.

This is exactly why the existing [`JS↔C# interop bridge proposal`](../../proposals/carbide-js-interop-bridge-proposal__2026-04-18__22-00-00-000000__a2d2955163d1.md) is so relevant here. A PowerShell subset host wants a **data-plane bridge**, not just the existing control-plane "build and run a whole entry point" surface.

### 4.4 Some engine paths assume runtime code generation

Even after trimming away obvious platform-heavy features, there is a deeper technical risk: parts of `System.Management.Automation` assume dynamic code generation:

- [`engine/lang/scriptblock.cs`](../../../../../lib/pwsh/src/System.Management.Automation/engine/lang/scriptblock.cs) calls `Expression.Lambda(...).Compile()`.
- [`engine/ScriptCommandProcessor.cs`](../../../../../lib/pwsh/src/System.Management.Automation/engine/ScriptCommandProcessor.cs) calls `_scriptBlock.Compile(...)`.
- [`engine/CoreAdapter.cs`](../../../../../lib/pwsh/src/System.Management.Automation/engine/CoreAdapter.cs) uses `DynamicMethod` and multiple `Expression.Lambda(...).Compile()` paths.
- [`engine/EventManager.cs`](../../../../../lib/pwsh/src/System.Management.Automation/engine/EventManager.cs) creates a dynamic assembly with `AssemblyBuilder.DefineDynamicAssembly(...)`.

I do **not** treat those lines as proof that the subset is impossible. I do treat them as proof that a Carbide-friendly subset is **not** going to happen by removing a few top-level features and hoping the rest of the engine falls into line.

The runtime-compatible subset must either:

- route around those paths,
- disable them,
- or replace them with slower but simpler reflection/interpreter code.

## 5. What subset is actually realistic and useful

The right v1 is an **embedded automation engine**, not a clone of `pwsh.exe`.

### 5.1 Useful target scenarios

A subset is worth doing if it can handle scripts like:

- function libraries and reusable scriptblocks,
- pipeline-style object shaping over in-memory data,
- JSON/text/environment-driven automation,
- template expansion and lightweight task orchestration,
- controlled module-like extension through explicitly injected commands/providers.

It is **not** worth targeting, in v1, scripts whose value depends on:

- launching native processes,
- remote sessions/jobs,
- Registry/Certificate/WSMan providers,
- PSReadLine and console UX parity,
- advanced module discovery from arbitrary filesystem layouts,
- debugger integration,
- Windows policy/security features,
- CIM / DSC / EventLog / ETW-style subsystems.

### 5.2 Most credible v1 subset boundary

The most credible engine boundary is:

- **Parser, tokenizer, AST, and basic script evaluation**
  - These are clearly valuable on their own and already separated in [`docs/lib/pwsh/parser-and-execution-pipeline-architecture.md`](../../../../../docs/lib/pwsh/parser-and-execution-pipeline-architecture.md).
- **Interpreter-first or interpreter-heavy execution**
  - The parser/execution docs show both runtime and interpreter strata. A Carbide subset should bias toward the non-emit path, even if slower.
- **A custom minimal session state**
  - Do **not** call `InitialSessionState.CreateDefault2()` blindly. It imports the core snap-in and default providers including `FileSystem` and, on Windows, `Registry`.
  - Instead, build a smaller session state explicitly.
- **Synchronous invocation only**
  - Align with `PSThreadOptions.UseCurrentThread`; do not expose `BeginInvoke` / `InvokeAsync` in v1.
- **Providers**
  - Start with `Variable`, `Function`, `Alias`, and `Environment`.
  - Treat `FileSystem` as a phase-1.5 or phase-2 feature pending a filesystem spike.
- **Cmdlets**
  - Curate a small set of managed, provider-light commands.
  - Many user-visible cmdlets live outside `System.Management.Automation`, so assume some additional cherry-picking or reimplementation will be necessary.

### 5.3 `FileSystem` deserves caution, not blind inclusion

PowerShell's own provider docs reinforce that provider behavior is part of the semantic contract, not just a path parser:

- [`docs/lib/pwsh/provider-and-namespace-architecture.md`](../../../../../docs/lib/pwsh/provider-and-namespace-architecture.md) describes providers as capability-bearing virtual stores, with `FileSystemProvider` including content and ACL behavior.
- [`docs/lib/pwsh/interactive-host-integration-guide.md`](../../../../../docs/lib/pwsh/interactive-host-integration-guide.md) explicitly warns that hosts need a clear policy for file-system-only behavior versus full provider semantics.

So the right stance is:

- **do not make `FileSystem` a prerequisite for the first engine bring-up,**
- but **do spike it early**, because a Node-hosted file-oriented subset is much more useful if `Get-ChildItem`, `Get-Content`, `Set-Location`, and simple path semantics can be made trustworthy.

## 6. What would need to change

### 6.1 Carbide changes required for runtime-hosting

These are the changes I would treat as genuinely necessary to host a useful subset well:

1. **A persistent managed-object surface**
   - Best fit: implement the existing [`JS↔C# interop bridge proposal`](../../proposals/carbide-js-interop-bridge-proposal__2026-04-18__22-00-00-000000__a2d2955163d1.md), or at least a narrower host-object bridge for one long-lived PowerShell session object.
   - Reason: a PowerShell host wants repeated `invoke(script)` calls against one persistent session, not repeated entry-point programs.

2. **A stable packaging story for prebuilt managed engine DLLs**
   - Ship the subset engine as a managed-only companion package or bundle it with a Carbide-specific runtime package.
   - Reason: this avoids making "Carbide builds the fork from source" a day-one requirement.

3. **Optional: a host callback surface for filesystem and native-command emulation**
   - `FileSystem` may be able to use managed APIs directly; if not, host callbacks become important.
   - Native command execution, if ever added later, should almost certainly be delegated to Node rather than reusing PowerShell's existing `Process`-centric implementation.

4. **Optional but high-value: streaming / incremental output**
   - The initial subset can buffer results per invocation.
   - A better shell-ish experience later wants output/events as they happen.

### 6.2 Carbide changes required only if the fork itself must become Carbide-buildable

This is a separate workstream. It becomes tractable only if the fork is restructured to avoid needing full MSBuild parity.

The rule should be:

- **change the fork to fit Carbide,**
- **not Carbide to fit the current pwsh build graph.**

Concretely:

- do **not** make "general `<Target>` / `<Task>` execution" a prerequisite;
- do **not** make analyzer/source-generator execution a prerequisite;
- instead, make a new forked project that avoids those features entirely.

If that is done, Carbide likely needs only modest additional help:

- perhaps a few more managed-only allow-list entries,
- perhaps a dedicated companion ref/runtime package,
- perhaps some build ergonomics around multi-project packaging.

But the main work is in the fork, not in Carbide.

### 6.3 PowerShell-fork changes required

The fork itself is the heavy lift. I would expect all of the following:

1. **New project file**
   - New `csproj`, new versioning, no import of `PowerShell.Common.props`, no analyzer/source-generator dependency.

2. **Explicit source allow-list, not a giant block-list**
   - Start from the parser/runtime/interpreter/session-state/provider files you really want.
   - Pull in more only when tests prove they are needed.

3. **Custom initial session state**
   - Do not start from `CreateDefault2()` and then try to subtract.
   - Build the command/provider surface intentionally.

4. **Thread model collapse**
   - Force synchronous current-thread execution.
   - Remove or hard-fail async pipeline lanes.

5. **Disable or replace native-command integration**
   - [`CommandDiscovery.cs`](../../../../../lib/pwsh/src/System.Management.Automation/engine/CommandDiscovery.cs) routes `CommandTypes.Application` to `NativeCommandProcessor`.
   - For v1, that should become "unsupported" or a host callback, not "let the old implementation try."

6. **Trim feature families aggressively**
   - Remoting, jobs, CIM, DSC, eventing, Windows-only providers, policy/security subsystems, telemetry, and console-host-specific behavior should all be treated as out of scope until proven essential.

7. **Replace dynamic-code fast paths where necessary**
   - Prefer correctness and host compatibility over PowerShell's current optimization strategy.

8. **Define a small cmdlet strategy**
   - Either cherry-pick a handful of managed cmdlets from sibling PowerShell assemblies or add a tiny new cmdlet layer around the subset engine.

## 7. Recommended delivery strategy

### 7.1 The first milestone should not be "compile current pwsh with Carbide"

That is the wrong hill to die on. It maximizes uncertainty in every dimension at once:

- build system,
- runtime compatibility,
- packaging,
- provider semantics,
- cmdlet surface,
- host API.

The first milestone should be:

- a **normal-`dotnet`-built managed subset engine**, plus
- a **Carbide-hosted Node runner** that keeps one session alive and can execute multiple scripts against it.

That proves the part we actually care about: **can a PowerShell-derived scripting engine live inside Carbide's Node-hosted Mono-WASM runtime and do useful work?**

### 7.2 Suggested staged plan

#### Stage 0: cheap empirical spikes

Before doing any large forking, answer three questions with tiny probes:

1. Can the current Carbide runtime tolerate the specific dynamic-code patterns the subset still wants to use?
2. Does managed file I/O behave well enough under Carbide's Node host to justify carrying `FileSystemProvider` early?
3. What is the smallest useful cmdlet set that does not drag in half of the PowerShell tree?

#### Stage 1: build `pwsh-lite` outside Carbide

Create a new experimental project with:

- parser + AST,
- minimal execution,
- custom session state,
- sync invocation only,
- variable/function/alias/environment providers,
- a tiny command surface.

Success criterion:

- one persistent host object can run a sequence of scripts such as:
  - define function,
  - set variable,
  - pipe objects through a simple command,
  - inspect session state in the next submission.

#### Stage 2: host it inside Carbide

Package that engine as managed DLLs and load it into Carbide's runtime.

Success criterion:

- from Node.js, through Carbide, create one session and execute multiple scripts without reinitializing the engine each time.

#### Stage 3: improve usefulness

Only after stage 2 works:

- add `FileSystem` if the spike says yes,
- add a better cmdlet surface,
- add structured results and better errors,
- add completion or cancellation if still worth it.

#### Stage 4: decide on Carbide-buildability of the forked source

If the subset is already useful when shipped as prebuilt managed DLLs, then decide whether "Carbide can build the fork from source too" is strategically valuable or just intellectually pleasing.

My guess: it is a **nice-to-have**, not the key value.

## 8. Final conclusions

### 8.1 What is feasible

These things are feasible:

- a **PowerShell-inspired embedded scripting engine** on Carbide in Node.js,
- a **useful subset of PowerShell script behavior** centered on parsing, session state, object pipelines, and controlled automation,
- a **persistent session model** once Carbide gets a data-plane host bridge,
- a **Carbide-oriented fork** with a new project file and a much smaller source/dependency surface.

### 8.2 What is not a good plan

These things are not a good plan:

- trying to compile today's `lib/pwsh` project graph under current Carbide,
- assuming a small set of package allow-list tweaks will be enough,
- targeting native-command parity or interactive-shell parity in the first version,
- treating the existing `pwsh` host/runspace/threading model as something Carbide should imitate wholesale.

### 8.3 Recommendation

I would classify this as:

- **technically feasible,**
- **strategically worthwhile only if the goal is embedded automation rather than `pwsh` replacement,**
- **a medium-to-large subproject, not a spike,**
- **best approached as a new Carbide-compatible fork with a smaller semantic promise.**

If the real goal is:

- "run some useful PowerShell-flavored automation inside Node without shipping `pwsh.exe`",

then I think this is worth doing.

If the real goal is:

- "make `pwsh` itself effectively available on Carbide with broad script compatibility",

then I think the cost/risk curve turns bad quickly.

My recommendation is therefore:

- **Proceed only with a deliberately branded subset target**,
- **build and prove the runtime-hosting story first**,
- **treat source-build-on-Carbide as a later optimization, not the initial deliverable.**
