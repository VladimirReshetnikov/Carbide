# Proposal: `dotnet.exe` as a Carbide-backed VFS SDK facade

- Created (UTC): 2026-04-25T19:49:18Z
- Repository HEAD: 8ad5657c49d6aca974b7cc30a30e2f02b82940f2
- Status: Draft
- Audience: Vladimir; Carbide shell maintainers; future agents extending browser-side C# compilation
- Scope: Add a VFS-visible `dotnet.exe` executable stub that compiles and runs C# code by delegating to existing Carbide compiler/runtime infrastructure, including investigation of in-process execution for compiled executable assemblies.
- Related code:
  - `src/Carbide/packages/core/src/Services/ProjectCompiler.cs`
  - `src/Carbide/packages/core/src/CompilationInterop.cs`
  - `src/Carbide/packages/core/src/ts/project.ts`
  - `src/Carbide/packages/core/src/ts/session.ts`
  - `src/Carbide/packages/cli/src/commands/build.ts`
  - `src/Carbide/packages/cli/src/commands/run.ts`
  - `src/Carbide/packages/cli/src/project-file.ts`
  - `src/Carbide/packages/carbide-multishell/src/VirtualExecutableCatalog.cs`
  - `src/Carbide/packages/carbide-multishell/src/MultishellVirtualExecutableHandler.Core.cs`
  - `src/Carbide/packages/carbide-shell-core/src/Dispatch/ShellDispatcher.cs`
  - `src/Carbide/packages/carbide-shell-core/src/Dispatch/VirtualExecutable.cs`
  - `src/Carbide/packages/carbide-shell-core/src/Apps/StubInstaller.cs`
- Related docs:
  - [Carbide Current-State Guide](../Carbide-Current-State-Guide.md)
  - [Virtual executable stubs for common `System32` and Git `usr/bin` tools in `carbide-multishell`](carbide-multishell-vfs-executable-stubs-proposal__2026-04-22__23-10-39-000000__6827e976e1d5.md)
  - [Scope report: converging Carbide browser shells onto `carbide-pwsh` only](../reports/carbide-pwsh-single-endpoint-scope-report__2026-04-24__00-17-09__f4e7687b27b5.md)
  - [Carbide T2.1 resolution report](../reports/carbide-T21-resolution__2026-04-21__03-15-50-000000.md)
  - [Carbide T2.1 runMain experiment](../reports/carbide-T21-runmain-experiment__2026-04-21__00-36-24-000000.md)
- External references:
  - [Microsoft Learn: `dotnet` command](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet)
  - [Microsoft Learn: `dotnet build`](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-build)
  - [Microsoft Learn: `dotnet run`](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-run)
  - [Microsoft Learn: `dotnet exec`](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-exec)
  - [Microsoft Learn: `Main()` and command-line arguments](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/program-structure/main-command-line)
  - [Microsoft Learn: Assembly unloadability](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability)
  - [Microsoft Learn: Lazy load assemblies in ASP.NET Core Blazor WebAssembly](https://learn.microsoft.com/en-us/aspnet/core/blazor/webassembly-lazy-load-assemblies)

## Summary

Carbide should add a VFS-visible `dotnet.exe` facade, but it should not try to emulate a host operating-system process or a full .NET SDK installation. In the browser, there is no meaningful child-process boundary for `dotnet.exe`; the correct architecture is a shell executable stub that delegates to the already-running Carbide compiler/runtime session.

The investigation supports the core feasibility claim:

- Carbide already compiles C# in Mono-WASM through Roslyn.
- Carbide already emits deterministic PE/PDB bytes.
- Carbide already loads emitted PE bytes into a per-run collectible `AssemblyLoadContext`.
- Carbide already reflects and invokes executable assembly entry points.
- Carbide already has a workaround for Roslyn's synthesized blocking async-entry wrapper, so `async Task Main`, `async Task<int> Main`, and top-level `await` can work when routed through the current non-interactive run path.

The missing pieces are not the compiler or the in-process executable-assembly runner. The missing pieces are the facade boundary:

- a VFS executable catalog entry for `dotnet` / `dotnet.exe`;
- an async virtual-executable dispatch path, because compilation and browser host calls must not be synchronously blocked;
- a shell-to-host compiler bridge, because the VFS executable handler runs inside the shell program while the authoritative `CarbideSession` lives in the JavaScript host package;
- a public `runAssembly` style API extracted from `ProjectCompiler.RunAsync` so `dotnet exec app.dll` and `dotnet app.dll` can run already-compiled executable assemblies from the VFS;
- a VFS-aware port of the existing CLI build/run project pipeline, because today's `@carbide/cli` pipeline is Node filesystem based.

This proposal recommends proceeding with the facade, with a bounded SDK-compatible command subset. The facade should present a familiar `dotnet` command surface while being explicit that it is "Carbide dotnet", not Microsoft.NET.Sdk/MSBuild parity.

## Investigation Results

### Existing Carbide behavior

`ProjectCompiler.RunAsync` is already the closest thing to the desired runtime path. The current implementation:

- compiles with `OutputKind.ConsoleApplication`;
- emits PE bytes and portable PDB bytes;
- creates a per-run collectible `AssemblyLoadContext`;
- loads attached references into that context with `AssemblyLoadContext.LoadFromStream`;
- wires a context-scoped `Resolving` handler by simple assembly name;
- loads the emitted user assembly from a byte array stream;
- reads `assembly.EntryPoint`;
- bypasses Roslyn's synthesized sync wrapper for async entry points when possible;
- invokes `Main` with `string[] args` when the reflected signature accepts arguments;
- awaits `Task`, `Task<int>`, `ValueTask`, and `ValueTask<int>`;
- captures stdout/stderr and restores `Console` state in `finally`;
- unloads the run context when execution completes.

That means Carbide already performs the critical "load a compiled executable assembly and call its async Main" operation for assemblies it just emitted. The proposed work should extract that runner into a reusable core service rather than inventing a second execution path.

### Live public API probe

A Node-hosted probe was run from `src/Carbide/packages/core` against the built public API. The probe created a project, compiled this entry point, forwarded argv/stdin, awaited `Task.Delay(1)`, and returned `42`:

```csharp
using System;
using System.Threading.Tasks;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("args=" + string.Join(",", args));
        Console.WriteLine("stdin=" + Console.In.ReadToEnd());
        await Task.Delay(1);
        Console.WriteLine("after-delay");
        return 42;
    }
}
```

Observed output:

```json
{"kind":"build","success":true,"peBytes":4096,"pdbBytes":10376,"diagnostics":0}
{"kind":"run","success":true,"exitCode":42,"stdOut":"args=alpha,beta\nstdin=input-text\nafter-delay\n","stdErrLength":0,"diagnostics":0}
```

This is strong evidence that a `dotnet run` facade can be built on top of the current Carbide public API for source/project execution.

It does not prove that the public API can already run an arbitrary VFS DLL by path. Today, `session.addReference(bytes)` registers a DLL as metadata/reference input, not as an executable target. `dotnet exec` and `dotnet app.dll` need a new public API that accepts PE bytes as the primary executable assembly.

### Existing CLI behavior

The `@carbide/cli` package already contains useful command semantics:

- `carbide build --source ... --out ...`;
- `carbide build --project ... --out ...`;
- `carbide run --source ... -- ...program args`;
- `carbide run --project ... -- ...program args`;
- PackageReference resolution through `@carbide/nuget`;
- bounded `.csproj` parsing through `@carbide/msbuild-lite`;
- ProjectReference graph traversal and topological compilation.

The CLI is not directly reusable from a browser VFS executable because it reads and writes through Node `fs`. The right move is to extract or mirror its project pipeline behind a file-system-like abstraction, not to copy/paste a second `.csproj` implementation into the shell handler.

### Shell executable constraints

The current virtual executable system is synchronous:

- `IVirtualExecutableHandler.Execute(...)` returns `int`;
- `ShellDispatcher.ExecuteVirtualExecutable(...)` calls the handler synchronously;
- each shell interpreter calls the dispatcher synchronously when it resolves a virtual executable.

That is fine for `grep.exe`, `findstr.exe`, `python.exe`, `perl.exe`, and `cscript.exe` as currently implemented because their handlers run entirely inside the shell runtime. It is not fine for `dotnet.exe`. A `dotnet.exe` facade must cross to the browser host, await compilation, maybe await NuGet dependency resolution, and await user code execution.

Blocking those operations with `.Wait()` or `.GetAwaiter().GetResult()` is exactly the class of pattern that caused the earlier Mono-WASM single-threaded `Cannot wait on monitors on this runtime` failures. The facade must therefore force an async virtual-executable path into shell-core.

### Official CLI surface

Microsoft's command documentation makes three distinctions that map well to Carbide:

- `dotnet build` is project/solution oriented and produces assemblies;
- `dotnet run` builds/runs a project and forwards program arguments after `--`;
- `dotnet exec` and `dotnet <application.dll>` execute already-built framework-dependent applications.

Carbide should follow that shape where practical, but should declare its extensions and limits. Supporting loose `.cs` source files is useful for Carbide and consistent with existing `carbide build/run --source`, but it is a Carbide extension rather than stock `dotnet` behavior.

## Recommendation

Add `dotnet` as a Carbide SDK facade with these architectural rules:

- The VFS stub is discoverable as `dotnet` and `dotnet.exe`.
- The implementation never launches a native or host `dotnet` process.
- Source/project compilation delegates to the existing Carbide `Project` API and the existing CLI project graph pipeline.
- Executable DLL execution delegates to a new `runAssembly` API extracted from the current `ProjectCompiler.RunAsync` runner.
- The facade is async end to end.
- The shell handler owns command-line parsing and user-facing compatibility, but the host bridge owns Roslyn compilation, NuGet resolution, and executable assembly invocation.
- Unsupported SDK features produce crisp diagnostics with stable exit codes instead of pretending to work.

The feature should be positioned as:

```text
Carbide dotnet facade for browser/VFS C# build and run
```

It should not identify itself as a stock Microsoft .NET SDK.

## Proposed Executable Catalog

Use one command id:

```text
dotnet
```

Recommended search names:

- `dotnet`
- `dotnet.exe`

Recommended default stub paths:

- `/usr/bin/dotnet`
- `/usr/bin/dotnet.exe`
- `/bin/dotnet`
- `/bin/dotnet.exe`
- `/Program Files/Git/usr/bin/dotnet`
- `/Program Files/Git/usr/bin/dotnet.exe`
- `/Program Files/dotnet/dotnet.exe`

The first six paths make `dotnet` visible through the existing `cmd`, `pwsh`, and `bash` search-root rules without expanding default `PATH`. The `/Program Files/dotnet/dotnet.exe` path models the usual Windows installation location for path-qualified commands.

Do not initially install `/Windows/System32/dotnet.exe`. `dotnet.exe` is not a Windows system binary in a normal SDK installation. If script corpus data later shows frequent hard-coded `C:\Windows\System32\dotnet.exe`, add it as an explicit compatibility alias and document that it is intentionally synthetic.

Recommended metadata change:

```csharp
public enum VirtualExecutablePersonality
{
    Shell,
    Gnu,
    Windows,
    Language,
    Sdk,
}
```

If adding `Sdk` is considered too much metadata churn for the first patch, use `Language` temporarily, but tests and help text should still call the command an SDK facade rather than a language interpreter.

Suggested catalog helper:

```csharp
private static VirtualExecutableDefinition Sdk(string commandId, params string[] basenames)
    => new(
        commandId,
        VirtualExecutablePersonality.Sdk,
        BuildPaths(SdkRoots, basenames),
        basenames,
        HandlerKey);
```

## Command Scope

### First supported command set

| Command | Proposed behavior |
|---|---|
| `dotnet --help`, `dotnet -h`, `dotnet /?` | Print Carbide-specific help with supported and unsupported command groups. |
| `dotnet --version` | Print a Carbide facade version such as `Carbide dotnet facade 0.1 (.NET 10 reference surface)`. |
| `dotnet --info` | Print host kind, target framework surface, Carbide package versions if available, runtime mode, VFS note, and unsupported SDK features. |
| `dotnet --list-sdks` | Print one synthetic SDK row for the Carbide facade. |
| `dotnet --list-runtimes` | Print the bundled runtime assemblies/runtime identity that Carbide can report honestly. |
| `dotnet build [project-or-source]` | Compile a `.csproj` or a Carbide extension loose `.cs` source set, write PE/PDB to VFS. |
| `dotnet run [--project project] [--] args...` | Compile and run a project or current-directory project; optionally support Carbide loose-source mode. |
| `dotnet exec app.dll [args...]` | Read an executable assembly from VFS and run it in-process through the extracted runner. |
| `dotnet app.dll [args...]` | Alias for executable assembly run, matching common `dotnet MyApp.dll` usage. |
| `dotnet restore [project]` | Resolve bounded PackageReference graphs through `@carbide/nuget`, write lock/cache artifacts to VFS. |
| `dotnet clean [project]` | Delete Carbide output directories for the selected project/source root. |

### Deliberate Carbide extensions

The facade should support a loose-source workflow because Carbide already has it and because browser shell users will expect quick C# snippets without scaffolding:

```text
dotnet run Program.cs -- alpha beta
dotnet build Program.cs -o out
```

This is not stock `dotnet` CLI behavior. It should be labeled as a Carbide extension in `dotnet --help` and in diagnostics. The project-oriented path should still remain the primary compatibility target:

```text
dotnet run --project App.csproj -- alpha beta
dotnet build App.csproj
```

### Explicitly unsupported command groups

Return a stable unsupported-feature exit code and a diagnostic for:

- `dotnet new`, except possibly a later minimal `console` template;
- `dotnet test`;
- `dotnet publish`;
- `dotnet pack`;
- `dotnet tool`;
- `dotnet workload`;
- `dotnet sln`;
- `dotnet watch`;
- `dotnet format`;
- `dotnet nuget`;
- native AOT, apphost generation, COM hosting, Windows service hosting, single-file publish;
- arbitrary MSBuild targets, tasks, imports, SDK resolvers, source generators, and analyzers unless they are later implemented explicitly.

Unsupported commands should not shell out to the host. They should fail inside the VFS sandbox.

## Runtime Architecture

### Layering

The recommended layering is:

```text
shell command line
  -> VFS stub resolution
  -> async virtual executable handler
  -> dotnet facade parser
  -> shell-to-host compiler bridge
  -> @carbide/core session/project APIs
  -> Roslyn compile / AssemblyLoadContext run
  -> stdout/stderr/stdin/exit-code projection back to shell
```

The shell runtime should not link all of `@carbide/core` into itself. The browser host already owns the real `CarbideSession`; the `dotnet.exe` handler should call a narrow host bridge.

### Host bridge

Introduce a host-facing service boundary, conceptually:

```csharp
public interface IDotnetFacadeHost
{
    ValueTask<DotnetBuildResponse> BuildAsync(DotnetBuildRequest request, CancellationToken cancellationToken);
    ValueTask<DotnetRunResponse> RunProjectAsync(DotnetRunProjectRequest request, CancellationToken cancellationToken);
    ValueTask<DotnetRunResponse> RunAssemblyAsync(DotnetRunAssemblyRequest request, CancellationToken cancellationToken);
    ValueTask<DotnetRestoreResponse> RestoreAsync(DotnetRestoreRequest request, CancellationToken cancellationToken);
}
```

The interface shape is illustrative; the actual bridge may be JS-import based, JSON-message based, or internal to the browser demo host. The important contract is:

- Requests carry normalized VFS paths and file contents/bytes that the host needs.
- Responses carry diagnostics, stdout/stderr, exit code, emitted PE/PDB bytes, output paths, warnings, and structured unsupported-feature diagnostics.
- The bridge is async and does not block the Mono-WASM UI/event-loop thread.
- The bridge is a trust boundary: the shell program supplies VFS data, not host filesystem paths.

### Why not execute inside the shell handler directly?

The shell program is itself user code running inside the Carbide runtime. A direct in-shell compiler would either:

- require shipping Roslyn and the compiler services again inside the shell application;
- create unclear ownership between the shell's user-program assembly and the host `CarbideSession`;
- increase payload size and duplicate reference-pack loading;
- invite recursive bootstrapping bugs when `dotnet.exe` compiles code that runs in the same runtime instance that hosts the shell.

The bridge keeps authority in one place: the browser/Node host owns compiler services, while the shell owns UX and VFS state.

### Async virtual executable dispatch

Add async dispatch alongside the current sync path:

```csharp
public interface IAsyncVirtualExecutableHandler
{
    ValueTask<int> ExecuteAsync(VirtualExecutableInvocation invocation, CancellationToken cancellationToken);
}
```

Possible integration strategies:

- Add async methods through `ShellDispatcher`, then migrate shell interpreters to call async command dispatch where needed.
- Keep the synchronous handler interface for existing commands and add an adapter that returns completed `ValueTask<int>`.
- Let only commands marked as async use the new path at first, but do not block on them from synchronous interpreter code.

This is a hard prerequisite for browser correctness. The facade must not use synchronous waits around host compilation or user program execution.

### VFS file provider

The bridge needs a VFS-backed file provider that can answer:

- read text file by normalized path;
- read binary file by normalized path;
- write text/binary output file;
- enumerate directory;
- test existence/type;
- normalize Windows, POSIX, and current-directory paths consistently with the invoking shell;
- collect project inputs for a `.csproj` graph;
- materialize build outputs.

There are two plausible placements:

1. C# shell handler snapshots the relevant VFS subtree into a request payload and sends it to the host.
2. Host bridge calls back into shell VFS operations on demand.

The snapshot approach is simpler and more deterministic for a first implementation. It also avoids re-entrant file reads while the host is compiling. The callback approach is more memory efficient for large projects but requires a more complex lifetime and concurrency model.

Recommendation: start with bounded snapshots, with explicit size diagnostics for unexpectedly large trees. Add callback reads later only if real workloads require it.

## Build Semantics

### Project mode

For `.csproj` input, reuse the existing bounded pipeline:

- parse through `@carbide/msbuild-lite`;
- resolve packages through `@carbide/nuget`;
- traverse ProjectReference graphs where currently supported;
- create one Carbide project per node;
- compile in topological order;
- register upstream PE bytes as references for downstream projects;
- write root and dependency outputs to VFS.

The existing `@carbide/cli/src/project-file.ts` is the right semantic source, but it should be moved behind a filesystem abstraction before browser facade use.

### Loose source mode

For loose `.cs` files:

- assembly name defaults to the first source basename;
- output kind defaults to console application when an entry point is present, matching current `ProjectCompiler` inference;
- `-r` / `--reference` can attach VFS DLL references;
- `-o` / `--output` selects an output directory;
- omitted output writes to a synthetic SDK-like directory.

Recommended default output path:

```text
bin/Debug/net10.0/<AssemblyName>.dll
bin/Debug/net10.0/<AssemblyName>.pdb
```

`net10.0` is appropriate because Carbide's current reference surface is .NET 10. If the reference package changes, the facade should report and use the actual target framework surface.

### Output manifests

The facade should write a small build manifest next to outputs:

```text
bin/Debug/net10.0/.carbide-dotnet-build.json
```

Suggested contents:

- facade schema version;
- root assembly path;
- assembly name;
- target framework surface;
- source paths;
- project path if applicable;
- reference DLL paths and simple names;
- PackageReference outputs copied or available;
- ProjectReference outputs;
- warnings produced during build;
- whether the output is runnable.

This manifest is not required for simple execution, but it gives `dotnet exec` and later diagnostics a reliable dependency map without guessing every adjacent DLL.

## Run Semantics

### `dotnet run`

`dotnet run` should:

- resolve project/source inputs;
- build through the same path as `dotnet build`;
- execute the root assembly through the existing compile-and-run path or through the extracted assembly runner;
- forward program args after `--`;
- forward stdin from the current virtual executable invocation;
- write stdout/stderr to the invoking shell streams;
- return the user program exit code where available.

For the first slice, non-interactive execution is enough. Interactive execution is useful but should be its own acceptance surface because the shell is already an interactive program, and nested terminal ownership is easy to get subtly wrong.

### `dotnet exec` and `dotnet app.dll`

Add a public core API for executable assembly run:

```ts
const result = await session.runAssembly({
    pe,
    pdb,
    assemblyName,
    references,
    args,
    stdin,
});
```

And a matching C# service extraction:

```csharp
public sealed class AssemblyRunner
{
    public Task<RunResult> RunAsync(RunAssemblyRequest request);
}
```

The runner should use the same mechanics as `ProjectCompiler.RunAsync`:

- per-run collectible `AssemblyLoadContext`;
- `LoadFromStream` for executable and reference assemblies;
- simple-name `Resolving` handler;
- `Assembly.EntryPoint`;
- async-entrypoint fallback;
- `Task` / `Task<int>` / `ValueTask` / `ValueTask<int>` awaiting;
- stdout/stderr/stdin capture;
- `Console` state restoration;
- unload in `finally`.

For arbitrary DLLs, the runner cannot use Roslyn's `Compilation.GetEntryPoint`. That is acceptable: reflection over `Assembly.EntryPoint` is the correct primary mechanism. The existing async fallback already starts from the reflected entry point and can be shared.

### Dependency resolution for executable DLLs

Initial behavior should resolve dependencies in this order:

1. References listed in `.carbide-dotnet-build.json` when present.
2. DLLs adjacent to the executable assembly in the VFS output directory.
3. Explicit `--additional-deps` or `--reference` style facade options, if added.
4. Built-in framework/runtime assemblies already loaded by Carbide.

Do not try to implement full `.deps.json` probing in the first slice. It is more important to be correct for assemblies built by Carbide than to partially imitate hostfxr.

## Package Restore And Browser Dependency Loading

`dotnet restore` should be a facade over `@carbide/nuget`, not over a real NuGet client.

In browser mode, package resolution has these constraints:

- NuGet service endpoints may involve CORS behavior that differs by CDN and endpoint.
- Package payloads can be large; the facade should report bytes fetched/cached where practical.
- Offline/replay mode should use lock data and browser cache/storage where available.
- Allow-list policy from `@carbide/nuget` should stay active unless explicitly disabled by facade options.
- Native assets, build assets, analyzers, source generators, MSBuild `.targets`, and transitive tooling should remain unsupported unless a later design explicitly handles them.

The user has already accepted browser-side dependency loading for the executable catalog. That acceptance makes `dotnet restore` plausible, but the facade still needs crisp limits. It should say "resolved compile/runtime DLLs usable by Carbide", not "full NuGet restore".

## Interactive Execution

Interactive user programs are valuable, but should be staged after non-interactive `dotnet run` and `dotnet exec`.

Reasons:

- Existing `Project.runInteractive` is browser-only and terminal-object based.
- The shell REPL is already using the terminal.
- A nested `dotnet run` needs to hand the same terminal input stream to the user program without losing the parent shell's prompt/editor state.
- Ctrl+C, EOF, resize, and teardown must return control to the parent shell consistently.
- The current virtual executable invocation surface carries `TextReader`/`TextWriter`, not the browser terminal object and session lifecycle needed by `Project.runInteractive`.

Recommended staged behavior:

1. First implementation: non-interactive `dotnet run` buffers stdin from the invocation and captures stdout/stderr.
2. Second implementation: streaming stdout/stderr for long-running programs.
3. Third implementation: full nested interactive run using the same terminal stack mechanism that shell nesting uses.

This staging avoids coupling the first facade patch to terminal multiplexing.

## Diagnostics And Exit Codes

Use stable exit codes:

| Exit code | Meaning |
|---:|---|
| `0` | Success. |
| `1` | Compilation failed, execution failed, or user program returned `1` when no more specific code applies. |
| user code | User program returned an explicit nonzero exit code. |
| `2` | Facade command-line usage error. |
| `3` | Unsupported `dotnet` command or option. |
| `4` | VFS/project resolution failure. |
| `5` | NuGet/package resolution failure. |
| `6` | Invalid executable assembly or missing entry point. |
| `7` | Host bridge unavailable or failed before user code started. |

Diagnostics should name Carbide explicitly:

```text
dotnet: Carbide facade does not support 'dotnet publish'.
dotnet: use 'dotnet build' to emit VFS assemblies, or run 'dotnet --help' for supported commands.
```

Compilation diagnostics should preserve Roslyn file/line/span data and use VFS paths as source identities.

## Security And Trust Boundary

The facade executes user C# in the same browser runtime process as Carbide. It is not a sandbox boundary within the page. The supported security boundary is:

- no host OS process spawning;
- no host filesystem access;
- no direct native interop;
- no real registry/COM/service access;
- VFS-only file access for facade inputs/outputs;
- browser network access only through explicitly supported dependency-loading paths;
- best-effort assembly isolation through per-run collectible `AssemblyLoadContext`.

User code can still mutate process-global managed state, including `Console`, `AppContext`, culture, static fields in shared assemblies, and other BCL state. `ProjectCompiler.RunAsync` already restores the most important `Console` state, but it cannot make arbitrary user code hermetic.

This is acceptable for a browser coding shell, but documentation and diagnostics should avoid overclaiming sandbox strength.

## Lurking Issues

### Async dispatch is structural, not incidental

`dotnet.exe` is the first virtual executable whose natural implementation crosses an async host boundary. Treating it as sync would reintroduce the same Mono-WASM blocking hazards that Carbide has already spent substantial work avoiding.

### Two VFS authorities can diverge

The shell owns the in-memory VFS seen by `pwsh`, `cmd`, and `bash`. The JavaScript host owns the `CarbideSession`. If the bridge snapshots files, the host compiles a point-in-time copy. If it calls back into VFS, the host sees live state. The first model is easier to reason about; the second model is more memory efficient. Mixing the two casually would cause confusing race behavior.

### `dotnet build` expectations are enormous

Users may expect MSBuild imports, SDK resolvers, generated assembly attributes, globbing, analyzers, source generators, resources, satellite assemblies, apphost generation, publish profiles, RID-specific assets, and `.props`/`.targets` execution. The facade must be honest that it supports Carbide's bounded `.csproj` subset, not the full SDK.

### Executable DLL dependencies are not solved by loading one PE

`dotnet app.dll` is easy for self-contained trivial programs and hard for realistic dependency graphs. Carbide can load reference assemblies by bytes today, but executable dependency probing needs explicit VFS policy. A build manifest gives us a reliable path for assemblies produced by the facade.

### Runtime reference assemblies and runtime implementation assemblies differ

Compilation uses reference assemblies. Execution uses implementation assemblies loaded in the runtime plus user-supplied dependencies. The facade should not assume every compile-time reference is loadable at runtime or vice versa. Existing `session.addReference` behavior is metadata-oriented; `runAssembly` needs runtime-loadable bytes.

### Global `Console` state is shared

The runner redirects `Console.Out`, `Console.Error`, and sometimes `Console.In`. If a user program starts background work that writes after `Main` returns, output may leak into the parent shell or later runs. This is already a Carbide concern; `dotnet.exe` will make it more visible because users expect command isolation.

### Browser package resolution needs user-visible policy

Network failures, CORS failures, package allow-list refusals, lock mismatches, and cache misses should not all become "restore failed." The facade should surface distinct diagnostics so users know whether a package is unsupported, unavailable, or blocked by policy.

### Path identity must be normalized once

Carbide `Project.addSource` currently treats paths byte-for-byte. The facade should choose source identity strings deliberately, probably normalized VFS paths relative to the project root for project mode and basename-or-relative path for loose source mode. Inconsistent slash/case treatment will produce duplicate document bugs and misleading diagnostics.

### Direct DLL execution should not accidentally execute library DLLs

`dotnet app.dll` should fail with a clear missing-entry-point diagnostic when the DLL is a library. It should not guess a type/method name unless a later explicit `--entry-point` extension is designed.

### Browser payload size can creep

The facade itself should be small if it delegates to existing host infrastructure. Pulling Roslyn, NuGet, or project parsing into the shell program again would duplicate payload and should be avoided.

## Implementation Workstreams

This is the recommended implementation order. The size anchors are subsystem/artifact counts, not calendar estimates.

1. Extract the assembly runner from `ProjectCompiler.RunAsync`.
   - One reusable runner type.
   - One request/response DTO pair.
   - One shared async-entrypoint resolver.
   - Existing `RunAsync` and `RunInteractiveAsync` continue to pass tests through the extracted path where practical.

2. Add public `runAssembly` API in `@carbide/core`.
   - One C# JSExport.
   - One TypeScript `CarbideSession.runAssembly(...)` method.
   - JSON schema parser/serializer additions.
   - Node tests for valid executable, library-without-entrypoint, args/stdin, and suspending async `Main`.

3. Add async virtual executable dispatch.
   - One async handler interface or equivalent union.
   - Async dispatcher method.
   - Shell interpreter call-site updates for virtual executable invocation.
   - Compatibility adapter for existing sync handlers.

4. Add the dotnet facade handler skeleton.
   - Catalog entry and stub installation.
   - Help/version/info/list commands.
   - Command-line parser.
   - Unsupported command diagnostics.
   - Cross-shell discovery tests.

5. Add the shell-to-host bridge.
   - Host service abstraction.
   - Browser implementation backed by the existing `CarbideSession`.
   - Test/fake implementation for xunit shell tests.
   - VFS snapshot request payload.

6. Port build/run project semantics to a VFS file provider.
   - Extract `@carbide/cli` project pipeline behind a filesystem abstraction.
   - Support project mode and loose-source mode.
   - Write DLL/PDB/manifest outputs to VFS.
   - Preserve Roslyn diagnostics and project warnings.

7. Add executable DLL run.
   - `dotnet exec app.dll`.
   - `dotnet app.dll`.
   - Manifest-assisted dependency loading.
   - Adjacent DLL fallback.

8. Add restore and clean.
   - Restore maps to `@carbide/nuget`.
   - Clean deletes facade output directories.
   - Diagnostics distinguish unsupported package assets from network/cache/policy failures.

9. Add interactive execution.
   - Streaming output first.
   - Nested terminal input second.
   - Ctrl+C/EOF/resize teardown tests.

## Acceptance Test Matrix

Core API tests:

- `runAssembly` runs a simple `static int Main()`.
- `runAssembly` runs `static async Task<int> Main(string[] args)` with a genuine suspension.
- `runAssembly` forwards stdin.
- `runAssembly` fails cleanly for a library DLL with no entry point.
- `runAssembly` loads one sibling dependency DLL by manifest.

Shell catalog tests:

- `pwsh`: `Get-Command dotnet` resolves the virtual executable.
- `cmd`: `where dotnet` resolves the virtual executable.
- `bash`: `which dotnet` resolves the virtual executable.
- Path-qualified `/Program Files/dotnet/dotnet.exe --version` works.

Facade command tests:

- `dotnet --help`.
- `dotnet --info`.
- `dotnet --list-sdks`.
- `dotnet build Program.cs -o out`.
- `dotnet run Program.cs -- alpha beta`.
- `dotnet build App.csproj`.
- `dotnet run --project App.csproj -- alpha beta`.
- `dotnet exec out/App.dll alpha beta`.
- `dotnet out/App.dll alpha beta`.
- `dotnet publish` fails with unsupported-feature diagnostic.
- `dotnet app.dll` fails clearly for a non-executable library.

Browser integration tests:

- Browser `carbide-pwsh` session can compile and run a hello-world C# file through `dotnet run`.
- Browser session can build to VFS and then execute the emitted DLL through `dotnet exec`.
- A suspending async `Main` does not trigger `Cannot wait on monitors on this runtime`.
- Parent shell prompt/editor returns after the command completes.

## Open Decisions

1. Should the first implementation add `VirtualExecutablePersonality.Sdk`, or use `Language` until more SDK/toolchain facades exist?
2. Should loose source mode use `dotnet run Program.cs` syntax, or require an explicit Carbide option such as `dotnet run --source Program.cs` to avoid looking like stock SDK behavior?
3. Should project snapshots include the whole project directory by default, or only files reachable from `.csproj` items and known glob patterns?
4. Should `dotnet restore` write `carbide.lock.json`, a `.NET`-style assets file, or both?
5. How much `--info` should report about the underlying Mono-WASM runtime without making compatibility promises?
6. Should interactive `dotnet run` be blocked behind an explicit option until terminal nesting is fully correct?

## Conclusion

Adding `dotnet.exe` is feasible and strategically useful, but it should be designed as a Carbide SDK facade rather than a process emulator. The key evidence is that Carbide already compiles to PE bytes and already runs a genuinely suspending async `Main` by loading emitted assemblies into a per-run `AssemblyLoadContext`.

The first production-quality implementation should focus on a small, honest command set: `--help`, `--info`, `build`, `run`, `exec`, direct DLL execution, bounded `restore`, and `clean`. The work should start by extracting `runAssembly` and adding async virtual executable dispatch. Once those seams exist, `dotnet.exe` becomes a high-value facade over infrastructure Carbide already owns, instead of a risky attempt to simulate a full SDK inside a shell handler.
