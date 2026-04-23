# Report: scope of unifying Carbide shell endpoints around one shared session

- Created (UTC): 2026-04-23T21:12:38Z
- Repository HEAD: e5c1e2b48eea1534033dbf6bcd549b2059db91e7
- Status: Informational implementation-scope evaluation
- Audience: Vladimir; future Carbide contributors working on browser shells, `carbide-shell-core`, and the Carbide package/demo surface
- Scope: evaluate the work required to remove `carbide-multishell` as a separate browser endpoint while making `carbide-pwsh`, `carbide-cmd`, and `carbide-bash` all boot the same fully capable shared-shell session; enumerate the main technical risks and hidden scope
- Related code:
  - `src/Carbide/packages/carbide-pwsh/src/Program.cs`
  - `src/Carbide/packages/carbide-pwsh/src/Host/ShellHost.cs`
  - `src/Carbide/packages/carbide-pwsh/src/Host/PwshPromptEditor.cs`
  - `src/Carbide/packages/carbide-pwsh/src/Cmdlets/Shell/CrossShellCommands.cs`
  - `src/Carbide/packages/carbide-pwsh/src/Cmdlets/Pipeline.cs`
  - `src/Carbide/packages/carbide-cmd/src/Program.cs`
  - `src/Carbide/packages/carbide-bash/src/Program.cs`
  - `src/Carbide/packages/carbide-multishell/src/Program.cs`
  - `src/Carbide/packages/carbide-multishell/src/MultishellSession.cs`
  - `src/Carbide/packages/carbide-multishell/src/VirtualExecutableCatalog.cs`
  - `src/Carbide/packages/carbide-multishell/src/MultishellVirtualExecutableHandler.Advanced.cs`
  - `src/Carbide/packages/carbide-shell-core/src/Dispatch/ShellDispatcher.cs`
  - `src/Carbide/packages/core/src/ts/project.ts`
  - `src/Carbide/packages/core/src/ts/types.ts`
  - `src/Carbide/packages/carbide-pwsh/index.html`
  - `src/Carbide/packages/carbide-multishell/index.html`
- Related docs:
  - [Multi-shell (cmd + bash alongside pwsh) with cross-shell invocation](../proposals/carbide-multi-shell-proposal__2026-04-21__23-30-00-000000__d9a71f3c5b68.md)
  - [Virtual executable stubs for common `System32` and Git `usr/bin` tools in `carbide-multishell`](../proposals/carbide-multishell-vfs-executable-stubs-proposal__2026-04-22__23-10-39-000000__6827e976e1d5.md)
  - [Detailed implementation plan: `carbide-multishell` virtual executable stubs](../planning/carbide-multishell-vfs-executable-stubs-detailed-plan__2026-04-23__01-18-24-060735__6d4f2a9b1c7e.md)

## Summary

The requested end state is feasible, and most of the hard shell semantics already exist. The main gap is not interpreter capability; it is **composition topology**. Today, the only place that wires pwsh, cmd, bash, the shared VFS/env/dispatcher, and the virtual executable catalog into one coherent session is `carbide-multishell`. The standalone endpoints do not boot that topology.

That is why `cmd` does not work from the standalone `carbide-pwsh` endpoint today: `src/Carbide/packages/carbide-pwsh/src/Program.cs` creates a private `ShellHost`, and that host registers only the pwsh kernel and pwsh stub paths. No cmd kernel, no bash kernel, and no `VirtualExecutableCatalog` are present in that session. The same isolation exists in the standalone cmd and bash entry points.

So the core implementation question is not "can the shells do this?" They already can inside `MultishellSession`. The real question is "how do we make the three public endpoints all boot that same session without creating new drift, browser-build breakage, or prompt-UX regressions?"

My recommendation is to treat this first as an **endpoint unification** change, not an internal-package deletion. In other words: remove `carbide-multishell` as a public browser route, but keep its shared-session/runtime code as the implementation behind the three public endpoints until there is a separate reason to rename or extract it.

## Requested end state

The request implies all of the following must become true at once:

- `carbide-multishell` is no longer a separate browser endpoint.
- `carbide-pwsh`, `carbide-cmd`, and `carbide-bash` differ only by the shell they start in.
- Every endpoint boots one shared session containing:
  - one `VirtualFileSystem`
  - one `EnvVarStore`
  - one `AppRegistry`
  - one `ShellDispatcher`
  - pwsh, cmd, and bash kernels all registered
  - the full virtual executable catalog installed into the VFS
- From any starting shell, the user can enter nested interactive shells (`cmd`, `bash`, `pwsh`) and return with `exit`.
- The registered executable set implemented earlier is visible in the VFS and executable from any endpoint.

That is a coherent target. It is also importantly narrower than "eliminate every internal thing named multishell". Those are different scope levels.

## Current state

### The working shared session already exists

`src/Carbide/packages/carbide-multishell/src/MultishellSession.cs` already constructs the exact topology the request wants:

- shared VFS
- shared env store
- shared app registry
- shared dispatcher
- `CarbidePwsh.Host.ShellHost`
- `CarbideCmd.Host.ShellHost`
- `CarbideBash.Host.ShellHost`
- `VirtualExecutableCatalog.Install(...)`

`src/Carbide/packages/carbide-multishell/src/Program.cs` already supports `--shell <name>` through `Environment.GetCommandLineArgs()`. On the browser side, `Project.runInteractive(...)` already supports `args`, so the runtime hook needed for "same program, different starting shell" already exists in principle.

### The standalone endpoints are isolated sessions

The standalone entry points do not share that topology:

- `src/Carbide/packages/carbide-pwsh/src/Program.cs` constructs `new ShellHost()`.
- `src/Carbide/packages/carbide-cmd/src/Program.cs` constructs `new ShellHost()`.
- `src/Carbide/packages/carbide-bash/src/Program.cs` constructs `new ShellHost()`.

Each of those hosts creates its own private session state when no shared dependencies are passed in. That means:

- pwsh standalone registers only the pwsh kernel and pwsh stub paths
- cmd standalone registers only the cmd kernel and cmd stub paths
- bash standalone registers only the bash kernel and bash stub paths
- none of the standalone entry points call `VirtualExecutableCatalog.Install(...)`
- the standalone hosts seed a smaller default environment than `MultishellSession` does

So the three public entry points are not thin shells over one runtime today. They are three different boot shapes.

### Why `cmd` fails from the standalone `carbide-pwsh` endpoint

The failure path is straightforward:

1. In standalone `carbide-pwsh`, `cmd` is an alias for `Invoke-Cmd`.
2. `Invoke-Cmd` calls `RequireDispatcher(context, "cmd")` in `CrossShellCommands.cs`.
3. That helper asks the current dispatcher whether a cmd kernel is registered.
4. In the standalone pwsh host, only the pwsh kernel was registered.
5. The helper returns null and `Invoke-Cmd` throws `Invoke-Cmd: the session has no cmd kernel registered.`

This is not a bug in cmd support. It is a consequence of booting the wrong session topology for that endpoint.

### Browser endpoint coverage is asymmetric today

Current browser/demo package state:

- `carbide-pwsh` has `index.html`, `package.json`, `scripts/serve.mjs`, `scripts/smoke.mjs`.
- `carbide-multishell` has the same browser-facing set.
- `carbide-cmd` and `carbide-bash` currently have no browser `index.html`, no `package.json`, and no browser smoke script.

So the requested end state is not just "delete one page". It also implies adding two public browser surfaces or a shared mechanism that can expose those routes.

## Scope of required changes

## Workstream 1: unify the public endpoint boot path

This is the core workstream.

The three public endpoints need to stop booting isolated shell hosts and instead boot a single shared session composition. There are two main ways to do that.

### Option A: endpoint-only unification

Keep `carbide-multishell` as an internal implementation package/runtime, but stop exposing it as its own browser endpoint. Make the three public browser endpoints all compile/run the shared session and pass different startup args.

What this would touch:

- 3 browser entry pages (`carbide-pwsh`, `carbide-cmd`, `carbide-bash`)
- 2 new package roots or equivalent browser asset surfaces for cmd/bash
- 1 shared browser source/dependency manifest, or 3 duplicated lists if done mechanically
- 3 smoke routes or 1 parameterized smoke matrix
- 2-3 README/index documents

This is the smallest implementation that satisfies the public endpoint request.

### Option B: deeper package/runtime consolidation

If the goal also includes "there should no longer be a distinct internal `carbide-multishell` project/package", scope jumps materially.

Why it jumps:

- `CarbideMultishell.csproj` currently depends on pwsh, cmd, bash, and shell-core.
- making `carbide-pwsh` depend on `carbide-multishell` directly would create a project-reference cycle
- the session-wiring code, virtual-executable catalog, and shared outer runner would have to move again into a lower-level project or into `carbide-shell-core`
- the `SharpCompress` dependency and virtual-executable handler ownership would need a new home

This is a real refactor, not a thin endpoint change.

## Workstream 2: browser composition must stop being hand-maintained per page

This is the most obvious lurking maintenance problem.

Both existing browser pages manually enumerate source files in JavaScript arrays. That has already drifted.

Concrete current example:

- `src/Carbide/packages/carbide-multishell/src/MultishellSession.cs` calls `VirtualExecutableCatalog.Install(...)`
- but `src/Carbide/packages/carbide-multishell/index.html` does not include:
  - `src/VirtualExecutableCatalog.cs`
  - `src/MultishellVirtualExecutableHandler.Core.cs`
  - `src/MultishellVirtualExecutableHandler.Basic.cs`
  - `src/MultishellVirtualExecutableHandler.Advanced.cs`

That means the current browser-side source-list composition is already stale.

If we create three public endpoints all pointing at the same shared session, the source/dependency manifest really wants to exist once, not as three or four independent arrays in HTML files.

Minimal acceptable fix:

- one shared JS manifest describing source files per package plus external references
- each endpoint page imports that manifest and only varies title, copy, and startup shell

Anything more manual will keep reintroducing drift.

## Workstream 3: browser dependency loading, not just source loading

This is the most important hidden technical issue.

The browser demos do not build from csproj metadata. They build from source lists fed into `Project.addSource(...)`. That worked tolerably while the demos used only repo-local source files plus the default BCL/reference-pack surface.

The full virtual executable catalog changed that:

- `src/Carbide/packages/carbide-multishell/src/CarbideMultishell.csproj` now has a `PackageReference` to `SharpCompress`
- `src/Carbide/packages/carbide-multishell/src/MultishellVirtualExecutableHandler.Advanced.cs` uses `SharpCompress.Compressors.BZip2`

So exposing the full executable catalog from every browser endpoint is not just "add more `.cs` files to the page". The browser boot path also needs a strategy for external metadata references.

That strategy could be one of:

- explicitly fetch `SharpCompress.dll` and attach it with `session.addReference(...)`
- stop browser demos from compiling purely from source lists and instead compile against built artifacts that already carry package references
- split the virtual executable catalog so the browser build gets a reduced catalog that avoids external packages

Only the first two actually satisfy the user's request as stated, because the request explicitly wants the entire executable set available from any endpoint.

This is the sharpest lurking issue in the whole change.

## Workstream 4: interactive-shell UX abstraction

Semantically, nested interactive shells already work through `ShellDispatcher.RunInteractive(...)` and `RequestSubShellException`.

UX-wise, the shared runner is not symmetric yet.

Current state:

- the standalone pwsh endpoint uses `PwshPromptEditor`
- the shared multishell runner uses a generic async line loop over `Console.In.ReadLineAsync()`
- cmd and bash do not currently have richer line editors of their own

So if we simply point `carbide-pwsh` at the current shared multishell runner, we are very likely to regress the pwsh-specific prompt editor features that the standalone pwsh endpoint currently advertises:

- history walk
- tab completion
- `Esc` clear
- `Ctrl+C` interrupt handling
- cursor movement/editing shortcuts

This leads to an important design choice.

### Choice 1: accept a pwsh UX regression for the first unification pass

This is technically simplest. The three endpoints truly differ only by starting shell, but the pwsh endpoint loses its richer top-level editor.

### Choice 2: preserve pwsh's richer editor only for the top-level pwsh endpoint

This keeps the current pwsh browser experience, but then the three endpoints are no longer literally the same shell host with different startup args. They are still close, but not identical.

### Choice 3: introduce a shared per-kernel interactive input abstraction

This is the cleanest long-term design. The shared outer runner would ask the active shell whether it has a custom line editor. pwsh could provide one; cmd/bash could keep the default. Nested pwsh shells would then also be able to use the richer editor.

This is the best design if interactive UX parity matters. It is also additional scope, likely touching the shared runner, `IShellKernel` or a companion interface, pwsh tests, and browser smoke coverage.

## Workstream 5: command discovery and completion need to catch up with executability

Even after endpoint unification, there is still a distinction between "you can run it" and "the shell advertises it well".

In particular, `carbide-pwsh`'s discovery surfaces currently do not enumerate the shared virtual executable catalog:

- `ShellHost.GetInteractiveCommandNames()` does not consult the dispatcher's virtual executable registry
- `Get-Command` in `CommandDiscoveryCommands.cs` enumerates cmdlets, aliases, and functions, but not virtual executables

So after endpoint unification, pwsh could correctly run `grep`, `sed`, `awk`, `robocopy.exe`, and friends, yet still under-report them in completion and discovery.

That is not a blocker for making the executables work, but it is a real UX mismatch and a likely source of confusion.

## Workstream 6: public browser asset/package plumbing

Because `carbide-cmd` and `carbide-bash` do not currently have browser package roots, the endpoint change also needs concrete browser asset work.

At minimum this means introducing either:

- 2 new browser package surfaces (`carbide-cmd`, `carbide-bash`) with their own `index.html`, `package.json`, and serve/smoke scripts
- or one shared host package that can serve 3 routes with per-route configuration

If the goal is clean public URLs under `/packages/carbide-pwsh/`, `/packages/carbide-cmd/`, and `/packages/carbide-bash/`, the first shape is probably simplest even if the implementation underneath is shared.

## Scope tiers

## Tier 1: browser endpoint unification only

This tier assumes:

- the internal `carbide-multishell` project/runtime may remain
- only the public browser/demo endpoints are consolidated
- local `dotnet run` entry points may stay divergent for now
- pwsh prompt-editor preservation is optional, not required in the first pass

This is the smallest scope that actually satisfies the endpoint request in a browser sense.

Indicative artifact count:

- 3 public browser entry pages
- 1 shared manifest/helper layer for source and dependency lists
- 2 new browser package manifests for cmd/bash, unless a shared host route is chosen
- 3 smoke paths or 1 parameterized smoke matrix
- 2-3 documentation/index updates

This is a moderate change, but mostly mechanical.

## Tier 2: browser endpoint unification plus pwsh editor preservation

Adds:

- a shared interactive-runner abstraction that can preserve pwsh's prompt editor without keeping a separate pwsh-only boot path
- prompt-editor-aware browser validation
- likely 4-8 extra code/test artifacts over Tier 1

This is still very feasible, but no longer purely mechanical.

## Tier 3: eliminate the internal `carbide-multishell` project/package as well

Adds:

- extraction or relocation of `MultishellSession`, `VirtualExecutableCatalog`, and the outer runner into a lower-level project
- project-reference graph changes to avoid cycles
- a new home for the `SharpCompress`-using handlers and related package reference
- more widespread docs/test/project-file edits

This is the only tier that is meaningfully architectural.

## Lurking issues and traps

## 1. Project-reference cycles if the change is interpreted too literally

If `carbide-pwsh` starts referencing `carbide-multishell` as a project, the current graph becomes cyclic immediately because `carbide-multishell` already references `carbide-pwsh`.

That is why "remove the endpoint" is much cheaper than "remove the package/project".

## 2. The pwsh prompt editor is easy to lose accidentally

A naive endpoint merge that just swaps in the current multishell runner would likely remove pwsh's richer line editing from the top-level pwsh endpoint. That would be a user-visible regression.

## 3. Browser source-list drift is already real, not hypothetical

The current `carbide-multishell/index.html` is already missing four repo-local source files required by the C# it lists. That is concrete evidence that hand-maintained per-page source arrays are no longer safe.

## 4. External package references are now part of the browser story

Once the full executable catalog is required at every endpoint, browser composition has to account for `SharpCompress` and any future package references of the same kind. If this is ignored, the endpoints will drift into "looks unified, compiles less".

## 5. Discoverability will still lag unless we explicitly extend it

Without extra work, pwsh can become able to run the shared executable catalog while still failing to complete or list it through `Get-Command` and prompt completion.

## 6. Shell-specific precedence still matters

Endpoint unification does not erase shell-specific command precedence.

Examples in pwsh:

- `sort` still prefers `Sort-Object`
- `where` still prefers `Where-Object`
- `fc` still prefers `Format-Custom`

Those virtual executables remain runnable by explicit path or explicit executable name, but not necessarily by bare basename in pwsh. That is probably acceptable, but it should be treated as an intentional contract, not a surprise.

## 7. Local-entry divergence can remain even after browser unification

If only the browser routes are unified, `dotnet run` for `carbide-pwsh`, `carbide-cmd`, and `carbide-bash` will still boot isolated hosts. That is not wrong, but it should be a deliberate decision. Otherwise future maintainers will assume the packages are symmetric when they are only browser-symmetric.

## 8. Smoke coverage needs to follow the public contract

Today there is pwsh browser smoke coverage and multishell browser smoke coverage. After this change, the public contract becomes three endpoints. That means validation should follow that surface, not the retired route.

## Recommended implementation direction

I recommend the following order.

1. Treat this as a public endpoint unification first.

Keep `carbide-multishell` as an internal composition/runtime package for the first implementation. Remove it as a public browser endpoint, not necessarily as code.

2. Make the three public browser endpoints all boot the same shared session.

Use the existing `MultishellSession` and the existing `--shell` startup selector, driven through `Project.runInteractive({ args: [...] })` or through very thin wrapper programs if needed.

3. Introduce one shared browser manifest for source files and external references.

Do not duplicate source arrays across three pages again. The current drift is already evidence that this will not stay correct.

4. Decide explicitly whether pwsh prompt-editor parity is in scope for the first pass.

If yes, add a shared interactive-input abstraction before deleting the current pwsh boot path. If no, document the temporary regression clearly.

5. Only after the public route consolidation is stable, decide whether the internal `carbide-multishell` project/package should be renamed or extracted.

That second question is real, but it is not required to satisfy the user-visible request.

## Bottom line

The requested change is **moderate if interpreted as endpoint unification**, and **substantially larger if interpreted as internal package deletion/refactoring**.

The good news is that the shell functionality itself is already mostly there. The shared session, cross-shell dispatch, nested interactive shells, and virtual executable catalog already exist. The main work is to stop booting different worlds from different public routes.

The main hidden costs are:

- browser dependency/reference handling for the full executable catalog
- avoiding per-page source-list drift
- deciding what happens to the pwsh prompt editor
- avoiding accidental escalation into a project-graph refactor that the endpoint request does not actually require

If the implementation is scoped carefully around the public endpoint surface, this looks like a strong, tractable next change rather than a rewrite.
