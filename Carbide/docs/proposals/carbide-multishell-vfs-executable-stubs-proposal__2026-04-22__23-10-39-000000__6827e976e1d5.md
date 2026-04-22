# Proposal: virtual executable stubs for common `System32` and Git `usr/bin` tools in `carbide-multishell`

- Created (UTC): 2026-04-22T23:10:39Z
- Repository HEAD: b2dfbc1c772a37400b616ffe645ab508a54958df
- Status: Proposal (draft, pre-implementation)
- Audience: Vladimir; future Carbide contributors
- Scope: VFS stub population, command-resolution rules, and runtime feature commitments for non-shell executables commonly used from pwsh, cmd, and bash scripts inside `packages/carbide-multishell`
- Related code:
  - `src/Carbide/packages/carbide-multishell/src/MultishellSession.cs`
  - `src/Carbide/packages/carbide-shell-core/src/Apps/StubInstaller.cs`
  - `src/Carbide/packages/carbide-shell-core/src/Dispatch/ShellDispatcher.cs`
  - `src/Carbide/packages/carbide-bash/src/Builtins/Builtins.cs`
  - `src/Carbide/packages/carbide-cmd/src/Builtins/Builtins.cs`
  - `src/Carbide/packages/carbide-pwsh/src/Cmdlets/Pipeline.cs`
- Related docs:
  - [Multi-shell (cmd + bash alongside pwsh) with cross-shell invocation](carbide-multi-shell-proposal__2026-04-21__23-30-00-000000__d9a71f3c5b68.md)
  - [PowerShell-subset shell for Carbide + xterm.js](carbide-pwsh-subset-shell-proposal__2026-04-21__21-30-00-000000__e9c4b27a8f13.md)

## Summary

`carbide-multishell` currently materializes shell stubs only: `pwsh`, `cmd`, and `bash` can be entered by name or by path, but ordinary script-friendly executables such as `robocopy.exe`, `grep.exe`, `sed.exe`, `awk.exe`, `findstr.exe`, `tar.exe`, and `where.exe` do not exist in the VFS and therefore cannot participate in realistic cross-shell automation. This proposal extends the shell-only stub model into a broader **virtual executable catalog**: a curated set of utility stubs under `/Windows/System32`, `/usr/bin`, `/bin`, and a Git-for-Windows mirror path, all backed by `carbide-multishell` handlers that operate against the shared VFS, shared env store, and shared current directory.

The important point is not merely to drop more zero-byte files into the VFS. We also need a stable execution contract: how bare command names search PATH-like roots, how `.exe` and `.com` suffixes are inferred, how pwsh treats external-tool pipeline stages, how Windows and GNU command-name collisions (`find.exe`, `sort.exe`, `tar.exe`) are disambiguated, and which flags each command family must support. This document defines that contract and gives a complete file catalog for the first serious multishell utility surface.

## Scope and non-goals

### In scope

- Populating the VFS with stub executables for a curated set of utility commands commonly used in pwsh, cmd, and bash scripts.
- Defining the runtime registry that maps a stub path or a bare command name to a `carbide-multishell` handler.
- Defining search-path behavior, extension inference, and shell-specific command-precedence rules.
- Defining the minimum useful feature subset for each supported command family.
- Reusing existing cmd/bash built-in implementations where that produces good semantics quickly.

### Out of scope

- Spawning real host processes.
- Trying to reproduce every executable that ships in `C:\Windows\System32` or `C:\Program Files\Git\usr\bin`.
- Full GNU compatibility for `awk`, `sed`, `find`, `tar`, `patch`, or archive/compression tools.
- OS-administration commands whose semantics depend on services, processes, registry, networking, ACLs, drivers, devices, SMB shares, or the real host filesystem.
- Full-screen interactive tools such as `less`, `vim`, `nano`, `tig`, or `edit`.
- VFS features we do not currently model, such as NTFS ACLs, DOS hidden/system attributes, alternate data streams, hard links, symlinks, executable permission bits, block devices, or FIFOs.

## Why the current design is insufficient

The current multishell design solves only one class of cross-shell invocation: entering another shell or executing a script file whose extension names a shell kernel. That is enough for `pwsh`, `cmd`, and `bash`, but not for the utility-heavy scripts people actually write.

Today:

- `carbide-shell-core`'s `StubInstaller` registers shell stub paths only.
- `ShellDispatcher.Resolve(...)` knows about shell aliases, file extensions, and Carbide apps, but not a catalog of executable tools.
- `carbide-cmd` and `carbide-bash` fall back to the dispatcher only after checking built-ins. They can therefore reach shell aliases and scripts, but not a general-purpose utility executable surface.
- `carbide-pwsh` is stricter still: after cmdlets, functions, dot-sourcing, call-operator path dispatch, and app lookup, it throws "term not recognized." It has no native-command phase at all.

That leaves large holes:

- A pwsh script cannot call `grep`, `sed`, `awk`, `robocopy.exe`, `findstr`, or `tar`.
- A bash script cannot call `where.exe` or `robocopy.exe` unless those commands become first-class executable entries.
- A cmd script cannot call `awk.exe` or `sed.exe`.
- Path-qualified invocations that are common on Windows (`C:\Windows\System32\where.exe`, `C:\Program Files\Git\usr\bin\awk.exe`) have no VFS equivalents.

The local machine inventory from 2026-04-22 also surfaced an important semantic hazard: several names exist in both Windows and GNU worlds but mean different things. `find.exe`, `sort.exe`, and `tar.exe` are the big ones. So the fix cannot be "register tools by basename only." It has to be path- and personality-aware.

## Proposed architecture

### 1. Add a virtual executable registry to `carbide-shell-core`

`carbide-shell-core` should gain a registry alongside the existing shell registry:

- `VirtualExecutableDefinition`
  - `CommandId`
  - `Personality` (`windows`, `gnu`, or `shell`)
  - `StubPaths`
  - `SearchNames`
  - `ImplicitExtensions`
  - `HandlerKey`
- `VirtualExecutableRegistry`
  - register definitions
  - resolve by exact VFS path
  - resolve by bare name + shell personality
- `ResolutionKind.VirtualExecutable`
  - new dispatcher result kind next to `NamedShell`, `Script`, and `App`

The shell packages should not own the actual utility implementations. The handlers should live in `packages/carbide-multishell/src/Commands/`, and `carbide-multishell` should register them into the shared registry at session construction time.

The registry belongs in `carbide-shell-core` because name/path resolution is shared infrastructure. The command handlers belong in `carbide-multishell` because this proposal is intentionally about a multishell runtime catalog, not about bloating each shell package with a second independent copy of the same utility set.

### 2. Generalize stub installation from “shell only” to “virtual executable”

The current `StubInstaller` writes a small banner file and separately updates the shell dispatcher’s stub-path map. That pattern is still good, but it should be generalized:

- Shell stubs continue to exist and continue to resolve to interactive shell entry.
- Utility stubs use the same on-disk shape but register into the virtual executable registry instead of the shell-name table.
- Stub content should remain human-readable and self-describing. A banner like `#!carbide:exe:gnu-grep` is sufficient; the registry remains the authoritative resolver.

The VFS file content is not the behavior. The registry is the behavior. The file is there so:

- `ls /usr/bin`, `dir /b /Windows/System32`, and `Get-ChildItem` show a realistic tool surface;
- path-qualified invocations work;
- snapshots preserve the tool catalog naturally.

### 3. Install roots and path mirrors

We need four logical roots:

- `/usr/bin`
  - canonical Unix-style root exposed to bash and to cross-shell path invocations.
- `/bin`
  - compatibility mirror for scripts that hardcode `/bin/...`.
- `/Program Files/Git/usr/bin`
  - Windows-style mirror of the Git-for-Windows location for scripts that invoke tools through an explicit `C:\Program Files\Git\usr\bin\...` path.
- `/Windows/System32`
  - canonical Windows tool root.

Shell-specific special cases remain:

- `/Windows/System32/WindowsPowerShell/v1.0/powershell.exe`
- `/usr/bin/pwsh.exe`
- `/usr/bin/bash.exe`
- `/usr/bin/sh.exe`
- corresponding `/bin/...` and Git-mirror aliases where appropriate.

For the POSIX catalog we should install the same basenames in all three POSIX/Git roots:

- `/usr/bin/<name>`
- `/bin/<name>`
- `/Program Files/Git/usr/bin/<name>`

For the Windows catalog we install:

- `/Windows/System32/<name>`

That is enough to support both Unix-style and Windows-style explicit paths without inventing additional roots.

### 4. Command resolution order

The registry only helps if shells consult it in the right place.

#### pwsh

PowerShell should resolve in this order:

1. cmdlet
2. function
3. dot-sourced script / call-operator target
4. script by path
5. Carbide app
6. virtual executable by bare name or by path
7. error

This matches PowerShell’s broad “commands first, then native tools” mental model closely enough for the supported subset.

#### bash

Bash should resolve in this order:

1. alias
2. function
3. builtin
4. explicit shell alias (`bash`, `sh`, `pwsh`, `cmd`) if present
5. virtual executable by bare name or by path
6. shell script or Carbide app
7. `command not found`

This preserves the normal “builtin beats external command” rule that scripts rely on (`printf`, `echo`, `test`, `pwd`).

#### cmd

cmd should resolve in this order:

1. internal built-in
2. explicit shell alias (`cmd`, `pwsh`, `bash`)
3. virtual executable by bare name or by path
4. batch script or Carbide app
5. classic “not recognized as an internal or external command”

This keeps cmd built-ins authoritative while allowing realistic external-tool usage (`findstr`, `where`, `robocopy`, `awk.exe`).

### 5. PATH and extension behavior

One shared `EnvVarStore` cannot have three different default PATH orderings at once. The least surprising approach is:

- If `PATH` is explicitly set in the shared environment, all shells honor it.
- If `PATH` is absent, each shell uses its own built-in default search roots.

Default roots when `PATH` is absent:

- bash: `/usr/bin`, `/bin`, `/Windows/System32`
- cmd: `/Windows/System32`, `/usr/bin`, `/bin`
- pwsh: `/Windows/System32`, `/Windows/System32/WindowsPowerShell/v1.0`, `/usr/bin`, `/bin`

This intentionally gives bash the GNU personality first, and cmd/pwsh the Windows personality first, for colliding names such as `find`, `sort`, and `tar`.

Extension behavior:

- pwsh/cmd: if the user typed `grep`, search `grep`, then `grep.com`, `grep.exe`, `grep.cmd`, `grep.bat` using a `PATHEXT`-style list. The default synthetic `PATHEXT` should be `.COM;.EXE;.CMD;.BAT` if the environment variable is absent.
- bash: search exact name first, then try `.exe` and `.com` as compatibility suffixes.

This means we do **not** need duplicate `grep` and `grep.exe` files in the VFS merely to satisfy shell lookup. One `grep.exe` stub plus the shell’s search rules is enough.

### 6. Runtime contract for virtual executables

Virtual executables are not real child processes. They are handler invocations with process-like semantics:

- Input: text stdin
- Output: text stdout and stderr
- Exit code: integer
- Working directory: shared VFS current location
- Environment: shared `EnvVarStore`
- Filesystem: shared VFS only

The handler contract should therefore be:

```csharp
public interface IVirtualExecutableHandler
{
    int Execute(VirtualExecutableInvocation invocation);
}
```

with `VirtualExecutableInvocation` carrying:

- resolved stub path
- command id
- personality
- argv
- stdin/stdout/stderr
- VFS
- env
- dispatcher

### 7. pwsh pipeline behavior for external tools

This needs to be explicit because it is the one place where pwsh differs materially from cmd/bash.

For a pwsh pipeline stage targeting a virtual executable:

- upstream PowerShell objects are stringified with an `Out-String -Stream`-style bridge;
- the external tool consumes line-oriented text;
- stdout re-enters the pwsh pipeline as strings;
- stderr remains text-only and writes to the terminal/error stream;
- `$LASTEXITCODE` is updated from the handler result;
- `$?` becomes `$true` only when the exit code is zero.

This is intentionally a text bridge, not an object bridge. The utility surface proposed here is for `grep`, `sed`, `awk`, `findstr`, `robocopy`, `tar`, and similar tools, not for preserving .NET object identity through native-looking command stages.

## Stub catalog

## 1. Shell alias completion

The current shell catalog is almost correct but not quite complete from a Windows/Git point of view. The same proposal should finish it.

| Family | Basenames | Install roots | Notes |
|---|---|---|---|
| pwsh | `pwsh`, `pwsh.exe`, `powershell`, `powershell.exe` | `/usr/bin`, `/bin`; keep `/Windows/System32/WindowsPowerShell/v1.0/powershell.exe` | Existing surface; no change in behavior. |
| cmd | `cmd`, `cmd.exe` | `/usr/bin`, `/bin`, `/Windows/System32` | Existing surface; keep both Unix-style and Windows-style paths. |
| bash | `bash`, `bash.exe`, `sh`, `sh.exe` | `/usr/bin`, `/bin`, `/Program Files/Git/usr/bin` | `bash.exe` and `sh.exe` should be added explicitly; today only extensionless `bash`/`sh` are materialized. |

## 2. POSIX/Git utility catalog

For every basename below, materialize the same stub file under all of:

- `/usr/bin`
- `/bin`
- `/Program Files/Git/usr/bin`

These are GNU/MSYS-style personalities unless noted otherwise.

| Family | Basenames | Minimum feature subset | Reuse / net-new |
|---|---|---|---|
| awk | `awk.exe`, `gawk.exe` | `-F`, `-v`, inline program or `-f`; `BEGIN`/`END`; regex and truthy-pattern selection; `$0`, `$1..$NF`, `NR`, `FNR`, `NF`, `FS`, `OFS`, `ORS`; `print`, `printf`, assignments, `if`, `for`, `while`, `next`, `exit`; string helpers (`split`, `sub`, `gsub`, `match`, `length`, `tolower`, `toupper`) | Net-new, large surface |
| sed | `sed.exe` | `-e`, `-f`, `-n`; line addresses and ranges; `s`, `d`, `p`, `q`, `a`, `i`, `c`; file/stdin input; stdout rewrite | Net-new, medium surface |
| grep | `grep.exe`, `egrep`, `fgrep` | fixed-string and regex search; `-i`, `-v`, `-n`, `-c`, `-l`, `-r/-R`, `-e`, file args, stdin; `egrep` and `fgrep` are mode aliases, not separate implementations | Mostly extractable from existing bash `grep` plus flag growth |
| cut/paste | `cut.exe`, `paste.exe` | `cut`: `-b`, `-c`, `-d`, `-f`; `paste`: `-d`, `-s`; stdin/file args | Net-new, modest |
| head/tail/wc | `head.exe`, `tail.exe`, `wc.exe` | `head`: `-n`, `-c`; `tail`: `-n`, `-c`; `wc`: `-l`, `-w`, `-c`, `-m`, `-L`; stdin/file args | Mostly extractable from existing bash built-ins |
| sort/uniq/comm | `sort.exe`, `uniq.exe`, `comm.exe` | GNU `sort`: `-r`, `-n`, `-u`, `-t`, `-k`; `uniq`: `-c`, `-d`, `-u`; `comm`: column suppression `-1/-2/-3` for sorted inputs | `sort`/`uniq` largely reusable; `comm` new |
| tr | `tr.exe` | translate, delete, squeeze; `-d`, `-s`, `-c`; stdin-only | Existing bash `tr` plus flag growth |
| tee | `tee.exe` | write stdin to stdout and one or more files; `-a` append | Net-new, modest |
| xargs | `xargs.exe` | whitespace mode, `-0`, `-n`, `-I`, `-d`; invokes another Carbide virtual executable or script command repeatedly | Net-new, medium |
| seq | `seq.exe` | 1-arg, 2-arg, 3-arg forms; `-f`, `-s`, `-w` | Net-new, modest |
| path/context | `basename.exe`, `dirname.exe`, `pwd.exe`, `readlink.exe`, `realpath.exe`, `which.exe` | `basename`: `-a`, `-s`; `dirname`: multiple args; `pwd`: current VFS location; `readlink`: `-f`, `-e`, `-m`; `realpath`: `-e`, `-m`, `--relative-to`; `which`: PATH lookup, `-a` | Mostly net-new; `pwd` can reuse existing builtin |
| env/context | `env.exe`, `printenv.exe`, `hostname.exe`, `uname.exe`, `whoami.exe`, `yes.exe`, `test.exe`, `date.exe`, `sleep.exe`, `mktemp.exe` | `env`: list or set vars then run command; `printenv`: list names/values; `hostname`: synthetic host name; `uname`: `-s`, `-r`, `-m`, `-a`; `whoami`: bare output; `yes`: repeated line; `test`: file/string/integer predicates; `date`: formatting and `-u`, `-I`, `-d`; `sleep`: GNU duration parsing; `mktemp`: file/dir creation, `-d` | Mixed; several are trivial handlers |
| file/tree ops | `ls.exe`, `cat.exe`, `cp.exe`, `mv.exe`, `rm.exe`, `rmdir.exe`, `mkdir.exe`, `touch.exe`, `printf.exe` | `ls`: `-1`, `-l`, `-a`, `-R`; `cat`: stdin/files; `cp`: file and recursive copy `-r/-R`; `mv`: rename/move; `rm`: `-f`, `-r/-R`; `rmdir`: remove empty dirs; `mkdir`: `-p`; `touch`: create/update timestamp; `printf`: `%s`, `%d`, `%x`, escapes | Strong reuse from existing bash built-ins |
| compare/patch | `cmp.exe`, `diff.exe`, `diff3.exe`, `patch.exe` | `cmp`: exit-code comparison and first-difference reporting; `diff`: unified/context/brief; `diff3`: three-way merge report sufficient for scripted conflict detection; `patch`: unified/context application with `-p`, `-R`, `--dry-run` | Net-new, medium/large |
| find | `find.exe` | traversal over the VFS with `-name`, `-iname`, `-path`, `-type`, `-maxdepth`, `-mindepth`, `-print`, `-print0`, `-exec ... ;`; no device/ACL/filesystem-class predicates | Net-new, large surface |
| archive/compression | `tar.exe`, `unzip.exe`, `gzip.exe`, `gunzip`, `bzip2.exe`, `bunzip2.exe` | `tar`: create/list/extract with `-c`, `-x`, `-t`, `-f`, `-v`, `-z`; `unzip`: list/extract; gzip/bzip2 pairs: compress/decompress file or stdin/stdout | Net-new, medium/large |

## 3. Windows `System32` utility catalog

For every basename below, materialize the stub under:

- `/Windows/System32`

These are Windows personalities, even when the same basename also exists in the GNU catalog.

| Family | Basenames | Minimum feature subset | Notes |
|---|---|---|---|
| Windows find | `find.exe` | literal substring search in stdin or files; `/I`, `/V`, `/C`, `/N`; Windows-style exit codes | Distinct from GNU `find.exe`; path root determines personality |
| findstr | `findstr.exe` | `/I`, `/S`, `/N`, `/M`, `/V`, `/R`, `/L`, `/C:`; file args and stdin | Reuse possible from existing cmd `FINDSTR` plus regex/file-recursion work |
| Windows sort | `sort.exe` | `/R`, `/+n`, file args, stdin | Distinct from GNU `sort.exe`; path root determines option parser |
| more | `more.com` | file or piped input; paging behavior can initially degrade to pass-through plus `+n` start-line support | xterm scrollback means a full pager is not mandatory for v1 |
| where | `where.exe` | PATH search, `/R`, `/Q`, `/F`, `/T`; reports VFS paths | Net-new, modest |
| whoami | `whoami.exe` | bare output plus minimal `/USER` and `/GROUPS` reporting using synthetic identity data | Can share core identity provider with GNU `whoami.exe` |
| robocopy | `robocopy.exe` | source/destination/file-pattern copy; `/S`, `/E`, `/XF`, `/XD`, `/XO`, `/MIR`, `/L`, `/R:n`, `/W:n`, `/NFL`, `/NDL`, `/NJH`, `/NJS`; VFS-only, no ACL or SMB semantics | Net-new, large surface; high-value Windows automation tool |
| timeout | `timeout.exe` | `/T n`, `/NOBREAK`; can map to `Task.Delay`/timer without blocking host event pump incorrectly | Net-new, trivial |
| fc | `fc.exe` | line and binary compare sufficient for scripted equality/difference checks; `/B`, `/N`, `/L`, `/W` | Net-new, moderate |
| xcopy | `xcopy.exe` | `/S`, `/E`, `/I`, `/Y`, `/Q`, `/L`; implemented as a Windows-flavored wrapper over the same tree-copy engine as `robocopy.exe` | Useful legacy batch surface |
| tree | `tree.com` | directory tree rendering; `/F`, `/A` | Net-new, modest |
| Windows tar | `tar.exe` | create/list/extract over VFS with a bsdtar-compatible subset; accept Windows path spelling and common `-c/-x/-t/-f/-v` operations | Same basename as GNU `tar.exe`; path root determines personality |

## 4. Name collisions and personalities

These collisions are the main reason the catalog must be path-aware:

| Basename | GNU personality | Windows personality | Default winner by shell when `PATH` is unset |
|---|---|---|---|
| `find` / `find.exe` | directory traversal expression language | file-content substring search | bash → GNU, cmd/pwsh → Windows |
| `sort` / `sort.exe` | GNU option set (`-n`, `-u`, `-t`, `-k`, `-r`) | Windows option set (`/R`, `/+n`, `/M`, `/L`) | bash → GNU, cmd/pwsh → Windows |
| `tar` / `tar.exe` | GNU tar behavior | bsdtar/Windows tar behavior | bash → GNU, cmd/pwsh → Windows |
| `whoami.exe` | effectively same outcome | effectively same outcome | either personality may share one handler |

The correct design is:

- same basename may map to different handlers depending on the resolved stub path;
- shell defaults choose a root order only when `PATH` is absent;
- explicit path always wins over personality defaults.

## 5. Commands deliberately excluded from the catalog

The catalog should stay sharply scoped. These commands should **not** be stubbed in the first serious version:

- process/service/system management: `tasklist.exe`, `taskkill.exe`, `sc.exe`, `net.exe`, `reg.exe`, `wmic.exe`, `sudo.exe`, `wsl.exe`
- network transfer and remote session tools: `curl.exe`, `wget`, `ftp.exe`, `ssh.exe`, `scp.exe`, `sftp.exe`
- filesystem features the VFS does not model well: `chmod.exe`, `chown.exe`, `icacls.exe`, `attrib.exe`, `ln.exe`, `mkfifo.exe`, `mount.exe`
- full-screen editors and pagers: `less.exe`, `vim.exe`, `nano.exe`, `edit.exe`
- package/source-control tools: `git.exe`, `winget.exe`, `choco.exe`, `npm`, `pip`

Some tools are reasonable but should remain deferred until the basic catalog lands cleanly:

- `stat.exe`, `du.exe`, `install.exe`, `unlink.exe`, `zipinfo.exe`, `zipgrep`, `choice.exe`, `forfiles.exe`

These are not bad ideas; they are just lower priority than the catalog above.

## Implementation strategy

## 1. Keep handlers in `carbide-multishell`, keep registry types in `carbide-shell-core`

That split avoids circular ownership:

- `carbide-shell-core` owns the neutral resolution machinery.
- `carbide-multishell` owns the concrete utility surface.

## 2. Extract reusable command engines instead of re-entering bash/cmd built-ins indirectly

Many useful tools already exist as shell built-ins, but wrapping them by instantiating a bash or cmd interpreter would create semantic drift and weird error behavior. The better pattern is:

- extract reusable pure helpers for file-tree operations, line filters, text transforms, path canonicalization, and env/context reporting;
- let existing shell built-ins call those helpers;
- let virtual executable handlers call the same helpers with GNU or Windows option parsing as needed.

Good immediate extraction candidates:

- `ls`, `cat`, `cp`, `mv`, `rm`, `mkdir`, `rmdir`, `touch`, `printf`
- `grep`, `head`, `tail`, `wc`, `tr`, `sort`, `uniq`
- cmd `FIND`, `FINDSTR`, `SORT`, `MORE`

Net-new engines still needed:

- `awk`
- `sed`
- GNU `find`
- `robocopy`/`xcopy`
- archive/compression
- `diff`/`diff3`/`patch`

## 3. Phase the work by command-family complexity, not by path root

Recommended implementation order:

1. Registry + resolution + shell alias completion
2. low-risk reusable tools
   - file/tree ops
   - env/path/context helpers
   - head/tail/wc/tr/sort/uniq/grep
   - Windows `find`, `findstr`, `where`, `timeout`, `tree`
3. medium tools
   - cut/paste/tee/seq/xargs
   - cmp/comm/fc
   - Windows `more`
   - `readlink`/`realpath`/`which`
4. large tools
   - `awk`
   - `sed`
   - GNU `find`
   - `robocopy` and `xcopy`
   - `tar`, `unzip`, `gzip`, `bzip2`
   - `diff`, `diff3`, `patch`

This keeps the first useful slice broad without pretending that `awk` and `robocopy` are the same size class as `basename.exe`.

## Testing requirements

The catalog is large enough that tests need to be part of the design, not an afterthought.

Required test layers:

- stub-presence tests
  - exact path assertions for all roots
  - shell alias completion checks (`bash.exe`, `sh.exe`, Git mirror paths)
- resolution tests
  - bare-name lookup in each shell
  - path-qualified lookup
  - collision tests for `find`, `sort`, `tar`
  - `PATH` override tests
- pwsh bridge tests
  - object input to external tool becomes text
  - stdout lines become strings
  - stderr and `$LASTEXITCODE` behave correctly
- command-family fixture tests
  - representative happy-path scripts
  - failure and exit-code behavior
  - recursion/path edge cases
- cross-shell integration tests
  - cmd invoking GNU tools
  - bash invoking Windows tools
  - pwsh piping into GNU and Windows tools

## Risks and edge cases

| Risk | Why it matters | Mitigation |
|---|---|---|
| Windows/GNU name collisions produce surprising behavior | `find`, `sort`, and `tar` mean different things | path-aware registry plus shell-specific default roots only when `PATH` is absent |
| `awk` and `sed` expand into mini-language projects | they are much larger than simple filters | explicit subset contract; do not promise full gawk/GNU sed parity |
| pwsh external-tool bridging becomes too magical | object vs text semantics can get muddy | keep it text-only and document that clearly |
| Duplicate logic between shell built-ins and executable handlers drifts | same command behaves differently by invocation style | extract shared helpers and make both layers call them |
| Archive and patch tools quietly need metadata the VFS does not have | permissions, symlinks, owners, mtimes, device nodes | support data/path-centric subsets only; reject metadata-heavy options explicitly |
| `robocopy.exe` scope balloons toward full Windows filesystem semantics | ACLs, retries, shares, attributes, timestamps are deep | implement a script-useful subset focused on VFS tree copy and filtering; reject unsupported switches loudly |

## Recommendation

We should implement this as a **cataloged virtual executable surface**, not as a bag of special cases. The registry, stub installer, resolution rules, and path/personality model are the real design. Once those exist, the command families above can land incrementally without each one inventing its own dispatch path.

The first catalog should be opinionated and broad:

- include the high-value GNU text and file utilities that bash scripts expect;
- include the Windows utilities that cmd and pwsh scripts actually reach for (`robocopy.exe`, `findstr.exe`, `where.exe`, `timeout.exe`);
- explicitly support the Windows/GNU collisions rather than pretending they do not exist;
- finish the shell alias surface by adding `bash.exe` and `sh.exe`.

That gives Carbide a realistic “mixed shell toolbox” story: not just three shells that can call one another, but three shells that can share the same familiar utility layer while remaining inside the VFS sandbox.

## Appendix A: complete POSIX/Git basenames

Install each of these basenames in:

- `/usr/bin`
- `/bin`
- `/Program Files/Git/usr/bin`

```text
awk.exe
basename.exe
bunzip2.exe
bzip2.exe
cat.exe
cmp.exe
comm.exe
cp.exe
cut.exe
date.exe
diff.exe
diff3.exe
dirname.exe
egrep
env.exe
fgrep
find.exe
gawk.exe
grep.exe
gunzip
gzip.exe
head.exe
hostname.exe
ls.exe
mkdir.exe
mktemp.exe
mv.exe
paste.exe
patch.exe
printenv.exe
printf.exe
pwd.exe
readlink.exe
realpath.exe
rm.exe
rmdir.exe
sed.exe
seq.exe
sleep.exe
sort.exe
tail.exe
tar.exe
tee.exe
test.exe
touch.exe
tr.exe
uname.exe
uniq.exe
unzip.exe
wc.exe
which.exe
whoami.exe
xargs.exe
yes.exe
```

## Appendix B: complete Windows basenames

Install each of these basenames in:

- `/Windows/System32`

```text
fc.exe
find.exe
findstr.exe
more.com
robocopy.exe
sort.exe
tar.exe
timeout.exe
tree.com
where.exe
whoami.exe
xcopy.exe
```

## Appendix C: shell alias completion basenames

These are not the main point of this proposal, but they should be completed in the same work.

```text
pwsh
pwsh.exe
powershell
powershell.exe
cmd
cmd.exe
bash
bash.exe
sh
sh.exe
```
