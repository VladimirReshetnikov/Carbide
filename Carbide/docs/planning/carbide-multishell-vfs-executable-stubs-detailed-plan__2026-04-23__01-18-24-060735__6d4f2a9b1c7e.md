# Detailed implementation plan: `carbide-multishell` virtual executable stubs

- Created (UTC): 2026-04-23T01:18:24Z
- Repository HEAD: c1e734191d4326d3f6501084f30cd30226374804
- Status: Implemented
- Implements proposal: [Virtual executable stubs for common `System32` and Git `usr/bin` tools in `carbide-multishell`](../proposals/carbide-multishell-vfs-executable-stubs-proposal__2026-04-22__23-10-39-000000__6827e976e1d5.md)
- Related evaluation: [Content-identified virtual executable stubs for `carbide-multishell`](../reports/carbide-multishell-content-identified-stubs-report__2026-04-23__00-08-04__f85c83692721.md)

## Summary

This plan turns the proposal into a concrete, path-based implementation for `carbide-multishell`. The resulting system keeps executable identity in the resolved VFS path, not in file content, and adds a shared virtual-executable registry, stub installation across Unix-style and Windows-style roots, shell-aware resolution rules, pwsh external-command bridging, and a catalog of GNU- and Windows-personality command handlers that run entirely against Carbide's shared VFS/env/session state.

The implementation is proposal-complete at the catalog level:

- shell alias completion for `pwsh`, `cmd`, `bash`, and `sh`, including missing `.exe` and Git-mirror aliases;
- POSIX/Git catalog installation under `/usr/bin`, `/bin`, and `/Program Files/Git/usr/bin`;
- Windows catalog installation under `/Windows/System32`;
- shell-specific default root order for collision-prone names such as `find`, `sort`, and `tar`;
- text-bridge execution for pwsh native-looking command stages;
- runtime support for the command families committed in the proposal, including `awk`, `sed`, GNU `find`, `robocopy`, `findstr`, `tar`, `gzip`, `gunzip`, `bzip2`, and `bunzip2`.

## Goals and acceptance criteria

The implementation had to satisfy all of the following:

- A stub file must exist in the VFS for every basename committed by the proposal appendices.
- Stub behavior must be owned by `carbide-multishell`, not by host-process execution.
- A bare command name must resolve differently by shell when `PATH` is absent and the basename is personality-sensitive.
- An explicit path must always win over shell-default personality.
- `pwsh` must be able to invoke virtual executables both directly and from pipelines, with `$LASTEXITCODE` semantics preserved.
- `cmd` and `bash` built-ins must remain authoritative where the proposal required builtin-before-external behavior.
- The catalog must remain path-based; file content is descriptive only.
- Focused unit/integration coverage must exist for stub presence, resolution, collisions, path-qualified invocation, and representative command execution.

## Workstream A: shell-core execution model

Status: completed

Deliverables:

- Add a shared virtual executable model to `carbide-shell-core`.
- Make path-qualified and bare-name resolution understand virtual executables as a first-class dispatch result.
- Keep shell stubs and virtual-executable stubs distinct while reusing the same VFS installation pattern.

Implementation:

- Add [`src/Carbide/packages/carbide-shell-core/src/Dispatch/VirtualExecutable.cs`](../../packages/carbide-shell-core/src/Dispatch/VirtualExecutable.cs) with:
  - `VirtualExecutablePersonality`
  - `VirtualExecutableDefinition`
  - `VirtualExecutableMatch`
  - `VirtualExecutableInvocation`
  - `IVirtualExecutableHandler`
  - `VirtualExecutableRegistry`
- Extend [`src/Carbide/packages/carbide-shell-core/src/Dispatch/ShellDispatcher.cs`](../../packages/carbide-shell-core/src/Dispatch/ShellDispatcher.cs) with:
  - `ResolutionKind.VirtualExecutable`
  - catalog registration and handler registration
  - shell-aware virtual-executable resolution
  - `ExecuteVirtualExecutable(...)`
  - path-like command handling that honors drive-qualified Windows spellings
- Extend [`src/Carbide/packages/carbide-shell-core/src/Apps/StubInstaller.cs`](../../packages/carbide-shell-core/src/Apps/StubInstaller.cs) so utility stubs can be registered just like shell stubs, with a descriptive banner payload of the form `#!carbide:exe:<commandId>`.
- Extend [`src/Carbide/packages/carbide-shell-core/src/Vfs/VfsPath.cs`](../../packages/carbide-shell-core/src/Vfs/VfsPath.cs) so `C:\...` and `C:/...` normalize into the VFS root layout (`/Windows/...`, `/work/...`, and so on).

Acceptance checks:

- `Resolve("grep", ..., "bash")` can return a virtual executable.
- `Resolve(@"C:\Windows\System32\where", ..., "cmd")` can resolve `/Windows/System32/where.exe`.
- `ExecuteVirtualExecutable(...)` updates dispatcher `LastExitCode`.

## Workstream B: shell integration and precedence

Status: completed

Deliverables:

- Finish the shell alias surface promised by the proposal.
- Ensure each shell consults the dispatcher at the right point.
- Preserve the intended precedence rules instead of letting the new catalog distort builtin semantics.

Implementation:

- `carbide-pwsh`
  - [`src/Carbide/packages/carbide-pwsh/src/Host/PwshKernel.cs`](../../packages/carbide-pwsh/src/Host/PwshKernel.cs) now recognizes `powershell`, `pwsh.exe`, and `powershell.exe`.
  - [`src/Carbide/packages/carbide-pwsh/src/Host/ShellHost.cs`](../../packages/carbide-pwsh/src/Host/ShellHost.cs) installs the missing `/bin` aliases and exposes a helper so dispatcher-driven script execution preserves script args/automatic vars.
  - [`src/Carbide/packages/carbide-pwsh/src/Cmdlets/Pipeline.cs`](../../packages/carbide-pwsh/src/Cmdlets/Pipeline.cs) now has a native-command phase that bridges object input to line-oriented text and converts stdout back into strings.
- `carbide-bash`
  - [`src/Carbide/packages/carbide-bash/src/Host/BashKernel.cs`](../../packages/carbide-bash/src/Host/BashKernel.cs) now recognizes `sh`, `bash.exe`, and `sh.exe`.
  - [`src/Carbide/packages/carbide-bash/src/Host/ShellHost.cs`](../../packages/carbide-bash/src/Host/ShellHost.cs) installs `/usr/bin`, `/bin`, and Git-mirror aliases for both extensionless and `.exe` forms.
  - [`src/Carbide/packages/carbide-bash/src/Runtime/Interpreter.cs`](../../packages/carbide-bash/src/Runtime/Interpreter.cs) now accepts `ResolutionKind.VirtualExecutable`.
- `carbide-cmd`
  - [`src/Carbide/packages/carbide-cmd/src/Host/ShellHost.cs`](../../packages/carbide-cmd/src/Host/ShellHost.cs) now installs `/Windows/System32/cmd`.
  - [`src/Carbide/packages/carbide-cmd/src/Runtime/Interpreter.cs`](../../packages/carbide-cmd/src/Runtime/Interpreter.cs) now accepts `ResolutionKind.VirtualExecutable`.

Precedence contract implemented:

- `pwsh`: cmdlet/function/script/app before virtual executable.
- `bash`: builtin before virtual executable.
- `cmd`: builtin before virtual executable.
- explicit path beats all shell-default personality ordering.

## Workstream C: catalog installation and session wiring

Status: completed

Deliverables:

- Materialize the proposal's executable surface into the VFS.
- Keep one authoritative registry of command ids, personalities, basenames, and stub paths.
- Install the catalog automatically when a multishell session starts.

Implementation:

- Add [`src/Carbide/packages/carbide-multishell/src/VirtualExecutableCatalog.cs`](../../packages/carbide-multishell/src/VirtualExecutableCatalog.cs).
- Install the catalog from [`src/Carbide/packages/carbide-multishell/src/MultishellSession.cs`](../../packages/carbide-multishell/src/MultishellSession.cs).
- Seed the shared environment with stable identity/context defaults:
  - `HOME=/home/user`
  - `USER=user`
  - `USERNAME=user`
  - `HOSTNAME=carbide`

Installed roots:

- POSIX/Git roots: `/usr/bin`, `/bin`, `/Program Files/Git/usr/bin`
- Windows root: `/Windows/System32`

Catalog shape:

- GNU/personality-backed tools are installed in all three POSIX/Git roots.
- Windows/personality-backed tools are installed in `/Windows/System32`.
- The catalog includes the full Appendix A/B surface from the proposal, including the Windows/GNU collisions.

## Workstream D: command-family implementation

Status: completed

Deliverables:

- Reuse existing shell engines where they already match the proposal well.
- Add direct handlers where a real utility engine is required.
- Preserve the proposal's path-based collision model all the way through execution.

Implementation files:

- [`src/Carbide/packages/carbide-multishell/src/MultishellVirtualExecutableHandler.Core.cs`](../../packages/carbide-multishell/src/MultishellVirtualExecutableHandler.Core.cs)
- [`src/Carbide/packages/carbide-multishell/src/MultishellVirtualExecutableHandler.Basic.cs`](../../packages/carbide-multishell/src/MultishellVirtualExecutableHandler.Basic.cs)
- [`src/Carbide/packages/carbide-multishell/src/MultishellVirtualExecutableHandler.Advanced.cs`](../../packages/carbide-multishell/src/MultishellVirtualExecutableHandler.Advanced.cs)

Implementation strategy:

- Reuse bash built-ins where the proposal explicitly favored that:
  - file/tree ops: `ls`, `cat`, `cp`, `mv`, `rm`, `rmdir`, `mkdir`, `touch`, `printf`, `test`, `pwd`
  - text filters: `grep`, `head`, `tail`, `wc`, `sort`, `uniq`, `tr`
- Reuse cmd built-ins for Windows-personality tools where that produces the right surface:
  - `find`
  - `findstr`
  - `sort`
  - `more`
- Implement direct multishell handlers for families that need their own engine:
  - context/path helpers: `basename`, `dirname`, `env`, `printenv`, `hostname`, `uname`, `whoami`, `which`, `where`, `readlink`, `realpath`, `seq`, `sleep`, `mktemp`, `date`, `yes`
  - file comparison and merge: `cmp`, `comm`, `diff`, `diff3`, `patch`, `fc`
  - Windows file-tree tools: `tree`, `timeout`, `xcopy`, `robocopy`
  - text transforms: `cut`, `paste`, `tee`
  - traversal and search: GNU `find`, Windows `findstr`
  - archive/compression: `tar`, `unzip`, `gzip`, `gunzip`, `bzip2`, `bunzip2`
  - higher-level argument dispatchers: `xargs`
  - mini-language tools: `sed`, `awk`

Supporting implementation work:

- `carbide-bash` built-ins were expanded to cover the proposal's minimum useful subset for `head`, `tail`, `wc`, `grep`, `sort`, `uniq`, and `tr`.
- `carbide-cmd` built-ins were expanded to cover the committed subset for `FIND`, `FINDSTR`, `SORT`, and `MORE`.
- `SharpCompress` was added to [`src/Carbide/packages/carbide-multishell/src/CarbideMultishell.csproj`](../../packages/carbide-multishell/src/CarbideMultishell.csproj) so `bzip2`/`bunzip2` are implemented rather than cataloged-but-unsupported.

Command-family acceptance checks:

- `cmd` can invoke GNU `grep`.
- `cmd` can run recursive `findstr`.
- `pwsh` can pipe strings into `grep`.
- `robocopy` copies VFS directory trees.
- `sed` transforms file/stdin text through the dispatcher.
- `awk` evaluates record fields and printing rules.
- `bzip2`/`bunzip2` round-trip VFS file content.

## Workstream E: testing and validation

Status: completed

Deliverables:

- Add shell-core tests for the new dispatch semantics.
- Add multishell tests for catalog presence, collisions, and representative command behavior.
- Re-run the dependent shell suites after the dispatcher and pwsh changes.

New/expanded coverage:

- [`src/Carbide/packages/carbide-shell-core/test/ShellDispatcherTests.cs`](../../packages/carbide-shell-core/test/ShellDispatcherTests.cs)
  - bare-name virtual executable resolution
  - handler execution + `LastExitCode`
  - drive-qualified Windows path resolution
- [`src/Carbide/packages/carbide-shell-core/test/VfsTests.cs`](../../packages/carbide-shell-core/test/VfsTests.cs)
  - drive-qualified normalization
- [`src/Carbide/packages/carbide-multishell-tests/VirtualExecutableTests.cs`](../../packages/carbide-multishell-tests/VirtualExecutableTests.cs)
  - stub presence across roots
  - collision resolution for `find`, `sort`, and `tar`
  - `PATH` override behavior
  - `cmd` invoking GNU `grep`
  - recursive `findstr`
  - `where` returning virtual paths
  - `robocopy`
  - `sed`
  - `awk`
  - `bzip2`/`bunzip2`
  - pwsh pipeline bridge

Validation run completed:

- `dotnet test src/Carbide/packages/carbide-shell-core/test/CarbideShellCore.Tests.csproj`
- `dotnet test src/Carbide/packages/carbide-bash/test/CarbideBash.Tests.csproj`
- `dotnet test src/Carbide/packages/carbide-cmd/test/CarbideCmd.Tests.csproj`
- `dotnet test src/Carbide/packages/carbide-pwsh/test/CarbidePwsh.Tests.csproj`
- `dotnet test src/Carbide/packages/carbide-multishell-tests/CarbideMultishell.Tests.csproj`

Observed result counts at completion:

- `CarbideShellCore.Tests`: 62 passed
- `CarbideBash.Tests`: 48 passed
- `CarbideCmd.Tests`: 48 passed
- `CarbidePwsh.Tests`: 230 passed
- `CarbideMultishell.Tests`: 35 passed

## Deliberate boundaries that remain after implementation

These are consistent with the proposal's non-goals, not missing deliverables:

- no host-process execution;
- no content-identified stub format;
- no attempt at full GNU or Windows compatibility beyond the committed feature subsets;
- no NTFS/ACL/service/network/process semantics outside the VFS/env/session model;
- no pager/editor/full-screen TUI support.

## Follow-up backlog after this plan

The proposal is implemented, but there is still reasonable expansion room:

- broaden option parity within already implemented families (`awk`, `sed`, `patch`, `tar`, `find`, `robocopy`);
- extract more helper engines out of bash/cmd built-ins to reduce long-term semantic drift;
- add lower-priority tools deferred by the proposal (`stat`, `du`, `install`, `zipinfo`, `choice`, `forfiles`);
- trim style/analyzer noise in the new multishell source files once the feature surface settles.

## Final status

This implementation plan is complete and executed. `carbide-multishell` now exposes a path-based virtual executable surface that matches the proposal's design direction and catalog commitments, and the behavior is validated across shell-core, bash, cmd, pwsh, and multishell integration tests.
