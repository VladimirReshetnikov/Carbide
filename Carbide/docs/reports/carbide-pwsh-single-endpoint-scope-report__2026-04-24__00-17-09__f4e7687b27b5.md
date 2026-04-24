# Report: scope of converging Carbide browser shells onto `carbide-pwsh` only

- Created (UTC): 2026-04-24T00:17:09Z
- Repository HEAD: 88b1db86277bb9620d8e12d59a0814ece3a42a45
- Status: Informational implementation-scope evaluation
- Audience: Vladimir; future Carbide contributors working on Carbide browser shells, the shared shell runtime, and the public demo/package surface
- Scope: evaluate the narrower design where `carbide-pwsh` is the only public browser endpoint, `cmd` and `bash` are entered from inside that endpoint, browser-side dependency loading for the full virtual executable catalog is explicitly allowed, and `carbide-multishell` ceases to be a separate public route
- Related code:
  - `src/Carbide/packages/carbide-pwsh/src/Program.cs`
  - `src/Carbide/packages/carbide-pwsh/src/Host/ShellHost.cs`
  - `src/Carbide/packages/carbide-pwsh/src/Host/PwshPromptEditor.cs`
  - `src/Carbide/packages/carbide-pwsh/src/Cmdlets/Shell/CrossShellCommands.cs`
  - `src/Carbide/packages/carbide-pwsh/src/Cmdlets/Pipeline.cs`
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
  - [Scope report: unifying Carbide shell endpoints around one shared session](carbide-shell-endpoint-unification-scope-report__2026-04-23__21-12-38__9c7d7f44715e.md)
  - [Multi-shell (cmd + bash alongside pwsh) with cross-shell invocation](../proposals/carbide-multi-shell-proposal__2026-04-21__23-30-00-000000__d9a71f3c5b68.md)
  - [Virtual executable stubs for common `System32` and Git `usr/bin` tools in `carbide-multishell`](../proposals/carbide-multishell-vfs-executable-stubs-proposal__2026-04-22__23-10-39-000000__6827e976e1d5.md)
  - [Detailed implementation plan: `carbide-multishell` virtual executable stubs](../planning/carbide-multishell-vfs-executable-stubs-detailed-plan__2026-04-23__01-18-24-060735__6d4f2a9b1c7e.md)

## Summary

This narrower design is materially simpler than the three-public-endpoint plan. The shells already know how to coexist inside one shared session, and the public browser surface can collapse to a single route without creating any new interpreter work. The key simplification is that we no longer need browser-facing `carbide-cmd` and `carbide-bash` package surfaces at all.

Under this design, the public contract becomes:

- open `carbide-pwsh`
- start in pwsh
- type `cmd` or `bash` whenever needed
- all shells share one VFS, one env store, one dispatcher, and one executable catalog

That removes the largest packaging/documentation part of the earlier plan. We no longer need to stand up and maintain:

- a browser route for `carbide-cmd`
- a browser route for `carbide-bash`
- separate browser smoke flows for those routes
- duplicate page shells that differ only in startup flavor

The main remaining engineering question is not shell capability. It is **how `carbide-pwsh` should host the shared session while preserving the pwsh-first user experience**. In particular, if we switch the endpoint to the current `carbide-multishell` outer runner mechanically, we likely regress the richer `PwshPromptEditor` behavior that the current `carbide-pwsh` endpoint already exposes.

If browser-side dependency loading for the full executable catalog is acceptable, then the strongest former blocker becomes an implementation task rather than a design objection. That leaves one dominant trade-off:

- simplest path: make `carbide-pwsh` boot the existing shared runner and accept a plainer top-level pwsh UX
- better path: make `carbide-pwsh` boot the shared session through a pwsh-branded runner that preserves the current prompt-editor experience

My recommendation is the second path. It is still much smaller than the three-endpoint plan and it keeps the public story clean: one pwsh entrypoint, full shell toolbox inside it.

## What changes relative to the earlier three-endpoint report

The previous report evaluated this public surface:

- `carbide-pwsh`
- `carbide-cmd`
- `carbide-bash`

all booting the same shared session, with `carbide-multishell` retired as a separate endpoint.

This newer design drops two public routes entirely. That removes several whole workstreams from scope:

- no browser `index.html` / `package.json` / `serve` / `smoke` setup for cmd
- no browser `index.html` / `package.json` / `serve` / `smoke` setup for bash
- no need to decide whether three endpoints should all preserve shell-specific top-level editors
- no need to document three public routes that are intentionally near-identical

The scope now concentrates on a single question:

- how should the `carbide-pwsh` public endpoint boot the shared session?

That is a significantly cleaner problem.

## Current state

### The shared runtime already exists

`src/Carbide/packages/carbide-multishell/src/MultishellSession.cs` already creates the session shape we want:

- shared `VirtualFileSystem`
- shared `EnvVarStore`
- shared `AppRegistry`
- shared `ShellDispatcher`
- pwsh host
- cmd host
- bash host
- `VirtualExecutableCatalog.Install(...)`

That means the functionality needed by the proposed public endpoint is already present in repo-local code.

### The current public `carbide-pwsh` endpoint does not use that runtime

The current `carbide-pwsh` endpoint still boots a standalone pwsh-only host:

- `src/Carbide/packages/carbide-pwsh/src/Program.cs` constructs `new ShellHost()`
- `src/Carbide/packages/carbide-pwsh/src/Host/ShellHost.cs` registers only the pwsh kernel and pwsh stub paths
- no cmd kernel is present
- no bash kernel is present
- no virtual executable catalog is installed

That is why `cmd` from the standalone `carbide-pwsh` page fails today.

### The current public `carbide-pwsh` endpoint does have a better top-level UX

The current standalone pwsh endpoint uses:

- `PwshPromptEditor`
- prompt-aware history
- cycling tab completion
- cursor editing shortcuts
- explicit `Ctrl+C` handling

The current `carbide-multishell` outer runner does not use that editor. It runs a generic shell-stack loop over `Console.In.ReadLineAsync()` and delegates syntax completeness to the active kernel.

So the public route simplification does not remove the prompt-UX question. It makes it more central.

## Why the one-endpoint plan is attractive

## 1. It matches the product story better

If pwsh is the front door, then the browser story becomes easy to explain:

- \"Carbide gives you a pwsh-first shell environment.\"
- \"You can drop into `cmd` or `bash` when needed.\"
- \"Everything still shares one sandboxed VFS and one tool surface.\"

That is easier to communicate than three public endpoints which are mostly the same runtime in different startup modes.

## 2. It reduces packaging and maintenance surface

Compared to the three-endpoint plan, this approach removes:

- 2 browser package roots
- 2 browser landing pages
- 2 route-specific smoke scripts or smoke branches
- several route-specific README/index mentions

The underlying runtime may still remain multishell internally, but the public artifact count becomes much smaller.

## 3. It makes pwsh discoverability more worth polishing

When pwsh is the only public endpoint, investing in pwsh-side discovery of shared tools becomes clearly worthwhile. We do not need to split polish effort across three public shells. We can make the pwsh experience strong and let cmd/bash stay available as dialect escapes.

## 4. It avoids a fake symmetry

The current codebase is not actually symmetric across shells at the user-experience level:

- pwsh has a richer browser prompt editor
- cmd and bash do not
- pwsh has far richer discovery and presentation surfaces

A one-endpoint design acknowledges that asymmetry rather than pretending the public surface is flatter than it really is.

## Required changes

## Workstream 1: repoint the public `carbide-pwsh` browser route at a shared session

This is the essential change.

The public `carbide-pwsh` page needs to stop compiling/running the pwsh-only `Program.cs` and instead boot a shared session that includes pwsh, cmd, bash, and the virtual executable catalog.

There are two realistic ways to do that.

### Variant A: boot the existing `carbide-multishell` runner under pwsh branding

Mechanically:

- `carbide-pwsh/index.html` compiles the shared runtime instead of the pwsh-only runtime
- it launches the existing multishell outer program
- it passes `--shell pwsh` through `Project.runInteractive({ args: [...] })`
- `carbide-multishell` stops being a separate public browser page

This is the smallest implementation.

It already lines up with the existing `CarbideMultishell` program shape because:

- the runner already accepts `--shell`
- the runner already understands nested shell entry through `RequestSubShellException`
- the runner already knows how to keep one shell stack alive

The biggest downside is user-visible: the top-level pwsh prompt becomes the generic multishell prompt loop rather than the current `PwshPromptEditor` flow.

### Variant B: add a pwsh-branded shared-session runner

Mechanically:

- keep `MultishellSession` as the shared session composition
- add a new pwsh-public runner that constructs `MultishellSession`
- start in pwsh
- keep pwsh branding and pwsh-specific top-level editing behavior
- still allow `cmd` and `bash` entry into nested interactive shells
- retire `carbide-multishell` as a separate public route

This is slightly more work than Variant A, but it keeps the public endpoint honest: the page is named `carbide-pwsh`, and the initial interaction actually feels like pwsh rather than like a generic shell stack manager.

I consider this the better design.

## Workstream 2: shared browser source/dependency manifest

This still needs to happen even in the one-endpoint plan.

The current browser pages enumerate C# source files by hand inside HTML. That has already drifted. The current `carbide-multishell/index.html` does not include several `.cs` files now required by the runtime it claims to compile.

Because the one-endpoint plan explicitly accepts browser-side dependency loading, the browser composition layer should become a first-class artifact rather than a hand-maintained array in one page.

Minimum useful change:

- one shared browser manifest/helper describing:
  - repo-local C# source lists
  - external DLL references needed for browser compilation
  - startup assembly/program choice
- `carbide-pwsh/index.html` becomes a thin branded host around that manifest

This is important both for maintainability and for keeping the full executable catalog actually present.

## Workstream 3: browser-side dependency loading for the full executable catalog

This is now allowed by the task, so it is no longer a design objection. It is still real implementation work.

The shared runtime currently depends on code that is not covered by the repo-local source lists alone:

- `CarbideMultishell.csproj` includes `SharpCompress`
- the advanced virtual executable handler uses `SharpCompress.Compressors.BZip2`

So the browser composition layer must explicitly support that dependency if the full catalog is expected to work from `carbide-pwsh`.

That likely means one of:

- fetch/build the required DLL and register it with `session.addReference(...)`
- shift browser compilation to a built-artifact flow that already resolves package references

Because the task explicitly says this browser-side loading is acceptable, the right conclusion is:

- this is required work
- it is not a blocker to choosing the one-endpoint design

## Workstream 4: preserve or intentionally drop pwsh top-level prompt-editor behavior

This is the most important design decision still left open.

If the top-level `carbide-pwsh` route becomes just a thin wrapper around the current multishell outer runner, then the current pwsh prompt-editor experience likely disappears from the first prompt the user sees.

That is not catastrophic, but it is a real regression against the currently documented endpoint behavior.

So the implementation should make an explicit decision:

- preserve pwsh top-level editor behavior
- or deliberately simplify the top-level UX and document that change

Because this is now the only public endpoint, I think preserving the pwsh-first UX is worth the extra code.

## Workstream 5: pwsh-side discovery and completion of shared executables

This work becomes more important in the one-endpoint design than it was in the three-endpoint design.

Why:

- all users now enter through pwsh
- so pwsh is the discovery surface for the whole environment

Current pwsh discovery gaps:

- `ShellHost.GetInteractiveCommandNames()` does not enumerate virtual executables
- `Get-Command` does not surface virtual executables from the shared registry

That means the endpoint could become functionally correct while still feeling incomplete from the user's point of view.

This is not required for the first scope cut if the goal is only \"make it runnable,\" but it is the first polish step I would queue behind the route unification itself.

## Workstream 6: retire or de-emphasize the public `carbide-multishell` route

If `carbide-pwsh` becomes the only public browser shell endpoint, the repo should stop presenting `carbide-multishell` as a peer public destination.

Possible outcomes:

- remove the `carbide-multishell` page entirely
- keep it only as an internal/demo/dev route not linked from public docs
- keep the code/package but make the docs clearly state that `carbide-pwsh` is the supported entrypoint

This is mostly docs and route hygiene, not deep runtime work.

## Scope tiers

## Tier 1: single public route, generic shared runner

Deliverables:

- `carbide-pwsh` browser route boots the shared session
- `carbide-multishell` is no longer a public browser endpoint
- browser dependency loading for the full catalog works
- cmd/bash are entered from inside pwsh

Indicative artifact count:

- 1 browser page rewrite
- 1 shared source/dependency manifest/helper
- 1 smoke rewrite
- 2-3 docs/index updates

Risk:

- likely top-level pwsh UX regression

## Tier 2: single public route, pwsh-branded shared runner

Adds:

- 1 pwsh-public outer runner over `MultishellSession`
- top-level pwsh editor preservation
- dedicated tests/smoke assertions for that top-level UX

Indicative added artifacts beyond Tier 1:

- 1 new runner or runner abstraction
- 1-3 test/smoke updates

This is the tier I recommend.

## Tier 3: internal refactor / rename after route unification

Adds:

- optional movement or renaming of the internal `carbide-multishell` implementation pieces
- possible project/package graph cleanup
- possible extraction of shared session/runner code to reduce naming mismatch

This is explicitly not needed to satisfy the user-facing goal.

## Lurking issues

## 1. Public naming vs internal naming mismatch

If `carbide-pwsh` becomes the public shell entrypoint but still boots `MultishellSession` internally, that is fine technically but a bit awkward semantically. It is acceptable as an implementation detail, but the docs should be careful not to present `carbide-multishell` as the user-facing product at the same time.

## 2. Prompt-editor regression risk

This is the main user-visible risk. It is very easy to remove the current pwsh-first editing behavior accidentally by reusing the current shared runner too literally.

## 3. Browser manifest drift

This issue remains real. Even with only one public route, hand-maintained HTML source arrays will keep drifting unless replaced with a shared manifest/helper layer.

## 4. Full catalog dependency loading is required, not optional

The task accepts browser-side dependency loading, but the implementation still has to do it correctly. Otherwise the public endpoint will advertise more capability than it can compile.

## 5. pwsh command precedence will still surprise some users

Even after the one-endpoint merge, pwsh will still prefer its own command model for some bare names:

- `sort` -> `Sort-Object`
- `where` -> `Where-Object`
- `fc` -> `Format-Custom`

That is normal for pwsh, but because it is now the only public entrypoint, it becomes more important to document or surface explicit paths/aliases for the external-tool variants where needed.

## 6. Local `dotnet run` behavior may still diverge from browser behavior

If we only unify the browser route, the standalone `dotnet run` for `carbide-pwsh` can remain pwsh-only. That is acceptable if stated explicitly, but it should not be left ambiguous.

## Recommendation

This one-public-endpoint design is the better near-term direction.

Compared to the earlier three-endpoint plan, it:

- removes two public browser routes
- reduces browser packaging work substantially
- matches the actual pwsh-first product story better
- avoids investing in public cmd/bash page shells that add little value

The implementation I would recommend is:

1. Keep `carbide-pwsh` as the only public browser shell endpoint.
2. Retire `carbide-multishell` as a separate public route.
3. Make `carbide-pwsh` boot a shared session containing pwsh, cmd, bash, and the full executable catalog.
4. Preserve pwsh-first top-level UX by using a pwsh-branded shared-session runner rather than reusing the current generic multishell runner verbatim.
5. After that lands, improve pwsh-side discovery/completion for the shared executable catalog.

## Bottom line

With browser-side dependency loading explicitly allowed, the one-public-endpoint plan is not just simpler than the three-endpoint plan; it is simpler in exactly the right way. It removes packaging and public-surface complexity without removing any of the shell functionality the user actually wants.

The main remaining cost is concentrated in one place:

- making `carbide-pwsh` host the shared runtime without sacrificing the good parts of the current pwsh experience

That is a focused, worthwhile piece of work. It is much more attractive than standing up and maintaining three public browser routes for what is fundamentally one shared shell environment.
