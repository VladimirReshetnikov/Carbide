# Proposal: cmd.exe and bash subsets for Carbide, with cross-shell invocation across pwsh / cmd / bash

- Created (UTC): 2026-04-21T23:30:00Z
- Repository HEAD: fd524f9d7350cc98cac94a770f2715a7d6bce67f
- Status: Proposal (draft, pre-implementation)
- Audience: Carbide Contributors; future Carbide contributors
- Scope: design-level proposal for two new shells (`carbide-cmd`, `carbide-bash`) that mirror `carbide-pwsh`'s architecture, share its VFS and I/O layer, and can be invoked from each other in both directions

## 1. Motivation

`carbide-pwsh` has proven, across three phases, that a sandboxed PowerShell-flavored shell can be built from scratch in Carbide-hostable C# and run end-to-end in xterm.js. The architecture — hand-rolled lexer → recursive-descent parser → tree-walking interpreter → curated built-in catalog, sitting on top of a virtualized filesystem and Carbide's T3 `System.Console` fork — generalizes cleanly. Two more shell dialects are worth the same treatment:

- **cmd.exe + batch (`.cmd` / `.bat`)** — still how a huge fraction of Windows automation is written. CI scripts, installer hooks, legacy helpers, `nmake` invocations. Being able to execute the common subset without shelling to a real `cmd.exe` makes those artifacts portable to Carbide's agent/sandbox niche.
- **bash (`.sh`)** — the universal Unix scripting dialect. Build scripts, test harnesses, CI pipelines, developer conveniences. Supporting a common subset makes cross-platform scripts runnable in the same shell environment pwsh users already have.

Beyond shipping each shell individually, the high-value feature is **cross-shell invocation**: a pwsh script can run `./backup.sh --all`, a bash script can run `powershell -Command 'Get-ChildItem /work'`, a cmd batch file can run `bash -c 'ls /work'`, and everything shares one virtualized filesystem, one environment-variable namespace, one xterm output stream, and one set of exit-code semantics.

That makes the Carbide sandbox a viable home for the kind of heterogeneous automation real codebases actually have — `build.sh` that shells out to `setup.cmd` that calls `Test-Stuff.ps1` — without forcing users to choose a single dialect or spawn real `cmd.exe` / `bash.exe` processes the WASM runtime can't host.

## 2. Executive summary

| Deliverable | Shape | Indicative LOC |
|---|---|---|
| `carbide-shell-core` — shared infrastructure package | Extracted VFS, I/O plumbing, `ShellDispatcher`, shared built-in helpers | ~800–1200 |
| `carbide-cmd` — cmd.exe subset | New package: lexer + parser + interpreter + built-in catalog + host, mirrors `carbide-pwsh` shape | ~3–4k |
| `carbide-bash` — bash subset | Same shape as `carbide-cmd` with bash grammar | ~4–6k |
| `carbide-pwsh` — refactor to use shared core | Moves VFS and I/O helpers into `carbide-shell-core`; adds cross-shell dispatch hook | +~300 LOC net delta |
| Cross-shell routing | File-extension resolver, `powershell` / `cmd` / `bash` built-ins in each shell, shared exit-code + env-var surface | ~500 LOC |
| Tests | xUnit coverage for each shell + cross-invocation integration | ~2–3k |

**Verdict: feasible, strategically aligned, and materially smaller than rewriting a stock `cmd.exe` or `bash`** because the scope is a curated subset that covers the *common* surface (`.bat` echo + set + if + for + goto + call; bash echo + cd + if + for + while + case + functions + arithmetic + string ops), not full parity. The pwsh implementation's tree-walking-interpreter-with-VFS-backed-built-ins design transfers cleanly — the new effort is roughly "two more passes of the same pattern with different grammar and built-ins", not a from-scratch architectural invention.

## 3. Non-goals

What this proposal explicitly does not commit to:

- **Bit-level compatibility with cmd.exe or bash**. We aim for "the common subset of handwritten scripts works"; edge cases documented as divergences.
- **External process spawning**. The shells run in Carbide's Mono-WASM single-threaded runtime. Any `.exe` / native binary invocation is out of scope; only Carbide-compiled `.dll` apps and cross-shell script invocation are supported.
- **Interactive features beyond line-by-line REPL**. No `bash` tab completion, no `readline` history, no `cmd` `doskey`. Those are Phase 4+ per-shell refinements.
- **Full POSIX compliance**. No `printf` format parity, no full `[[ ... ]]` regex semantics, no `coproc`, no job control, no `fc`, no subshell `()` concurrency.
- **cmd.exe networking, service control, drivers**. No `net`, `sc`, `reg`, `tasklist`, `wmic` — these don't have sandboxed analogues in our runtime.
- **bash extensions from zsh / ksh**. We target bash's common scripting subset, not bashisms in GNU extensions (associative arrays stretch-only, `[[ =~ ]]` regex stretch-only).
- **Real-disk access**. Like pwsh today, all three shells operate on the shared `VirtualFileSystem`. Host policy can mount real directories; default is fully sandboxed.
- **Interactive terminal control sequences beyond what T3's `System.Console` fork handles**. No alternate-screen, no xterm-specific mouse reporting, no readline-key bindings.

## 4. High-level architecture

### 4.1 Package layout

```
src/Carbide/packages/
├── carbide-pwsh/              (existing; refactored in Phase 0)
├── carbide-cmd/               (NEW)
├── carbide-bash/              (NEW)
└── carbide-shell-core/        (NEW; shared library referenced by all three)
```

The three shells each remain self-contained end-user packages (xterm.js demo + REPL entry + C# project). `carbide-shell-core` is a pure-C# class library referenced by all three; it has no standalone host page.

### 4.2 Layered architecture

```
 ┌──────────────────────── xterm.js terminal ────────────────────────┐
 └──────────────┬──────────────────┬───────────────────┬─────────────┘
                │                  │                   │
        ┌───────▼──────┐    ┌──────▼──────┐     ┌──────▼──────┐
        │ carbide-pwsh │    │ carbide-cmd │     │ carbide-bash│
        │ .ps1 / REPL  │    │ .cmd/.bat   │     │ .sh / REPL  │
        └───────┬──────┘    └──────┬──────┘     └──────┬──────┘
                │                  │                   │
                │  each shell:     │                   │
                │  - Lexer         │                   │
                │  - Parser        │                   │
                │  - Interpreter   │                   │
                │  - Built-ins     │                   │
                │  - Host (REPL)   │                   │
                │                  │                   │
                └────────┬─────────┴───────────────────┘
                         │
                 ┌───────▼────────────┐
                 │ carbide-shell-core │   shared across shells
                 │                    │
                 │  - VirtualFileSys  │   one tree, one $PWD
                 │  - EnvVarStore     │   one env namespace
                 │  - ShellDispatcher │   extension-based routing
                 │  - CarbideAppHost  │   invokes .dll apps
                 │  - StdIO bridge    │   Console.Out/In/Err plumbing
                 │  - CommonBuiltins  │   echo/cat/cp/mv/rm helpers
                 └────────┬───────────┘
                          │
                 ┌────────▼───────────┐
                 │  @carbide/core     │   Mono-WASM + T3 Console fork
                 └────────────────────┘
```

The invariants:

- One `VirtualFileSystem` instance per user session, shared by all three shells.
- One environment-variable map per session, shared across shells.
- One `Console.Out` / `Console.In` / `Console.Error` stack per submission, mediated by the session's host adapter.
- Each shell has its own `ShellHost` (lexer/parser/interpreter) but they all talk to the same `carbide-shell-core` services.
- `ShellDispatcher` routes invocations: extension-based (`.ps1` → pwsh, `.cmd`/`.bat` → cmd, `.sh` → bash, `.dll` → Carbide app) plus explicit-shell built-ins (`powershell`, `cmd`, `bash`).

### 4.3 Cross-shell invocation model

A command name `X` in any shell resolves in this order:

1. The current shell's own built-ins / keywords.
2. User functions / aliases defined in the current shell's session state.
3. `ShellDispatcher.TryResolve(X)`:
   - If `X` is a VFS path to a file with a known extension, dispatch to that shell's kernel.
   - If `X` matches a registered Carbide-compiled app, invoke via `CarbideAppHost`.
   - If `X` is one of the well-known shell names (`powershell`, `pwsh`, `cmd`, `cmd.exe`, `bash`, `sh`), dispatch to that shell's REPL-style sub-evaluator.
4. Otherwise, raise "command not found" (the shell's own error shape: `CommandNotFoundException` in pwsh, `'X' is not recognized as an internal or external command.` in cmd, `X: command not found` in bash).

Data passed to the invoked shell:

| Element | How |
|---|---|
| `argv` | Positional arguments, as `string[]` |
| `stdin` | `Console.In` (piped-in if the invocation is downstream of a pipeline) |
| `stdout` | `Console.Out` (piped-out if the invocation has a downstream) |
| `stderr` | `Console.Error` |
| Exit code | Return `int`; caller maps to dialect-appropriate variable (`$LASTEXITCODE` / `%ERRORLEVEL%` / `$?`) |
| Environment | Shared `EnvVarStore`; child sees parent's env; changes visible across shells |
| `$PWD` | Shared `VirtualFileSystem.CurrentLocation`; `cd` in any shell affects all |

Concrete round-trip examples (all run in the same xterm.js session):

```powershell
# pwsh invoking bash
./backup.sh --all
$LASTEXITCODE      # exit code from bash

# pwsh invoking cmd explicitly
cmd /c 'echo %USERNAME% && dir /b'

# pwsh invoking bash explicitly
bash -c 'ls -la /work && echo done'
```

```bat
REM cmd invoking pwsh
powershell -NoProfile -Command "Get-ChildItem /work | ConvertTo-Json"
echo %ERRORLEVEL%

REM cmd invoking bash
bash -c "tar -cf /tmp/bundle.tar /work"
```

```bash
# bash invoking pwsh
powershell -c 'Get-Process | Select-Object -First 5'
echo $?

# bash invoking cmd
cmd /c "dir /s /b"

# bash invoking a .ps1 by path (extension routing)
./report.ps1 --json
```

All of those work because the shared `ShellDispatcher` knows how to hand execution to the right kernel.

## 5. carbide-cmd design

### 5.1 Language subset

The cmd.exe / batch surface we commit to parse and execute:

#### 5.1.1 Tokens + lexing

- Commands and labels are case-insensitive (`IF` ≡ `if` ≡ `If`).
- Line-ending-sensitive: each line is either one command, one label (`:name`), or blank / comment.
- Comments: `REM ...` (to end of line), `::` (to end of line).
- Labels: `:name` at start of line.
- Variables: `%VAR%` (immediate), `!VAR!` (delayed, within `SETLOCAL ENABLEDELAYEDEXPANSION` blocks).
- Parameter references: `%0` (script path), `%1`–`%9`, `%*` (all args).
- Parameter modifiers: `%~f1` (full path), `%~dp1` (drive+dir), `%~n1` (name), `%~x1` (extension), `%~nx1` (name+ext), `%~z1` (size — Phase 2 stretch).
- String substitution: `%var:old=new%`, `%var:~start,len%`.
- Escape char: `^` (for `&`, `|`, `<`, `>`, newline).
- Double-quoted strings: `"..."` preserve spaces; contents don't interpolate `%var%` but do within ECHO's rendering.

#### 5.1.2 Control flow

```
IF [NOT] string1 == string2 command
IF [NOT] EXIST path command
IF [NOT] DEFINED name command
IF [NOT] ERRORLEVEL n command
IF /I …               # case-insensitive comparison
IF … ELSE …

FOR %X IN (set) DO command
FOR /L %X IN (start, step, end) DO command
FOR /F "options" %X IN (file-or-list) DO command

GOTO :label
CALL :label arg1 arg2
CALL script.cmd arg1 arg2
EXIT /B [code]
GOTO :EOF          # equivalent to EXIT /B on current process

SETLOCAL [ENABLEDELAYEDEXPANSION] [ENABLEEXTENSIONS]
ENDLOCAL
```

Phase 1 of `carbide-cmd` covers: IF with `==`, `EXIST`, `DEFINED`, `ERRORLEVEL`, `/I`, `NOT`; FOR-IN and FOR /L; GOTO :label; CALL :label; CALL script; EXIT /B; SETLOCAL / ENDLOCAL for delayed expansion.

Phase 2+ adds: FOR /F (token parsing), more parameter modifiers, `%var:~start,len%` substrings.

#### 5.1.3 Pipes, redirection, chaining

- `|` pipes stdout to next command's stdin.
- `>` redirects stdout to VFS file (overwrite).
- `>>` redirects stdout to VFS file (append).
- `<` feeds VFS file into stdin.
- `2>` redirects stderr.
- `2>&1` merges stderr into stdout.
- `&` unconditional chaining.
- `&&` chain if prior succeeded (`ERRORLEVEL == 0`).
- `||` chain if prior failed.

#### 5.1.4 Built-in commands

Phase 1 shipping catalog:

| Command | Purpose |
|---|---|
| `ECHO` / `ECHO.` / `ECHO OFF` / `ECHO ON` | Print + @-prefix suppression |
| `SET [name[=value]]` | Read/write env, list on no-arg |
| `SET /A expr` | Arithmetic |
| `CLS` | Clear screen |
| `DIR [path]` | List VFS entries |
| `CD [path]` / `CHDIR` | Change `$PWD` in VFS |
| `COPY src dest` | Copy file |
| `MOVE src dest` | Move file |
| `DEL file` / `ERASE file` | Delete file |
| `REN old new` / `RENAME` | Rename |
| `MD path` / `MKDIR` | Create directory |
| `RD path` / `RMDIR [/S]` | Remove directory (optionally recursive) |
| `TYPE file` | Print file contents |
| `EXIST path` | Existence check (implicit via IF) |
| `PAUSE` | Read a line (Phase 1 treats as immediate no-op with a prompt; no interactive UX) |
| `EXIT [/B] [code]` | Exit cmd or sub-batch |
| `TITLE s` | Set terminal title via OSC 0 |
| `VER` | Print "Carbide Cmd 1.0" banner |
| `COLOR fg[bg]` | Set default SGR colors |
| `TIMEOUT /T n` | Sleep (Phase 2 stretch) |
| `FIND "str" file` | Line-match filter |
| `FINDSTR pattern file` | Regex/substring filter |
| `SORT [/R]` | Sort stdin lines |
| `MORE` | Paginate (Phase 1: pass-through since xterm handles scroll) |

Cross-shell launchers (same table, any shell):

| Command (in cmd) | Effect |
|---|---|
| `POWERSHELL …` / `PWSH …` | Invoke carbide-pwsh with forwarded args |
| `BASH …` / `SH …` | Invoke carbide-bash |
| `CARBIDE-APP.DLL …` | Detected by extension; runs Carbide-compiled app |

#### 5.1.5 Parser / interpreter strategy

Same pattern as carbide-pwsh:

- **Lexer** — line-oriented. cmd's syntax is actually simpler than pwsh's: each statement is "command + space-separated argv until line end / pipe / redirection / conditional chain". The lexer produces tokens for command words, quoted strings, `%var%` references, operators (`|`, `>`, `>>`, `&&`, `||`, `&`, `^` escape), and newlines.
- **Parser** — small recursive-descent. AST nodes: `CmdLineAst`, `CommandAst`, `RedirectionAst`, `ChainAst`, `IfStatementAst`, `ForStatementAst`, `LabelAst`, `GotoStatementAst`, `CallStatementAst`, `SetLocalAst`, `ScriptAst`.
- **Interpreter** — tree-walker. Special structures: a label table for `GOTO` (pre-pass over the AST), a call stack for `CALL :label`, an env-var frame stack for `SETLOCAL`/`ENDLOCAL`.
- **Variable expansion** — performed at evaluation time per token. `%VAR%` expands immediately; `!VAR!` expands at command-execution time (when inside `SETLOCAL ENABLEDELAYEDEXPANSION`).
- **GOTO** — implemented as `PwshGotoException` analogous to `PwshReturnException`; caught by the top-level script runner, which seeks to the target label's AST index and resumes.

Indicative size: ~600 LOC lexer, ~800 LOC parser, ~800 LOC interpreter, ~1000 LOC built-ins.

### 5.2 Invocation surface

- Interactive REPL in xterm.js (`carbide-cmd`'s own page + Program.cs mirroring carbide-pwsh's).
- Programmatic `ShellHost.Submit(sourceText)` for in-process use.
- File execution: `carbide-cmd path\to\script.cmd arg1 arg2` (from CLI) or `cmd /c "…"` (from another shell) or `CALL script.cmd` (from within cmd).

### 5.3 Prompt shape

`C:\work>` — drive-letter path, then `>`. VFS paths start with `/`; we fabricate a drive letter `C:` for display (maps 1:1 to `/`). Alternately, use plain VFS paths: `/home/user>`; decide at implementation time.

## 6. carbide-bash design

### 6.1 Language subset

Phase 1 scope:

#### 6.1.1 Tokens + lexing

- Whitespace-separated words.
- Quotes: `'literal'` (no expansion), `"interpolated"` (supports `$var`, `${var}`, `\$`, `\\`, `\"`), `$'...'` (ANSI-C escapes — Phase 2).
- Escape: `\` outside quotes or inside double-quotes.
- Heredocs: `<< EOF …EOF`, `<<- EOF …EOF` (strip leading tabs), and `<<< string` (here-string).
- Comments: `#` to end of line.
- Variables: `$var`, `${var}`, `$@`, `$*`, `$#`, `$?`, `$$`, `$!`, `$0`–`$9`.
- Positional params beyond 9 via `shift`.
- Command substitution: `$(command)` and `\`command\``.
- Arithmetic: `$((expr))`, `(( expr ))` as a test-command.
- Parameter expansions: `${var:-default}`, `${var:=default}`, `${var:+alt}`, `${var:?err}`, `${var#prefix}`, `${var##prefix}`, `${var%suffix}`, `${var%%suffix}`, `${var/pattern/replacement}`, `${var//pattern/replacement}`, `${var:offset}`, `${var:offset:length}`, `${#var}`.
- Globbing: `*`, `?`, `[abc]` against VFS paths.
- Brace expansion: `{a,b,c}`, `{1..10}`.

#### 6.1.2 Control flow

```bash
if condition; then … elif condition; then … else … fi
for var in list; do …; done
for (( init; cond; update )); do …; done
while condition; do …; done
until condition; do …; done
case value in pattern) …;; esac

function name { … }     # bash-style
name() { … }            # posix-style
return [n]
exit [n]
break [n]
continue [n]

[[ expr ]]              # enhanced test
[ expr ]                # POSIX test
test expr

&&, ||, ;, &            # chaining
|                       # pipe
```

Phase 1 covers: `if`/`elif`/`else`/`fi`, `for x in …`, `while`, `until`, `case`/`esac`, functions (both syntaxes), `return`/`exit`/`break`/`continue`, `[[ ]]` and `[ ]` with string/number/file tests, `&&`/`||`/`;`/`|`. Phase 2 adds `select`, `((;;))`, `{ … }` command groups as first-class, and `time` timing.

#### 6.1.3 Built-in commands

Phase 1 shipping catalog:

| Built-in | Purpose |
|---|---|
| `echo` / `echo -n` / `echo -e` | Print |
| `printf` | Formatted print (`%s`, `%d`, `%x`, …) |
| `cd [dir]` | Change `$PWD` (shared with pwsh's Set-Location) |
| `pwd` | Print `$PWD` |
| `ls [-l] [-a] [-R] [path]` | VFS listing |
| `cat [file …]` | Concatenate / read file |
| `cp [-r] src dest` | Copy |
| `mv src dest` | Move / rename |
| `rm [-r] [-f] path` | Delete |
| `mkdir [-p] path` | Create dir |
| `rmdir path` | Remove empty dir |
| `touch path` | Create/update |
| `head [-n N]` / `tail [-n N]` | Line sampling |
| `wc [-l]` | Line count |
| `grep pattern [file …]` | Regex filter |
| `sort [-r] [-n]` | Sort |
| `uniq [-c]` | Dedupe |
| `tr old new` | Translate chars |
| `sed -e expr` | Stream editor (Phase 2) |
| `awk 'prog'` | Line processor (Phase 3 stretch — parser for awk is its own effort) |
| `export name[=value]` | Mark env var |
| `unset name` | Remove env var |
| `env` | List env |
| `read [-r] var` | Read line from stdin |
| `test` / `[` / `[[` | Condition evaluation |
| `true` / `false` | Exit-code builtins |
| `exit [n]` / `return [n]` | Flow control |
| `declare -a`, `declare -i` | Variable attributes |
| `local name[=value]` | Function-local var |
| `shift [n]` | Parameter shift |
| `source file` / `. file` | Dot-source |
| `eval string` | Evaluate as bash |
| `type name` | What would `name` resolve to? |
| `alias name=value` | Alias |

Cross-shell launchers:

| Command (in bash) | Effect |
|---|---|
| `powershell …` / `pwsh …` | Invoke carbide-pwsh |
| `cmd /c …` | Invoke carbide-cmd |
| `./script.cmd …` | Extension-route to carbide-cmd |
| `./script.ps1 …` | Extension-route to carbide-pwsh |
| `./app.dll …` | Invoke Carbide-compiled app |

#### 6.1.4 Parser / interpreter strategy

Bash is harder than cmd because:

- Expansions are multi-stage (brace → tilde → parameter → command → arithmetic → word-splitting → pathname-glob).
- Heredocs have peculiar whitespace rules.
- Quoting rules interact with expansion stages.
- Arithmetic (`$((…))`) is a distinct sub-grammar.

We handle it with an explicit staged evaluator:

1. **Lexer** — emits tokens for words, `$var`-at-position, operators (`|`, `&&`, `||`, `;`, `;;`, `<<`, `<<-`, `<<<`, `<`, `>`, `>>`, `2>`, `&>`, `(`, `)`, `{`, `}`, `[[`, `]]`, etc.), newlines, and heredoc bodies.
2. **Parser** — recursive descent, familiar shape. AST nodes: `SimpleCommandAst`, `PipelineAst`, `IfAst`, `ForAst`, `WhileAst`, `CaseAst`, `FunctionAst`, `BlockAst`, `TestAst`, `ArithAst`.
3. **Expansion** — separate module that performs brace/tilde/parameter/command/arithmetic/word-splitting/glob expansion on each word before execution. This is distinct from parsing and intentionally lazy.
4. **Interpreter** — executes the AST. Control flow via flow-exceptions (`BashBreakException`, `BashContinueException`, `BashReturnException`, `BashExitException`). Functions live in a `FunctionRegistry` (analogous to pwsh).
5. **Test evaluator** — handles `[ ... ]` and `[[ ... ]]` separately, with their own mini-grammars for `-eq`, `-f`, `-d`, `-z`, `-n`, `==`, `=~` (Phase 2 for the regex one), and the primary numeric/string operators.

Indicative size: ~900 LOC lexer, ~1.2k parser, ~400 LOC expansion, ~900 LOC interpreter, ~1.5k LOC built-ins.

### 6.2 Invocation surface

Same as cmd: interactive REPL, programmatic `Submit`, or file execution by path.

### 6.3 Prompt shape

Classic `user@host:/path$`. Static `user@host` values read from env; path is `$PWD`. Root prompts use `#` instead of `$` if the session has a "root" flag (Phase 4 nicety).

## 7. `carbide-shell-core` — the shared foundation

New C# class library that three shells all reference. Extracted from the existing `carbide-pwsh` code so the refactor is mostly moving files rather than reinventing anything.

### 7.1 What gets hoisted out of carbide-pwsh

| From `carbide-pwsh` | Moved to `carbide-shell-core` |
|---|---|
| `src/Vfs/VfsPath.cs` | `Vfs/VfsPath.cs` |
| `src/Vfs/VfsNode.cs` | `Vfs/VfsNode.cs` |
| `src/Vfs/VirtualFileSystem.cs` | `Vfs/VirtualFileSystem.cs` |
| `src/Vfs/VfsSnapshot.cs` | `Vfs/VfsSnapshot.cs` |
| Parts of `Host/OutputFormatter.cs` (VfsNode rendering) | `Io/VfsFormatter.cs` |
| Ampersand-lexer/token + call operator split | Keep pwsh-specific; each shell has its own lexer |
| Existing `AppRegistry.cs` | `Apps/AppRegistry.cs` (shared) |
| `StreamingStdOutWriter.cs` (from `@carbide/core` already) | No change |

### 7.2 New primitives in `carbide-shell-core`

```csharp
public interface IShellKernel
{
    string Name { get; }                    // "pwsh", "cmd", "bash"
    IReadOnlySet<string> FileExtensions { get; } // [".ps1"], [".cmd",".bat"], [".sh"]

    /// <summary>Evaluate a script source string; args/stdin/stdout/env in the ctx.</summary>
    int Execute(string source, ShellExecutionContext ctx);

    /// <summary>Evaluate a script file by absolute VFS path (handles shebangs if bash-flavored).</summary>
    int ExecuteFile(string absolutePath, ShellExecutionContext ctx);

    /// <summary>Parse-only — used to detect incomplete input in multi-line REPLs.</summary>
    bool IsCompleteInput(string source);
}

public sealed class ShellExecutionContext
{
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();
    public TextReader Input { get; init; } = TextReader.Null;
    public TextWriter Output { get; init; } = TextWriter.Null;
    public TextWriter Error { get; init; } = TextWriter.Null;
    public VirtualFileSystem Vfs { get; init; } = null!;
    public EnvVarStore Env { get; init; } = null!;
    public ShellDispatcher Dispatcher { get; init; } = null!;
}

public sealed class ShellDispatcher
{
    public void Register(IShellKernel kernel);

    /// <summary>Resolve a command name (path, app, or shell alias) to a handler.</summary>
    public bool TryResolveScript(string commandName, out IShellKernel? kernel, out string? resolvedPath);
    public bool TryResolveShellByName(string shellNameOrAlias, out IShellKernel? kernel);

    /// <summary>Execute. Caller supplies args/io/env; return = exit code.</summary>
    public int ExecuteScript(string path, IShellKernel kernel, ShellExecutionContext ctx);
    public int ExecuteInline(IShellKernel kernel, string source, ShellExecutionContext ctx);
}

public sealed class EnvVarStore
{
    public string? Get(string name);
    public void Set(string name, string? value);
    public IReadOnlyDictionary<string, string?> All { get; }
    public IDisposable PushScope();   // for bash subshells, cmd SETLOCAL, etc.
}
```

`EnvVarStore` is shared by all shells. When one shell sets `FOO=bar`, the others see it. Shells that support lexical scoping of env (cmd via `SETLOCAL`, bash subshells `( … )`) push a temporary frame. Changes within that frame unwind on exit.

### 7.3 Shared built-ins

A handful of built-ins are genuinely identical across shells (modulo surface naming). We express them once in `carbide-shell-core.CommonBuiltins` and each shell wraps them:

| Logical op | pwsh | cmd | bash |
|---|---|---|---|
| "Print text" | `Write-Output` / `Write-Host` | `ECHO` | `echo` |
| "List directory" | `Get-ChildItem` | `DIR` | `ls` |
| "Print file" | `Get-Content` | `TYPE` | `cat` |
| "Change dir" | `Set-Location` / `cd` | `CD` / `CHDIR` | `cd` |
| "Copy" | `Copy-Item` | `COPY` | `cp` |
| "Move/rename" | `Move-Item` | `MOVE` / `REN` | `mv` |
| "Delete" | `Remove-Item` | `DEL` / `RD` | `rm` / `rmdir` |
| "Create dir" | `New-Item -ItemType Directory` | `MD` / `MKDIR` | `mkdir` |
| "Touch file" | `New-Item -ItemType File` | `type nul > file` (idiomatic) | `touch` |
| "Exists check" | `Test-Path` | `IF EXIST …` | `test -e …` / `[[ -e … ]]` |
| "Pipe shape tool" | `ForEach-Object` / `Where-Object` | n/a | n/a (filters are external in bash) |

Formatting differences mean we can't reuse presentation code trivially — `DIR`'s format is specific to cmd, `ls`'s is specific to bash, pwsh has its own. So we share the **action** via `CommonBuiltins` and each shell wraps it in its own presenter.

## 8. Detailed cross-invocation flow

### 8.1 File-extension routing

Inside any shell, when a command name is resolved to a VFS path, the file's extension determines which kernel runs it:

```csharp
string ext = VfsPath.GetExtension(absolutePath).ToLowerInvariant();
IShellKernel? kernel = ext switch
{
    ".ps1" or ".psm1" => registry.Pwsh,
    ".cmd" or ".bat"  => registry.Cmd,
    ".sh"             => registry.Bash,
    ".dll"            => null,        // Carbide app — separate path
    ""                => InferFromShebang(absolutePath, registry),
    _                 => InferFromShebang(absolutePath, registry),
};
```

Shebang inference reads the first line: `#!/usr/bin/env bash`, `#!/bin/bash`, `#!/usr/bin/pwsh`, `#!/usr/bin/env powershell` all map to their respective kernels.

### 8.2 Explicit-shell invocation

Each shell exposes the others' names as commands. In the `Builtins` table:

```csharp
// carbide-cmd:
public sealed class PowerShellBuiltin : Builtin
{
    public override string Name => "POWERSHELL";
    public override IEnumerable<string> Aliases => new[] { "PWSH" };
    public override int Execute(IReadOnlyList<string> args, ShellExecutionContext ctx)
    {
        var (scriptText, file) = ParsePwshArgs(args);   // -NoProfile, -Command, -File, etc.
        return ctx.Dispatcher.ExecuteInline(ctx.Dispatcher.Pwsh, scriptText, ctx)
            ?? ctx.Dispatcher.ExecuteScript(file, ctx.Dispatcher.Pwsh, ctx);
    }
}
```

Arg parsing is conservative — we handle the common forms:

| Form | Handled as |
|---|---|
| `powershell -Command "…"` | Inline eval |
| `powershell -c "…"` | Inline eval |
| `powershell -File path` | Script file |
| `powershell script.ps1 arg1 arg2` | Script file with args |
| `bash -c "…"` | Inline eval |
| `bash script.sh arg1` | Script file |
| `cmd /c "…"` | Inline eval |
| `cmd /c "script.cmd"` | Inline eval (which cmd then interprets as script execution) |
| `cmd /k "…"` | Interactive mode — Phase 4 deferral; for Phase 1, treat as `/c`. |

### 8.3 Data passing

**argv**: passed as `string[]`, materialized in each shell's argument-binding idiom:

| Shell | argv binding |
|---|---|
| pwsh | `$args[0]`, `$args[1]`, …; also `param($a, $b)` for named |
| cmd | `%0` = script path, `%1`–`%9` = positional, `%*` = all remaining |
| bash | `$0` = script path, `$1`–`$9` = positional, `$@` = all quoted, `$*` = all joined, `$#` = count |

**stdin/stdout/stderr**: each invocation inherits the caller's streams unless the caller pipes. In pipelines, the `ShellDispatcher` sets up a `MemoryStream` between stages. For cross-shell pipelines:

```bash
# bash
ls /work | powershell -Command "ForEach-Object { $_.ToUpper() }"
```

`ls` emits lines; `powershell` receives them on stdin as a string stream. The pwsh side treats stdin as `Console.In` — each line becomes a pipeline input. This already works because we swap `Console.In` via the standard `TextReader` plumbing.

**Exit code**: the invoked kernel returns an `int`; the invoking shell maps it to its own idiom:

| Shell | Read exit code |
|---|---|
| pwsh | `$LASTEXITCODE` |
| cmd | `%ERRORLEVEL%` |
| bash | `$?` |

All three are kept in sync through the shared `ShellDispatcher.LastExitCode` slot (set every time any shell exits a sub-invocation), projected into each shell's automatic variable on read.

**Environment**: the shared `EnvVarStore`. A `bash` subshell `( … )` or cmd `SETLOCAL` pushes a scope on the store; changes unwind on exit. pwsh doesn't have lexical env scoping (matches real PowerShell). All three shells can read and write env; changes persist across invocations within the same session.

### 8.4 Pipeline interleaving

A cross-shell pipeline is a chain of `(kernel, source)` stages. The dispatcher builds pipes between consecutive stages:

```
ls /work | grep .txt | powershell -Command "ForEach-Object { $_.Length }"
```

Three stages, three kernels (bash, bash, pwsh). Implementation:

- Allocate `N-1` `MemoryStream`s (or pipes).
- For each stage, swap `Console.In` / `Console.Out` to point at the adjacent pipe.
- Execute stages in order; each stage reads previous's output as its stdin.
- Materialize the final stage's stdout back to the caller's (REPL's) `Console.Out`.

Each shell already supports reading from `Console.In` when its pipeline source isn't provided by upstream cmdlet output (e.g., pwsh's `Get-Content` without `-Path`, bash's `read`, cmd's `FIND` without a file arg). Thread that across shell boundaries using the same plumbing.

### 8.5 `$PWD` sharing

`cd /work` in any shell changes `VirtualFileSystem.CurrentLocation`. Next `pwd` / `cd` / `$PWD` / `%CD%` read in any shell reflects it. This is the same `VirtualFileSystem` instance.

Edge case: bash has `pushd` / `popd` / `$OLDPWD`. Phase 2 stretch; the stack lives on bash's side only.

## 9. Detailed implementation phasing

Following the carbide-pwsh three-phase pattern:

### 9.1 Phase 0 — shared-core extraction (1 PR)

Create `carbide-shell-core` package. Move `VirtualFileSystem`, `VfsPath`, `VfsNode`, `VfsSnapshot`, `AppRegistry` out of `carbide-pwsh`. Add `IShellKernel`, `ShellDispatcher`, `ShellExecutionContext`, `EnvVarStore`. Refactor `carbide-pwsh` to reference the shared core. No behavioral change.

**Gate**: all 252 existing carbide-pwsh tests still pass.

### 9.2 Phase 1 — carbide-cmd

Same shape as pwsh Phase 1–3 (expression → pipelines/VFS → control/functions/etc.), adapted to cmd semantics:

1. **Cmd-1** — minimal: lex + parse + eval of ECHO, SET, variable expansion, `%0`-`%9`, IF (==, EXIST, DEFINED, ERRORLEVEL), GOTO :label, labels, REM / ::, simple redirection `>`, `>>`.
2. **Cmd-2** — FOR-IN + FOR /L, CALL (both forms), EXIT /B, SETLOCAL with delayed expansion, full built-in catalog (DIR, CD, COPY, MOVE, DEL, REN, MD, RD, TYPE, CLS), `|` pipe, `&` / `&&` / `||` chains, `2>` stderr redirect.
3. **Cmd-3** — String manipulation (`%var:old=new%`, `%var:~s,l%`), `%~f1` etc. parameter modifiers, FOR /F (basic), FIND / FINDSTR / SORT / MORE built-ins, cross-shell launchers.

Each sub-phase is one PR with xUnit coverage for new surface.

### 9.3 Phase 2 — carbide-bash

Same shape:

1. **Bash-1** — lex + parse + eval of echo, set, variable expansion, `$1`-`$9`, `if`/`then`/`else`/`fi`, `for x in …`, simple redirection `>`, `>>`, command substitution `$(…)`, test via `[ … ]`.
2. **Bash-2** — `while`, `until`, `case`/`esac`, functions (both syntaxes), arithmetic `$(( … ))`, parameter expansions `${var:-default}` / `${var#prefix}` / `${var/a/b}`, full built-in catalog (ls, cat, cp, mv, rm, mkdir, head, tail, wc, grep, sort, uniq, tr), `|` pipe, `&&` / `||` / `;` chains, `2>` / `&>` stderr.
3. **Bash-3** — heredocs `<< EOF`, here-string `<<<`, globbing with `*`/`?`, brace expansion `{a,b,c}`, `[[ … ]]` tests, `export` / `unset` / `local` / `declare`, arrays basics `arr=(…)` / `${arr[@]}`, cross-shell launchers.

### 9.4 Phase 3 — cross-shell integration

This is the payoff phase. Most of it is just wiring:

1. **Cross-1** — `ShellDispatcher` file-extension routing. A pwsh script calling `./script.sh` dispatches through bash's kernel. Tests cover all 9 cross-shell extension-routing pairs.
2. **Cross-2** — Explicit launchers: `powershell`, `cmd`, `bash` built-ins in each shell. Tests cover `/c`/`-c`/`-Command`/`-File` arg shapes.
3. **Cross-3** — Cross-shell pipelines (`ls | pwsh -c "…"`), shared `$LASTEXITCODE`/`%ERRORLEVEL%`/`$?`, shared env, shared `$PWD`.
4. **Cross-4** — Heterogeneous integration tests: compound scripts that exercise all three shells calling each other (e.g., `build.sh` that calls `setup.cmd` that calls `Test.ps1`).

### 9.5 Phase 4 — polish (optional)

Per-shell niceties that Phase 1–3 deferred:

- cmd: `FOR /F` with full token/delim/options, `TIMEOUT`, `CHOICE`.
- bash: `sed` / `awk` (if worth the parser effort), `(( … ))` as test, `select` menus, `time` timing, subshell parallel `&` backgrounding (limited to sequential on single-threaded WASM).
- All: PSReadLine-like line editor with history + completion.

## 10. File-by-file deliverables

Phase 0 (shared core extraction):

```
src/Carbide/packages/carbide-shell-core/
├── CarbideShellCore.csproj           (new)
├── src/
│   ├── Vfs/ (moved)
│   │   ├── VfsPath.cs
│   │   ├── VfsNode.cs
│   │   ├── VirtualFileSystem.cs
│   │   └── VfsSnapshot.cs
│   ├── Env/
│   │   └── EnvVarStore.cs             (new, ~150 LOC)
│   ├── Dispatch/
│   │   ├── IShellKernel.cs            (new, ~40)
│   │   ├── ShellExecutionContext.cs   (new, ~50)
│   │   ├── ShellDispatcher.cs         (new, ~250)
│   │   └── CommandResolver.cs         (new, ~150)
│   ├── Apps/
│   │   └── AppRegistry.cs              (moved)
│   └── Io/
│       ├── StreamPipeBuilder.cs        (new, ~200)
│       └── VfsFormatter.cs             (moved)
└── test/
    ├── VfsTests.cs                     (moved)
    └── ShellDispatcherTests.cs         (~200 LOC new)
```

Phase 1 (carbide-cmd):

```
src/Carbide/packages/carbide-cmd/
├── CarbideCmd.csproj                   (~30)
├── index.html                          (~110)
├── scripts/{serve,smoke}.mjs           (~150)
├── src/
│   ├── Program.cs                      (~50, REPL entry)
│   ├── Lexer/
│   │   ├── Token.cs                    (~60)
│   │   ├── TokenKind.cs                (~40)
│   │   └── Lexer.cs                    (~600)
│   ├── Parser/
│   │   ├── Ast/                        (~200)
│   │   └── Parser.cs                   (~800)
│   ├── Runtime/
│   │   ├── Interpreter.cs              (~800)
│   │   ├── VarExpander.cs              (~250)
│   │   └── GotoExceptions.cs           (~50)
│   ├── Builtins/ (~1000 LOC total)
│   │   ├── Builtin.cs
│   │   ├── CmdletRegistry.cs
│   │   ├── EchoCommand.cs
│   │   ├── SetCommand.cs
│   │   ├── IfCommand.cs (handled by parser; not a builtin)
│   │   ├── DirCommand.cs
│   │   ├── CdCommand.cs
│   │   ├── CopyCommand.cs / MoveCommand.cs / DelCommand.cs
│   │   ├── RenCommand.cs / MdCommand.cs / RdCommand.cs
│   │   ├── TypeCommand.cs
│   │   ├── ClsCommand.cs
│   │   ├── ExitCommand.cs / PauseCommand.cs
│   │   ├── TitleCommand.cs / VerCommand.cs / ColorCommand.cs
│   │   ├── FindCommand.cs / FindStrCommand.cs / SortCommand.cs
│   │   └── CrossShell/
│   │       ├── PowerShellLauncher.cs
│   │       └── BashLauncher.cs
│   └── Host/
│       ├── ShellHost.cs                (~200)
│       └── Banner.cs                   (~20)
└── test/                               (~1500 LOC)
    ├── LexerTests.cs
    ├── ParserTests.cs
    ├── InterpreterTests.cs
    ├── BuiltinTests.cs
    ├── CrossShellTests.cs
    └── IntegrationTests.cs             (runs real .cmd scripts)
```

Phase 2 (carbide-bash): same shape, ~5–6k LOC total (bigger than cmd because parameter expansion and the test operator grammar are non-trivial).

Phase 3 (cross-shell integration): ~500 LOC new + ~1k LOC test code in a cross-shell test project.

## 11. Design decisions

### D1 — Three independent kernels, one dispatcher

Each shell has its own Lexer/Parser/Interpreter. This makes each one understandable in isolation and matches the established pwsh pattern. The alternative — one mega-parser that handles all three grammars with mode-switching — is unnecessarily clever and hard to debug.

### D2 — File extension is authoritative for script routing

`.ps1` → pwsh, `.cmd`/`.bat` → cmd, `.sh` → bash. Shebang is a secondary signal. This makes `./script.sh` unambiguous from any shell. Users can always fall back to explicit `bash script.sh` if they want to force a specific shell.

### D3 — Shared `EnvVarStore`, not per-shell env

Environment variables are global to the session. Setting `FOO=bar` in bash makes it visible to pwsh's `$env:FOO` and cmd's `%FOO%`. Matches real process-model behavior where child processes inherit but don't by default write back — we relax the "don't write back" part since we're all in one runtime, and it's a feature not a bug.

### D4 — Shared VFS and `$PWD`

All three shells operate on the same `VirtualFileSystem`. `cd /work` affects every shell's `$PWD`. If a user wants isolation, they push a sub-session.

### D5 — Cross-shell launchers are first-class built-ins, not special-case plumbing

`powershell`, `cmd`, `bash` are each built-in commands in the other two shells. They live in each shell's built-in catalog as regular commands that just happen to hand execution to the dispatcher. This makes the routing uniform with other built-ins and keeps the special-casing to the minimum.

### D6 — Exit codes are propagated as `int`, projected into each shell's automatic variable

`$LASTEXITCODE` / `%ERRORLEVEL%` / `$?` all read from the same `ShellDispatcher.LastExitCode` slot. A bash script `exit 7` surfaces to the calling pwsh as `$LASTEXITCODE = 7` and to the calling cmd as `%ERRORLEVEL% = 7`. The slot updates on every cross-invocation return — same behavior as real OS processes.

### D7 — Pipeline streams are text lines (byte streams under the hood)

`pwsh | cmd | bash` pipelines carry text. pwsh's "object pipeline" collapses to `string`-lines when crossing a shell boundary (via `Out-String` or equivalent). Bash/cmd both naturally process line streams. This is a lossy bridge but matches how real OS shells communicate.

### D8 — No threading, no concurrency, no background jobs

WASM is single-threaded. Bash's `cmd &` background invocation and `&` job control are out of scope. `( cmd )` subshells in bash run synchronously (as if `&` were a `;`). `START /B` in cmd similarly degrades. This is documented and tested; anyone needing real parallelism uses a different runtime.

### D9 — cmd and bash use their native quoting, but argv across shells is plain `string[]`

Each shell has its own quoting rules for its own scripts. When invoking a foreign shell, the caller's shell evaluates quoting first, producing a plain `string[]` of already-unquoted tokens. The foreign shell re-quotes at presentation time if needed. This avoids the classic Windows "args containing spaces" nightmare.

### D10 — Heredocs and here-strings are text payloads, not parseable inside the heredoc

A bash heredoc `<< EOF` collects lines verbatim until `EOF`. Interpolation of `$var` happens at expansion time, not parse time. Same applies to cmd's quoted ECHO strings: `%VAR%` expands when the ECHO runs.

### D11 — Case-insensitive cmd, case-sensitive bash

cmd treats keywords, built-in names, and env-var names case-insensitively. bash is case-sensitive throughout (except `[[ = =~ ]]` string comparisons which are case-sensitive). Each shell's identifier lookup uses the appropriate comparer.

### D12 — Glob and regex live in the shell that invokes them

Bash's `*`/`?` path globs expand against the VFS at expansion time. cmd doesn't have a general glob — `DIR *.txt` works because `DIR` itself handles globbing. Carbide-bash has a `Globber` helper in its expander; cmd's `DIR` / `COPY` / `DEL` each call into a shared `VfsGlobber` from `carbide-shell-core`.

## 12. Risks and open questions

| # | Risk / question | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | Bash's parameter-expansion stages interact in ways that are easy to get subtly wrong (e.g. `"${var/"$bad"/good}"` nesting) | High | Medium | Fixture-heavy test corpus of expansion shapes; document divergences prominently; don't claim full parity. |
| R2 | cmd.exe has a remarkably large number of edge cases that people actually depend on (quoting around `%%`, `^^`, `!!` interactions) | High | Medium | Subset-focused scope; document divergences; let users fall back to external `cmd.exe` if needed. |
| R3 | Cross-shell pipelines drop object fidelity (pwsh→bash loses type info) | Certain | Low | Documented. Pipelines are text streams across shell boundaries. Users who need object fidelity stay within pwsh. |
| R4 | Heredocs and multi-line constructs confuse the multi-line REPL logic | Medium | Medium | Each shell has its own `IsCompleteInput` implementation; REPL dispatches to the active kernel's parser to check. |
| R5 | `GOTO` in cmd lets labels backward-jump infinitely; a malicious script can DOS the runtime | Medium | Medium | Iteration-count ceiling (configurable, default 10⁶) per script; past the ceiling, raise `CmdRuntimeException`. |
| R6 | bash globbing against a huge VFS is slow | Low | Low | VFS enumerations are cheap for Phase-1-scale trees; revisit if profile surfaces an issue. |
| R7 | Env-var scoping across bash subshells vs cmd SETLOCAL may diverge from real behavior | Medium | Low | Document. The `EnvVarStore.PushScope()` primitive matches bash subshell semantics closely; cmd SETLOCAL uses the same primitive with an explicit ENDLOCAL handler. |
| R8 | Cross-shell invocation silently loses `$PSCulture` / `LANG` distinctions | Low | Low | Phase 4 concern. |
| R9 | Users expect `cmd.exe` paths with backslashes (`C:\work`) but our VFS uses forward slashes | Medium | Low | cmd's path-parse layer accepts both `\` and `/`; all VFS output uses forward slashes. Presentation layer in cmd optionally renders as backslashes (toggle). |
| R10 | Large .cmd scripts from real-world codebases exercise features outside our subset (e.g. long IF expressions, undocumented `cmd /f` behaviors) | High | Low | Expected. We publish a compatibility matrix. |
| R11 | bash's `source` and cmd's `CALL` with arguments interact with argv scoping in non-obvious ways | Medium | Medium | Write a targeted test corpus that pins behavior per shell; match real bash / cmd semantics where we can. |
| R12 | Starting up three shells simultaneously inflates Carbide's WASM session size beyond the budget | Low | Low | Each shell is 3–5k LOC + tests; a few MB in the compiled session. The WASM runtime already carries Roslyn + `@carbide/core`; one or two MB more is negligible. |
| R13 | Cross-invoking a shell from inside a deep pipeline exceeds stack depth on WASM | Low | Medium | Pipeline execution is iterative, not recursive; only function/scriptblock calls nest stack. For a reasonable nesting (< ~20 levels), no issue. |

## 13. Relationship to carbide-pwsh

This proposal **does not** require any changes to `carbide-pwsh`'s parser or interpreter except the Phase 0 extraction of VFS/Apps/I/O primitives into `carbide-shell-core`. After that extraction, carbide-pwsh remains behaviorally identical (same 252 tests green). The cross-shell launchers (`cmd`, `bash`) become new built-in cmdlets in carbide-pwsh that delegate to the `ShellDispatcher`.

carbide-pwsh's existing Phase 3 features — functions, classes, error handling — are orthogonal to this work. Cross-shell invocation doesn't care whether the invoker or invokee is pwsh; it just sees `IShellKernel`s talking through `ShellExecutionContext`s.

## 14. Success criteria

A minimum-useful delivery is:

- `carbide-shell-core` is extracted; carbide-pwsh's 252 tests still pass.
- `carbide-cmd` runs a 50-line hand-written `build.cmd` that uses ECHO, SET, IF EXIST, FOR %I IN, CALL :label, and simple pipes / redirection.
- `carbide-bash` runs a 50-line hand-written `build.sh` that uses echo, cd, if/then/fi, for x in list; do; done, command substitution $(…), test [ -f file ], and simple pipes / redirection.
- Cross-shell exit gate: a pwsh session runs `./build.sh` which inside calls `cmd /c "setup.cmd"` which inside calls `powershell -c "Get-ChildItem /work"`. Output is correct; `$LASTEXITCODE` propagates.
- An xUnit integration-test suite covers each shell's minimum useful surface plus the cross-shell scenarios. Target: ~400+ tests across all three shells.
- Each shell has its own `/packages/<name>/index.html` demo page driven through xterm.js.
- Each shell's Playwright smoke test passes the headline exit-gate script.

## 15. Appendices

### 15.1 Example cross-shell scripts

**`/work/deploy.sh`** (bash, shelling to pwsh and cmd):

```bash
#!/bin/bash
set -e
echo "preparing build..."
cmd /c "if not exist build md build"
powershell -Command "Get-ChildItem /src | Where-Object { \$_.Length -gt 1000 }"
for f in /src/*.txt; do
    cat "$f" | powershell -c "[string]::Join(',', (\$input -split '\n'))"
done
echo "done, exit=$?"
```

**`/work/setup.cmd`** (cmd, shelling to bash):

```bat
@echo off
setlocal enabledelayedexpansion
set BUILD_ROOT=/tmp/build
if not exist %BUILD_ROOT% md %BUILD_ROOT%
bash -c "find /src -name '*.json' | sort" > %BUILD_ROOT%\json-index.txt
call :process %BUILD_ROOT%\json-index.txt
exit /b %ERRORLEVEL%

:process
for /f "delims=" %%L in (%1) do (
    echo processing %%L
    powershell -c "Get-Content '%%L' | ConvertFrom-Json | ConvertTo-Json -Depth 5"
)
goto :eof
```

**`/work/orchestrate.ps1`** (pwsh, shelling to bash and cmd):

```powershell
param([string] $Env = 'dev')

$ErrorActionPreference = 'Stop'
bash -c "./scripts/pre-build.sh $Env"
cmd /c "setup.cmd --env=$Env"
if ($LASTEXITCODE -ne 0) { throw "setup failed" }
Write-Host 'post-build'
bash -c "./scripts/post-build.sh"
```

All three of those scripts run end-to-end in one Carbide session, sharing the same VFS, env vars, and `$PWD`.

### 15.2 Grammar sketch (indicative, not normative)

cmd:

```
script         ::= line (newline line)*
line           ::= label | statement | comment | blank
label          ::= ':' identifier
comment        ::= ('REM' | '::') any-to-eol
statement      ::= simpleCommand (chain simpleCommand)*
chain          ::= '|' | '&' | '&&' | '||'
simpleCommand  ::= '@'? name arg* redirection*
redirection    ::= '>' filename | '>>' filename | '<' filename | '2>' filename | '2>&1'
arg            ::= quotedString | bareWord | varReference
varReference   ::= '%' name '%' | '%' digit | '%~' modifiers digit | '!' name '!'
ifStatement    ::= 'IF' ('NOT')? ('/I')? condition '(' body ')' ( 'ELSE' '(' body ')' )?
forStatement   ::= 'FOR' ('/L' | '/F' '"'options'"')? '%' var 'IN' '(' set ')' 'DO' body
```

bash (simplified):

```
script         ::= list
list           ::= pipeline ( ( ';' | '&&' | '||' | '&' ) pipeline )*
pipeline       ::= command ( '|' command )*
command        ::= simpleCommand | ifCommand | forCommand | whileCommand | caseCommand | funcDef | '{' list '}' | '(' list ')'
simpleCommand  ::= word+ redirection*
redirection    ::= '>' word | '>>' word | '<' word | '2>' word | '<<' word '<<<' word
word           ::= dqString | sqString | bareWord | varReference | commandSub
varReference   ::= '$' name | '${' paramExpansion '}' | '$(' list ')' | '`' list '`'
paramExpansion ::= name | name ':-' word | name ':=' word | name '#' pattern | name '%' pattern | name '/' pattern '/' replacement
ifCommand      ::= 'if' list 'then' list ('elif' list 'then' list)* ('else' list)? 'fi'
forCommand     ::= 'for' name 'in' word+ 'do' list 'done'
funcDef        ::= name '(' ')' '{' list '}' | 'function' name '{' list '}'
test           ::= '[' testExpr ']' | '[[' testExpr ']]' | 'test' testExpr
```

### 15.3 Links

- [carbide-pwsh subset proposal](carbide-pwsh-subset-shell-proposal__2026-04-21__21-30-00-000000__e9c4b27a8f13.md) — the architectural pattern this proposal mirrors.
- [carbide-pwsh Phase 3 plan](../planning/carbide-pwsh-phase3-detailed-plan__2026-04-21__23-00-00-000000__f8c3e2a9b471.md) — concrete evidence that the tree-walking-interpreter-with-VFS shape reaches a "useful scripting language" milestone within a manageable LOC budget.
- [cmd.exe reference — Microsoft Learn](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/windows-commands) — the surface we subset.
- [bash reference manual](https://www.gnu.org/software/bash/manual/bash.html) — the surface bash subsets from.
- [POSIX.1-2024 shell spec](https://pubs.opengroup.org/onlinepubs/9799919799/utilities/V3_chap02.html) — the normative minimum for the bash subset.

### 15.4 What this proposal does not commit to

- A final naming decision. `carbide-cmd` / `carbide-bash` are working titles; `carbide-batch` / `carbide-sh` are fine too.
- A specific UI / terminal aesthetic. Each shell's xterm host page can differ.
- Whether to ship a single "meta-shell" that picks a kernel per-submission based on extension. Could be a Phase 4 convenience but isn't required.
- Whether to publish a `carbide-multishell` npm package that bundles all three. Decided at implementation time — most users want one shell per page.
- Whether real `cmd.exe` / `bash` behavior divergences are fixed or documented. Decided per case during Phase 1–3 as tests reveal them.
- Exit-code mapping for cross-shell failures that don't correspond to a normal exit (e.g. parse error in the invoked script). Proposal: return `255` per POSIX convention, settable to `1` per Windows convention by a policy flag.
