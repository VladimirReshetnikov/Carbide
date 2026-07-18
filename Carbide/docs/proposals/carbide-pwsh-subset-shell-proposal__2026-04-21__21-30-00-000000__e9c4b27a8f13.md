# Proposal: a small, practical PowerShell-subset shell for Carbide + xterm.js

- Created (UTC): 2026-04-21T21:30:00Z
- Repository HEAD: ad9a5ea93897117cd90e2e6e36142bc90927cea2
- Status: Proposal (draft, pre-implementation)
- Audience: Carbide Contributors; future Carbide contributors
- Scope: design-level proposal for a clean-room PowerShell-flavored interactive shell that runs inside Carbide's Mono-WASM runtime with xterm.js as the terminal surface, backed by a virtualized filesystem

## 1. Motivation

Two earlier feasibility reports explored forking `lib/pwsh` (the real PowerShell 7 sources) and running a trimmed `System.Management.Automation` engine under Carbide in Node:

- [carbide-pwsh-subset-feasibility](../research/pwsh/carbide-pwsh-subset-feasibility__2026-04-19__10-30-00-000000__a7c3d4e9f1b2.md)
- [carbide-powershell-subset-feasibility](../research/powershell/carbide-powershell-subset-feasibility__2026-04-19__20-23-22-238572__8b6d83c519ba.md)

Both reports agreed: the full-engine port is *technically feasible but a medium-to-large subproject*. The friction is not in PowerShell's parser (portable) or interpreter (portable) but in the surrounding assumptions the engine makes about its host — `DynamicMethod`, `AssemblyBuilder`, `PSThreadOptions` requiring worker threads, `InitialSessionState.CreateDefault2()` auto-loading a `FileSystemProvider` that expects a real disk, `CoreAdapter` member-access fast paths, `CommandDiscovery` routing unknown commands to a process-spawning `NativeCommandProcessor`, and a source tree that cannot be built by `@carbide/msbuild-lite` without considerable MSBuild parity work. The independent review framed the realistic net-new surface at "indicatively ~8–15k LOC net delta" once the persistent-session host, threading-model collapse, VFS bridge, InitialSessionState curation, and `CommandTypes.Application` refusal were all accounted for.

Since those reports were written, three things changed Carbide's runtime:

1. **T2.1 resolved** (5fb7afe7d, 51675bc85): `await Console.In.ReadLineAsync()` and other `await` points in a Carbide-compiled REPL no longer trip `PlatformNotSupportedException: Cannot wait on monitors on this runtime`. Interactive-loop code is now a first-class shape.
2. **T3 landed** (commit 43db73bda, referenced by the Current-State Guide): a forked `System.Console.dll` whose `ForegroundColor`, `BackgroundColor`, `SetCursorPosition`, `Title`, `Clear`, `WindowWidth/Height`, `TreatControlCAsInput`, `CancelKeyPress`, and `Beep` work against xterm.js through Carbide's streaming stdout writer. Pre-compiled NuGet libraries that touch the cosmetic `Console` surface no longer throw.
3. **carbide-gh demo shipped** (ad9a5ea93, 5fb7afe7d): a Spectre.Console-powered interactive REPL, compiled from four C# files + a vendored `Spectre.Console.dll`, runs against `https://api.github.com` through the Mono-WASM fetch bridge and renders rich widgets into xterm. This demonstrates end-to-end that a C# console app compiled by Carbide can behave like a real interactive shell.

This proposal pivots off that landscape with Carbide Contributors' explicit direction:

> We need to implement some small but practical subset of pwsh to have a xterm.js-hosted shell with a sandboxed or virtualized filesystem. It need not be a direct port of `lib/pwsh` sources, we can implement it in any way convenient to us. It should support expression evaluation, calling .NET API in pwsh syntax (`[System.Console]::BackgroundColor = 'DarkBlue'`), running .NET console apps that use only limited API supported in Carbide, and such.

The "need not be a direct port" clause is the load-bearing permission. It lets us sidestep every single structural obstacle the two feasibility reports identified and replace them with a smaller clean-room problem: *write a PowerShell-flavored parser and tree-walking interpreter in modern C#, target Carbide's runtime directly, ship a curated cmdlet set plus a virtualized FS, and host it in xterm.js exactly the way carbide-gh hosts a Spectre REPL*.

## 2. Executive summary

**Recommendation: build it. Clean room, not `lib/pwsh` fork.**

The subset we actually need is small enough that hand-writing it is cheaper than porting:

| Concern | `lib/pwsh` fork path | Clean-room path |
|---|---|---|
| Parser | ~48k LOC of hand-rolled C#, cross-linked to the engine's AST types; every surface we don't want still compiles | 1–2k LOC of hand-rolled recursive-descent C# over our own AST; only what we want to parse |
| Interpreter | Light-compiler fork of the DLR (~12k LOC) + binders + `CallSite<T>` + reflection adapters; 23 `Expression.Compile` sites across 13 files to audit; `DynamicMethod` in `CoreAdapter`; `AssemblyBuilder` in `EventManager` | Tree-walker over our AST; zero IL emission, zero DLR, zero `CallSite<T>`; reflection only for the `[Type]::Member` bridge and instance-member lookup |
| Session state | Force `PSThreadOptions.UseCurrentThread`; patch `CreateDefault2()` to not auto-load FileSystem/Registry; curate an `InitialSessionState` by hand anyway | A dictionary of scopes; no threading model to collapse |
| Command discovery | Patch or intercept `NativeCommandProcessor` to refuse `CommandTypes.Application` | A command-name dispatcher we write from scratch — cmdlet registry first, then functions, then VFS-backed scripts, then Carbide-compiled apps |
| Build system | Fork SMA.csproj, strip package references, exclude `engine/remoting/*`, `Interop/Windows/*`, `ComInterop/*`, `cimSupport/*`, `security/*native*`, patch `PowerShell.Common.props` dependency, decide between `git subtree`/copy/patch strategy, keep upstream sync as recurring tax | One normal `net10.0` csproj with one or two NuGet references (`Newtonsoft.Json` already on Carbide's allow-list); builds under `@carbide/msbuild-lite` today without any changes |
| Sizing to useful state | "~8–15k LOC net delta" (post-review estimate) + ongoing upstream-sync cost | Target ~4–7k LOC across parser, AST, evaluator, VFS, cmdlet catalog, host adapter, and tests; single-codebase maintenance |

The clean-room path also *actually fits Carbide's runtime model today*. The feasibility reports spent a lot of their scope budget on how to make pwsh's host assumptions fit Carbide; the clean room never has that mismatch because we are the author.

Verdict: **feasible, strategically aligned, and materially smaller than the fork path**. Carbide-side changes are limited to (a) possibly adding a small number of cmdlets/helpers as a new package `packages/carbide-pwsh/`, (b) exposing a way for the shell to invoke a second Carbide project (Phase 3), and (c) optional VFS-persistence host hooks for browser/Node.

## 3. Scope

### 3.1 In scope

- **Interactive shell running in xterm.js**, hosted by Carbide the same way carbide-gh hosts its REPL (`project.runInteractive({ terminal })`).
- **Script execution** via a script path in the VFS or an inline `-Command` string.
- **Useful PowerShell-flavored language subset** — see §4 for the surface.
- **Expression evaluation**: arithmetic, strings (interpolated and literal), booleans, arrays, hashtables, pipelines, comparisons, type conversions.
- **.NET interop through `[Type]::Member(…)` and `[Type]::Property = value`** — e.g. `[System.Console]::BackgroundColor = 'DarkBlue'`, `[System.Math]::Sqrt(2)`, `[DateTime]::Now.ToString('o')`.
- **Virtualized filesystem** with `Get-ChildItem`, `Get-Content`, `Set-Content`, `New-Item`, `Remove-Item`, `Test-Path`, `Set-Location`, `Resolve-Path`, `Copy-Item`, `Move-Item`, and `Join-Path`.
- **A curated cmdlet catalog** that covers the pipeline-shaping primitives (`Where-Object`, `ForEach-Object`, `Select-Object`, `Sort-Object`, `Measure-Object`, `Group-Object`, `ConvertTo-Json`, `ConvertFrom-Json`, `Write-Output`, `Write-Host`, `Write-Error`, `Read-Host`).
- **Running Carbide-compiled .NET console apps as commands** — either pre-registered DLLs (via `session.addReference(bytes)`) or dynamically loaded from the VFS.
- **Variables, functions, control flow**: `$x = …`, `function Foo { … }`, `if`/`elseif`/`else`, `while`, `for`, `foreach`, `switch`, `try`/`catch`/`finally`, `throw`, `break`, `continue`, `return`.
- **Scope discipline**: function-local and script-local scopes with explicit dotted-source behavior (`. ./script.ps1` runs in caller scope).

### 3.2 Non-goals (explicit)

These belong in "maybe later, if ever" territory and are **not** committed by this proposal:

- Byte-for-byte compatibility with `pwsh.exe` — we aim for "feels like PowerShell for the features we support."
- Remoting (`Invoke-Command -ComputerName`, `Enter-PSSession`), WinRM, SSH-based sessions, named pipes — not reachable under Carbide's runtime and not part of the shell's intent.
- CIM/WMI (`Get-CimInstance`, `Get-WmiObject`) — native + Windows-only + irrelevant to the sandboxed shell.
- Binary module loading (`.psd1`/`.psm1` files that reference `.dll` cmdlet libraries via reflection) — revisited once the curated cmdlet surface stabilizes.
- `Add-Type` with inline C# — interesting follow-up that would be powered by Carbide's own Roslyn (§12), but not day-one.
- PSReadLine (advanced line editing, syntax highlighting, multi-line composition) — start with the simple line editor carbide-gh already uses; layer PSReadLine-style features as a standalone future proposal.
- DSC, ScheduledJobs, Jobs (`Start-Job`), ETW, Event Log cmdlets, Registry provider, Certificate provider, Security cmdlets — out of scope.
- `$PSDefaultParameterValues`, dynamic parameters, parameter-set auto-disambiguation, `ValidateScript` / `ArgumentCompleter` attributes — tiered onto Phase 4+ if useful; first three phases ship with positional/named parameters only.
- Full PowerShell type-coercion parity (the [Language Specification v3](https://learn.microsoft.com/en-us/powershell/scripting/lang-spec/chapter-06) occupies many pages) — we cover the common cases explicitly and document deviations for the edge cases.

## 4. Language subset

The surface we commit to parse and execute. Anything not listed here is either out of scope or deferred.

### 4.1 Tokens and literals

- Identifiers: `[A-Za-z_][A-Za-z_0-9]*`.
- Variables: `$name`, `${name with spaces}`.
- Scope-qualified variables: `$script:x`, `$global:x`, `$env:PATH`, `$function:Foo`.
- Automatic variables at minimum: `$_` (pipeline current), `$PSItem` (alias), `$?` (last-success), `$PWD`, `$HOME`, `$args`, `$PSScriptRoot`, `$null`, `$true`, `$false`, `$Matches`.
- Numeric literals: integer, decimal, hex (`0x...`), with multiplier suffixes `kb`/`mb`/`gb`/`tb`/`pb`.
- String literals:
  - `"…"` — double-quoted, interpolated. Supports `$var`, `$(expression)`, and `` `n ``, `` `t ``, `` `r ``, `` `` ` ``, `` `" `` escapes.
  - `'…'` — single-quoted, literal. No interpolation.
  - Here-strings: `@"…"@` and `@'…'@` — multi-line, same interpolation rules as the single-line variants.
- Boolean literals: `$true`, `$false`, `$null`.
- Array literal: `@(1, 2, 3)`, or comma expressions `1, 2, 3` in expression contexts.
- Hashtable literal: `@{ key = value; key2 = value2 }`.
- Range: `1..10`, `10..1` (descending), `'a'..'z'` (character range).
- Type literal: `[int]`, `[System.String]`, `[System.Collections.Generic.List[int]]`.

### 4.2 Operators

Arithmetic (`+ - * / %`), string concatenation (`+` with coercion), comparison (PowerShell-style dashed form):

| Category | Operators |
|---|---|
| Equality | `-eq`, `-ne`, `-ceq`, `-cne` |
| Ordering | `-lt`, `-le`, `-gt`, `-ge` (and case-sensitive `c` variants) |
| Containment | `-contains`, `-notcontains`, `-in`, `-notin` |
| Match | `-match`, `-notmatch`, `-like`, `-notlike` |
| Logical | `-and`, `-or`, `-not`, `-xor`, `!` |
| Bitwise | `-band`, `-bor`, `-bxor`, `-bnot`, `-shl`, `-shr` |
| Assignment | `=`, `+=`, `-=`, `*=`, `/=`, `%=` |
| Unary | `-`, `+`, `++`, `--` |
| Cast | `[type] value` |
| Pipeline | `\|` |
| Format | `-f` (e.g. `'{0:N2}' -f 3.14`) |
| Join/Split | `-join`, `-split` |
| Replace | `-replace`, `-creplace` |

When the left-hand side is a collection, comparison operators filter element-wise (`@(1,2,3) -gt 1` returns `@(2,3)`) — this is how PowerShell's pipeline idioms read. We match that behavior.

### 4.3 Expressions and statements

- Member access: `$obj.Property`, `$obj.Method($arg1, $arg2)`.
- Index: `$arr[0]`, `$arr[0..2]`, `$hash['key']`.
- Static member: `[Type]::StaticMember`, `[Type]::Method($x)`.
- Type cast: `[int]$x`, `[string[]]$arr`.
- Subexpressions: `$(…)` evaluates and substitutes a value; `@(…)` always coerces to array; `&{…}` invokes a script block.
- Control flow: `if`/`elseif`/`else`, `while`/`do…while`/`do…until`, `for`, `foreach ($x in $xs)`, `switch`, `break`, `continue`, `return`.
- Error handling: `try { … } catch [Type] { … } finally { … }`, `throw`.
- Function definition: `function Name { param($a, $b) body }`; advanced-function attributes (`[Parameter()]`, `[ValidateSet()]`) parsed but enforcement limited to simple cases in Phase 2.
- Script block: `{ param(...) body }`; invocable via `&`, `.`, or as a cmdlet argument.
- Pipeline: `cmd1 | cmd2 | cmd3`; begin/process/end semantics for built-in cmdlets.
- Dot-sourcing: `. ./script.ps1` evaluates the script in the calling scope.

### 4.4 Explicitly deferred for later phases

- Classes (`class Foo { … }`), enums (`enum Bar { … }`) — Phase 4.
- Data sections, DSC, workflow — out of scope permanently.
- `using namespace`, `using module` — Phase 4.
- Parameter sets beyond "one set per cmdlet" — Phase 4.
- Argument completers and validation attributes beyond `[ValidateNotNull]`, `[ValidateSet]` — Phase 4.

## 5. Architecture

### 5.1 Project layout

A new Carbide package:

```
src/Carbide/packages/carbide-pwsh/
├── README.md
├── index.html                      # xterm.js host page
├── package.json
├── scripts/
│   ├── serve.mjs                   # mirrors carbide-gh's server
│   └── smoke.mjs                   # headless playwright smoke
├── public/
│   └── (nothing vendored; the shell is a from-source Carbide build)
├── src/                            # C# shell sources
│   ├── CarbidePwsh.csproj
│   ├── Program.cs                  # REPL entry (mirror of carbide-gh/src/Program.cs)
│   ├── Lexer/
│   │   ├── Token.cs
│   │   ├── TokenKind.cs
│   │   └── Lexer.cs
│   ├── Parser/
│   │   ├── Ast/                    # AST node classes
│   │   └── Parser.cs
│   ├── Runtime/
│   │   ├── Scope.cs                # variable/function scope stack
│   │   ├── Interpreter.cs          # tree-walking evaluator
│   │   ├── Pipeline.cs             # begin/process/end cmdlet pipeline
│   │   ├── TypeBridge.cs           # [Type]::Member reflection dispatch
│   │   └── Coercion.cs             # PowerShell-flavored type coercion
│   ├── Cmdlets/
│   │   ├── Cmdlet.cs               # base class
│   │   ├── WhereObject.cs
│   │   ├── ForEachObject.cs
│   │   ├── SelectObject.cs
│   │   ├── SortObject.cs
│   │   ├── MeasureObject.cs
│   │   ├── GroupObject.cs
│   │   ├── WriteOutput.cs
│   │   ├── WriteHost.cs
│   │   ├── WriteError.cs
│   │   ├── ReadHost.cs
│   │   ├── ConvertToJson.cs
│   │   ├── ConvertFromJson.cs
│   │   ├── GetChildItem.cs
│   │   ├── GetContent.cs
│   │   ├── SetContent.cs
│   │   ├── NewItem.cs
│   │   ├── RemoveItem.cs
│   │   ├── TestPath.cs
│   │   ├── SetLocation.cs
│   │   ├── ResolvePath.cs
│   │   ├── CopyItem.cs
│   │   ├── MoveItem.cs
│   │   ├── JoinPath.cs
│   │   └── InvokeApp.cs            # runs Carbide-compiled apps (Phase 3)
│   ├── Vfs/
│   │   ├── VirtualFile.cs
│   │   ├── VirtualDirectory.cs
│   │   ├── VirtualFileSystem.cs
│   │   ├── VfsPath.cs
│   │   └── VfsSnapshot.cs          # serialize/deserialize to a JSON snapshot
│   ├── Host/
│   │   ├── ShellHost.cs            # REPL driver
│   │   ├── LineReader.cs           # thin wrapper over CarbideConsole
│   │   └── OutputFormatter.cs      # Format-Table-style rendering
│   └── Properties/
└── test/
    ├── fixtures/
    │   ├── parser/                 # input → expected AST JSON
    │   ├── interpreter/            # input → expected stdout
    │   └── scripts/                # integration scripts
    └── (xunit tests)
```

The shell is self-contained: one `net10.0` class library + one executable entry assembly, plus a host `index.html` that boots Carbide, compiles the C# project (or loads a pre-built `CarbidePwsh.dll`), and calls `project.runInteractive({ terminal })`. No changes required to `@carbide/core` for Phase 1 or 2.

### 5.2 Component responsibilities

```
┌─────────────── xterm.js ───────────────┐
│           (browser / node TTY)         │
└──────────────────┬─────────────────────┘
                   │ bytes in/out
                   │
         ┌─────────▼──────────┐
         │    @carbide/core   │  ← existing: runtime boot, stdin/stdout bridge,
         │  (CarbideConsole)  │     SGR/cursor/key parsing, T3 System.Console
         └─────────┬──────────┘
                   │ in-process
                   │
      ┌────────────▼─────────────┐
      │   CarbidePwsh  (new)     │
      │  ┌────────────────────┐  │
      │  │   ShellHost (REPL) │  │  main loop: prompt → read line →
      │  └────────┬───────────┘  │  parse → evaluate → render
      │           │              │
      │  ┌────────▼───────────┐  │
      │  │   Parser → AST     │  │  hand-rolled recursive descent
      │  └────────┬───────────┘  │
      │           │              │
      │  ┌────────▼───────────┐  │
      │  │    Interpreter     │  │  tree walk; scope stack; coercion
      │  └────┬──────────┬────┘  │
      │       │          │       │
      │       │   ┌──────▼────┐  │
      │       │   │ TypeBridge│  │  reflection for [Type]::Member
      │       │   └───────────┘  │
      │       │                  │
      │  ┌────▼─────┐  ┌────────┐│
      │  │ Cmdlets  │◄─┤  VFS   ││
      │  └──────────┘  └────────┘│
      │                          │
      │  ┌──────────────────────┐│
      │  │    InvokeApp         ││  loads a Carbide-compiled DLL from
      │  │  (Phase 3)           ││  VFS or session reference → runs its
      │  └──────────────────────┘│  entry point in-process
      └──────────────────────────┘
```

The shell runs on the same dispatcher the rest of the Carbide-compiled program runs on — single-threaded, synchronous evaluator with `async`-friendly I/O points (the REPL `await`s on `Console.In.ReadLineAsync()` between submissions; cmdlets that read stdin do the same). This matches the runtime invariants the T2.1 investigation hardened in `packages/core/src/Terminal/CarbideSyncContext.cs`.

### 5.3 Parser

Hand-rolled recursive descent over a mutable `Lexer` position cursor. The parser builds our own AST (`PwshAst`), **not** PowerShell's `System.Management.Automation.Language.Ast` — we don't take a dependency on `lib/pwsh`.

Design choices:

- **No backtracking beyond one-token lookahead** for most productions. PowerShell's grammar has context-sensitive lexing (whether `>` is redirection or greater-than, whether `-match` is an operator or a parameter name), handled by threading an `ExpressionContext` / `CommandContext` flag through the lexer. This is the same technique `lib/pwsh`'s parser uses.
- **Statement-terminator sensitivity**: newlines end statements at the top level but not inside `(…)`, `@(…)`, `@{…}`, or after an explicit line-continuation backtick. The lexer emits `NewLine` tokens; the parser consumes or ignores them based on nesting depth.
- **Heredoc lexing**: `@"` / `@'` opens a multi-line mode; closing `"@` / `'@` must appear at column 0 of a line.
- **Type-literal parsing**: `[Generic[T,U]]` is handled by a nested type-argument parser; we accept fully-qualified .NET names (`System.Collections.Generic.List[int]`) plus the PowerShell aliases (`[int]`, `[string]`, `[pscustomobject]`, …) via a built-in alias table.

Indicative size: ~1.0–1.5k LOC for lexer + parser + AST definitions, tested with a corpus of ~200 input→AST fixtures.

### 5.4 Interpreter

Tree-walking evaluator. Each `PwshAst` node has an `Evaluate(EvalContext ctx)` method (or is dispatched through a visitor — bikeshed TBD). The evaluator returns `object?` (or `IEnumerable<object?>` for pipeline positions).

Key invariants:

- **No IL emission.** The evaluator uses ordinary method dispatch; there is no `Expression.Compile`, no `DynamicMethod`, no `AssemblyBuilder`, no DLR call-site caching. All work is tree walk + reflection (and reflection only for the `TypeBridge` and for `$obj.Property`/`$obj.Method()` dispatch).
- **Scope is a linked list of dictionaries.** Pushing a scope is cheap; variable lookup walks from innermost to outermost. Explicit scope qualifiers (`$script:`, `$global:`, `$env:`, `$function:`) route to the right ring directly.
- **Pipelines are enumerable composition.** A pipeline `A | B | C` builds an `IEnumerable<object?>` from A, each cmdlet implements `ProcessRecord(object? item)`; `B` and `C` consume the enumerable lazily. This is the same begin/process/end shape `PSCmdlet` uses.
- **Coercion rules are explicit and data-driven.** `Runtime/Coercion.cs` has one table that maps (source type, target type) → conversion strategy. Gaps throw `PwshCoercionException` rather than silently producing wrong values.

Indicative size: ~1.5–2k LOC for interpreter + scope + pipeline + coercion.

### 5.5 Type bridge (`[Type]::Member`)

Reflection-based, with a small allow-list policy.

- `[Type]` expressions resolve through a cache: alias table first (`[int]` → `System.Int32`), then full-name lookup against `AppDomain.CurrentDomain.GetAssemblies()`, then `Type.GetType(fqn, throwOnError: false)`.
- `::Member` access:
  - Properties → `PropertyInfo.GetValue`/`SetValue`.
  - Methods → resolve the best-matching overload via argument types (PowerShell's overload resolution is less strict than C#'s; we implement a simplified match-on-arity-then-assignability).
  - Fields → `FieldInfo.GetValue`/`SetValue`.
- Access policy:
  - **Allow-list by default**: an explicit set of types known to be Carbide-friendly (`System.Console` via the T3 fork, `System.Math`, `System.String`, `System.DateTime`, `System.TimeSpan`, `System.Convert`, `System.Text.Encoding`, `System.Text.StringBuilder`, `System.IO.Path` for pure-string work, `System.Text.Json.*`, `System.Threading.Tasks.Task`, `System.Environment` for its read-only surface, the generic collections, `System.Linq.Enumerable`, and the numeric primitives).
  - **Deny-list hard blocks**: types known to throw `PlatformNotSupportedException` across the board (`System.Threading.Thread` as a *constructor*, `System.Diagnostics.Process.Start`, `System.Net.Sockets.*`), with a deny reason surfaced as the shell-level error message.
  - **Anything else** is allowed with a one-time interactive warning ("this type is not on the Carbide-known-safe list; calls may throw"), escalatable via `$PwshPolicy::StrictTypes = $true` to make unknown types hard errors.

This is tighter than PowerShell's wide-open reflection stance but appropriate for a sandboxed shell, and easy to loosen if Carbide Contributors want.

Indicative size: ~400–700 LOC including the allow-list table and reflection cache.

### 5.6 Virtual filesystem

Described in detail in §6.

### 5.7 Cmdlet catalog

Each cmdlet is a small C# class deriving from a Carbide-specific base:

```csharp
public abstract class Cmdlet
{
    public virtual void Begin(CmdletContext ctx) { }
    public abstract IEnumerable<object?> Process(object? input, CmdletContext ctx);
    public virtual IEnumerable<object?> End(CmdletContext ctx) => [];
}
```

Indicative size: ~80–200 LOC per cmdlet; ~25 cmdlets at Phase 2 gate → ~2.5–4k LOC. Phase 3 adds `Invoke-App` and Phase 4 may add more.

### 5.8 Host / REPL

A direct descendant of `carbide-gh/src/Program.cs`:

```csharp
var shell = ShellHost.Create(vfs, typeBridge, cmdletRegistry);
shell.PrintBanner();
while (true)
{
    Console.Out.Write(shell.BuildPrompt());
    var line = await shell.ReadLineAsync();
    if (line is null) break;
    if (line is "exit" or "quit" or ":q") break;
    try
    {
        await shell.ExecuteAsync(line);
    }
    catch (PwshError e)
    {
        shell.WriteError(e);
        if (shell.Options.Verbose) shell.WriteException(e);
    }
}
```

For Phase 1, that's it: the shell is a line-per-submission REPL. Multi-line input (pending-parse state when the parser reports EOF mid-expression) lands in Phase 2 — on parse-incomplete, the prompt changes to `>> ` and accumulates input until the parser accepts.

## 6. Virtual filesystem

### 6.1 Data model

```
┌─────────────────────────────────────┐
│  VirtualFileSystem                  │
│  - root: VirtualDirectory           │
│  - currentLocation: VfsPath         │
│  - snapshot(): byte[] (JSON)        │
│  - restore(byte[]): void            │
└──────────────────┬──────────────────┘
                   │
         ┌─────────▼──────────┐
         │ VirtualDirectory   │
         │  - name            │
         │  - children: Dict  │  # name → VirtualNode
         │  - parent: weak    │
         │  - metadata        │
         └──────────┬─────────┘
                    │
        ┌───────────┴───────────┐
        │                       │
  ┌─────▼──────┐          ┌─────▼──────┐
  │VirtualFile │          │ VirtualDir │
  │ - content  │          │ - children │
  │ - encoding │          │ - …        │
  │ - metadata │          └────────────┘
  └────────────┘
```

Paths:

- Always normalized to forward-slashes internally; printed as `/` on display.
- Absolute paths start with `/`.
- `~` resolves to `/home/user` (arbitrary default).
- Virtual drives: Phase 1 ships a single root `/`. Phase 4 can add named drives (`work:`, `data:`) if useful; they map to subtrees.

Metadata:

- `CreationTimeUtc`, `LastWriteTimeUtc`, `Length`, `IsReadOnly`.
- No ACLs, no extended attributes — the sandbox doesn't model them.

### 6.2 Persistence

Three storage backends, pickable at host-init time:

| Backend | Where | Phase |
|---|---|---|
| `EphemeralVfsStore` | in-memory; discarded on session shutdown | 1 |
| `BrowserVfsStore` | `IndexedDB` object store keyed on origin + shell session id | 2 |
| `NodeVfsStore` | JSON snapshot in `~/.carbide/pwsh/vfs/<session-id>.json` | 2 |

All three backends implement one interface:

```csharp
public interface IVfsStore
{
    ValueTask<byte[]?> ReadSnapshotAsync();
    ValueTask WriteSnapshotAsync(byte[] snapshot);
}
```

Snapshot format: a JSON tree with `type: "dir" | "file"`, `name`, `children` / `content`. Binary file content is base64-encoded. For Node, the store can optionally shadow to a real OS directory — bi-directional sync `~/.carbide/pwsh/workspace/<session-id>/` ↔ the VFS — if Carbide Contributors want persistent editing in a real editor; this is a Phase 4 nice-to-have.

Note for browser: if Origin Private File System (OPFS) is available, we prefer it over IndexedDB for better fidelity and a native "Show in Finder / Files" export. OPFS has strong browser support by 2026 and a simpler API for tree-shaped storage.

### 6.3 FS cmdlets

Phase 2 delivers these backed by the VFS:

| Cmdlet | Surface |
|---|---|
| `Get-ChildItem [-Path] [-Recurse] [-Filter] [-File] [-Directory]` | enumerate children |
| `Get-Content [-Path] [-Raw] [-Encoding]` | read whole file or line-by-line |
| `Set-Content [-Path] [-Value] [-Encoding]` | write whole file |
| `Add-Content [-Path] [-Value]` | append |
| `Out-File [-Path] [-Append]` (via pipeline) | write incoming objects |
| `New-Item [-Path] [-ItemType File\|Directory] [-Value]` | create file or dir |
| `Remove-Item [-Path] [-Recurse] [-Force]` | delete |
| `Copy-Item [-Path] [-Destination] [-Recurse]` | copy |
| `Move-Item [-Path] [-Destination]` | move/rename |
| `Test-Path [-Path] [-PathType Any\|Leaf\|Container]` | exists? |
| `Resolve-Path [-Path]` | canonical absolute path |
| `Join-Path [-Path] [-ChildPath]` | path composition |
| `Set-Location [-Path]` (aliases: `cd`, `sl`) | change `$PWD` |
| `Get-Location` (alias: `pwd`) | read `$PWD` |
| `Push-Location` / `Pop-Location` | stack-based navigation |

All accept pipeline input for paths where PowerShell does.

### 6.4 Isolation guarantees

The shell *never* touches the real filesystem on behalf of user script code. The only real-disk I/O is the snapshot-persistence path (and even that is optional and opt-in). This is the sandbox promise: an untrusted script run in the shell cannot read `/etc/passwd` or enumerate `C:\Users\…`, because `Get-ChildItem /etc` simply sees the empty-or-populated VFS and nothing else.

Host policy escapes: if Carbide Contributors want a specific directory exposed (e.g. "map `/work` to my real `C:\source\repos\Tools\`"), the host provides an explicit `vfs.mount(realPath, virtualPath, readonly)` API at session-construction time. Not shipped by default; has to be explicit per session.

## 7. .NET interop

### 7.1 Expression form

```powershell
[System.Console]::BackgroundColor = 'DarkBlue'
[System.Console]::Clear()
[System.Math]::Sqrt(2)
[DateTime]::Now.AddDays(7).ToString('yyyy-MM-dd')
[System.IO.Path]::Combine('a', 'b', 'c')

# Instance members on values:
'hello world'.ToUpper()
'hello world'.Split(' ') | Select-Object -Last 1
$list = [System.Collections.Generic.List[int]]::new()
$list.Add(1); $list.Add(2); $list.Count
```

### 7.2 Resolution pipeline

1. Type resolution (`[Type]`):
   - Canonical alias table (`[int]` → `System.Int32`, `[string]` → `System.String`, etc.).
   - `Type.GetType(name, throwOnError: false)` scan across loaded assemblies.
   - A `[ref]`/`[void]`/`[pscustomobject]` block for special names.
   - Failures produce `CannotFindType` with a helpful hint (typos in namespace, missing assembly reference).
2. Member resolution (`::Member` or `.Member`):
   - For static: `type.GetMembers(BindingFlags.Public | BindingFlags.Static)` filtered by name.
   - For instance: `runtimeType.GetMembers(BindingFlags.Public | BindingFlags.Instance)` filtered by name.
   - Property read/write: simple; we invoke `GetValue`/`SetValue` with the right index args.
   - Method overload resolution: score each candidate by (a) arity match, (b) assignability of each argument's runtime type to the parameter type (with PowerShell coercion — strings to enums, numbers between widths, etc.). Pick the highest-scoring. Ties → first candidate; warnings can highlight ties in verbose mode.
3. Call:
   - Invoke via `MethodInfo.Invoke` / `PropertyInfo.SetValue`.
   - Wrap the result in the pipeline (methods that return `void` contribute nothing; methods that return `IEnumerable` are enumerable-flattened one level, matching PowerShell).
   - Exceptions from `MethodInfo.Invoke` are unwrapped once (stripping `TargetInvocationException`) and surfaced as `PwshRuntimeError`.

### 7.3 Why this is materially simpler than `lib/pwsh`'s path

`lib/pwsh` carries `PSObject`, `CoreAdapter`, `DotNetAdapter`, `ComAdapter`, and `PSMethodInvocationConstraints`; it has `CallSite<T>` caches, `DynamicMethod` fast paths, and DLR binders in `engine/runtime/Binding/*`. That machinery exists because the full engine wants to make 100-plus-call-a-second property access as fast as a virtual call. We don't have that performance constraint in a sandboxed interactive shell, and the `DynamicMethod` path throws `PlatformNotSupportedException` under Mono-WASM anyway — so the feasibility reports flagged it as a must-patch site. In the clean room we just skip the cache entirely and pay the reflection cost; for a shell, it's fine.

## 8. Running Carbide-compiled .NET console apps

### 8.1 Command dispatch order

When the shell sees a command name `Foo`, it resolves in this order:

1. Cmdlet registry (`Get-ChildItem`, `Where-Object`, …) → implement directly.
2. Function table (`function Foo { … }` defined in the current session) → invoke.
3. Script in VFS: `./Foo.ps1` / `Foo.ps1` on `$PSScriptPath` → parse & evaluate.
4. App in VFS: `./Foo.dll` / `Foo.dll` where `Foo` is a Carbide-compiled assembly with an entry point → invoke.
5. Registered session reference: an app handle previously attached via `session.addReference(bytes, "Foo")` and published as a shell command via host API.
6. Unresolved → `CommandNotFoundException` (no `NativeCommandProcessor` — we don't try to spawn a process).

### 8.2 Invocation

For (4) and (5):

```csharp
public sealed class InvokeAppCommand
{
    public int Run(byte[] peBytes, string[] args, IVirtualFileSystem vfs, IShellHost host)
    {
        var asm = Assembly.Load(peBytes);
        var entry = asm.EntryPoint
            ?? throw new PwshRuntimeError($"Assembly '{asm.GetName().Name}' has no entry point.");

        var saveIn = Console.In;
        var saveOut = Console.Out;
        var saveError = Console.Error;

        using var stdin = new ShellStdinReader(host);
        using var stdout = new ShellStdoutWriter(host);
        using var stderr = new ShellStdoutWriter(host, stderrStyle: true);
        Console.SetIn(stdin);
        Console.SetOut(stdout);
        Console.SetError(stderr);

        try
        {
            var parameters = entry.GetParameters();
            object? result = parameters.Length switch
            {
                0 => entry.Invoke(null, null),
                1 when parameters[0].ParameterType == typeof(string[]) => entry.Invoke(null, [args]),
                _ => throw new PwshRuntimeError("Unsupported entry-point signature."),
            };

            // async Task<int> Main returns Task<int>; unwrap.
            return result switch
            {
                null => 0,
                int i => i,
                Task<int> ti => ti.GetAwaiter().GetResult(),
                Task t => (t.GetAwaiter().GetResult(), 0).Item2,
                _ => 0,
            };
        }
        finally
        {
            Console.SetIn(saveIn);
            Console.SetOut(saveOut);
            Console.SetError(saveError);
        }
    }
}
```

Notes:

- We load the DLL **into the shell's own AppDomain/AssemblyLoadContext**. This is the simplest approach and matches how Carbide already loads user DLLs.
- Stdin/stdout/stderr swap: the invoked app writes to the same xterm surface the shell is bound to, but the app's lifetime doesn't clobber the shell's `CarbideConsole`-routed streams. This mirrors `ProjectCompiler.RunInteractiveAsync` in `@carbide/core`.
- Args: PowerShell-style arguments are marshalled to a `string[]` the same way a typical Main signature expects. Structured objects lose fidelity at this boundary — if we want richer composition, that's a Phase 4 design choice (probably via a dedicated `Invoke-CarbideApp` cmdlet that piping-proof-binds more cleanly).

### 8.3 Example

```powershell
# Register a Carbide-compiled app so the shell can invoke it by name.
Register-CarbideApp -Name 'wc' -Path './tools/wc.dll'

# Pipe to its stdin.
'hello world' | wc -l

# Or invoke directly from the VFS without registration.
./tools/greet.dll Ada
```

### 8.4 Relationship to the JS-interop bridge proposal

The `@carbide/js-interop-bridge` proposal ([carbide-js-interop-bridge-proposal](carbide-js-interop-bridge-proposal__2026-04-18__22-00-00-000000__a2d2955163d1.md)) imagines persistent JS-side handles onto Carbide-compiled C# objects. If/when that lands, the shell could accept "give me a live handle to the session object" from JS and let the JS host drive the shell through a data-plane API rather than only through xterm.js stdin. That's an orthogonal enhancement — this proposal does not depend on it.

## 9. Phasing

Work estimates are expressed as new-surface counts (LOC, files, cmdlets, fixtures) per `CLAUDE.md`'s "velocity-independent units" rule.

### Phase 1 — Expression evaluator (minimum viable)

**Deliverable:** a shell that evaluates expressions interactively — `2 + 2`, string interpolation, arrays, hashtables, `[System.Math]::Sqrt(2)`, `[System.Console]::ForegroundColor = 'Green'`. No cmdlets, no VFS, no pipelines.

**Indicative new surface:**
- 1 csproj, ~15 C# files under `Lexer/`, `Parser/`, `Parser/Ast/`, `Runtime/`, `Host/`.
- Parser + AST: ~1.0–1.5k LOC.
- Interpreter + scope + coercion: ~0.8–1.2k LOC.
- TypeBridge: ~0.4–0.7k LOC.
- REPL host: ~0.2k LOC.
- Tests: ~100 parser fixtures + ~60 interpreter end-to-end cases; ~0.8–1.5k LOC of test code.
- 1 `index.html` + 1 `serve.mjs` + 1 `smoke.mjs`, mirroring carbide-gh.

**Exit gate:** `2 + 2` prints `4` in xterm.js; `[System.Console]::BackgroundColor = 'DarkBlue'; [System.Console]::Clear()` visibly recolors and clears the buffer; a smoke test asserts both.

### Phase 2 — Pipelines + VFS + core cmdlets

**Deliverable:** the shell is actually useful. You can `cd /work`, `New-Item -ItemType File -Path foo.json -Value '{...}'`, read it back, pipe through `Where-Object` / `ForEach-Object` / `Select-Object`, serialize with `ConvertTo-Json`.

**Indicative new surface:**
- ~20 cmdlet files; ~2.5–4k LOC for cmdlet implementations.
- VFS: ~0.7–1.0k LOC (tree, path normalization, snapshot serialization).
- Pipeline runtime: ~0.4–0.6k LOC.
- Browser/Node VFS persistence: ~0.3–0.5k LOC.
- Tests: ~50 VFS-state fixtures + ~30 cmdlet-pipeline fixtures + ~20 integration scripts.
- Multi-line REPL: ~0.2k LOC for pending-parse state.

**Exit gate:** the smoke test runs a multi-command script end-to-end:

```powershell
Set-Location /tmp
@{ name = 'Ada'; langs = @('C#', 'PowerShell', 'TypeScript') } | ConvertTo-Json | Set-Content profile.json
Get-Content profile.json | ConvertFrom-Json | ForEach-Object { "Hello, $($_.name)!" }
# expected output: Hello, Ada!
```

### Phase 3 — Carbide-app invocation + scripts + error handling

**Deliverable:** scripts in the VFS run. Carbide-compiled DLLs with entry points run when invoked by name. `try`/`catch` / `throw` / `$ErrorActionPreference = 'Stop'` behave sensibly.

**Indicative new surface:**
- `Invoke-App` / `Register-CarbideApp` + command-dispatch extension: ~0.5–0.8k LOC.
- Script loader + dot-sourcing: ~0.3k LOC.
- Error object (`ErrorRecord`-shaped), `try`/`catch` filters, error-action preferences: ~0.4–0.6k LOC.
- Tests: ~20 script fixtures + ~15 invocation fixtures.

**Exit gate:** a small Carbide-compiled `.dll` (built separately, or inline via `carbide build --source`) is invokable from the shell by path; its `Main(string[] args)` runs, its stdout/stderr route to xterm, and its exit code surfaces as `$LASTEXITCODE`.

### Phase 4 — Stretch (optional)

Any of, subject to strategic value:

- Classes (`class Foo { … }`) and enums.
- `using module` + shell-defined modules.
- `Add-Type` wired to Carbide's own Roslyn — compile inline C# source to an assembly via `CarbideSession.createProject()` and reference it live.
- PSReadLine-style line editor (syntax highlighting, history, multiline-aware).
- `Invoke-RestMethod` / `Invoke-WebRequest` over the existing Mono-WASM fetch bridge.
- Host-exposed real-FS mount points (`vfs.mount(realPath, virtualPath, readonly)` as explicit, audited escape hatches).

## 10. Risks and open questions

| # | Risk / question | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | Our parser quietly diverges from PowerShell's grammar in edge cases (e.g. ambiguous `>` as redirection vs comparison, heredoc indentation rules) | High | Low | Build a fixture corpus early; snapshot-test AST shapes for each fixture; document divergences explicitly in README. |
| R2 | Pipeline semantics mismatch real PowerShell (e.g. implicit `ForEach-Object { … }` vs explicit) | Medium | Medium | Phase 2 runs the exit-gate script and a dozen hand-written scripts; diff stdout against real `pwsh` running the same script; document differences. |
| R3 | Type-coercion rules differ enough from PowerShell that real scripts produce wrong results silently | Medium | Medium | Coercion-table approach (§5.4) makes the rules data-driven; test against a corpus of `(value, targetType, expectedResult)` tuples borrowed from the PowerShell spec. |
| R4 | Reflection-based member resolution is too slow in a tight loop | Low | Low | Shell-scale workloads rarely hit this. If profiling surfaces a bottleneck, add a per-type member cache (a `Dictionary<Type, IReadOnlyList<MemberInfo>>`); not a redesign. |
| R5 | Mono-WASM rejects our reflection patterns for trimming or other reasons | Low | Medium | We run the shell as a normal Carbide-compiled C# app with trimming off (Carbide already ships this way); the same reflection patterns that work in carbide-gh's Spectre.Console invocation work for us. |
| R6 | The shell gets used enough that people try to run real `lib/pwsh` scripts with heavy module imports | Medium | Low | Documentation & scope discipline. README leads with "this is a PowerShell-flavored shell, not pwsh.exe" and lists what works. |
| R7 | The VFS becomes a coupling point — tests depend on exact VFS shape, making future rework painful | Medium | Medium | Keep `VirtualFileSystem` behind an interface; tests hit the interface, not the concrete type. |
| R8 | Command-dispatch precedence order confuses users (cmdlet shadows user script of same name, say) | Medium | Low | Document the order (§8.1) prominently; add a `Get-Command -Name Foo` diagnostic that prints the resolution path. |
| R9 | `[Type]::Member` typo errors are bad enough to derail users (long error messages, no "did you mean…") | Medium | Low | Shell-level error formatter trims `System.*` namespaces, shows top-3 nearest public member names via Levenshtein, highlights the specific argument where overload resolution failed. Same approach as `Microsoft.PowerShell.Commands.GetMemberCommand`'s error reporting. |
| R10 | Interactive features (especially execution-time Ctrl+C, multiline-aware history, and semantic completion depth) remain underdeveloped | High | Medium | The lightweight prompt editor can ship early with line-abandon Ctrl+C, session-local history, and command-name completion. Treat richer history/search/completion and true execution-time interrupt semantics as a follow-up plan instead of overloading the initial shell milestones. |
| R11 | The shell needs to load a user DLL that transitively references a non-allowlisted NuGet package | Medium | Medium | Carbide's existing reference-registry story handles loading arbitrary DLL bytes; the allow-list is a NuGet-resolver concept. DLLs loaded from VFS aren't subject to it. |

**Open questions** (to resolve during Phase 1):

- Q1: Which `$PSVersionTable` value do we report? Suggest `{ PSVersion = '7.5-carbide-subset', CarbideVersion = '<from core>' }`.
- Q2: Do we auto-import a tiny profile script from a well-known VFS path on startup? Suggest `$HOME/.profile.ps1` if it exists, off by default in Phase 1.
- Q3: How do we surface diagnostics for a VFS-snapshot mismatch (e.g. stored snapshot is newer than our current schema)? Suggest a "migrate or discard" prompt at session init.
- Q4: Should the shell be packaged as a pre-built DLL (faster boot) or always built-from-source by Carbide (eats own dog food)? Suggest **pre-built by default for the browser demo, build-from-source in CI and for development**, mirroring how carbide-gh vendors Spectre.Console.dll rather than compiling Spectre from source.

## 11. Relationship to prior research

The two feasibility reports:

1. [`docs/research/pwsh/carbide-pwsh-subset-feasibility__2026-04-19__10-30-00-000000__a7c3d4e9f1b2.md`](../research/pwsh/carbide-pwsh-subset-feasibility__2026-04-19__10-30-00-000000__a7c3d4e9f1b2.md) — concluded "feasible" for a `lib/pwsh` fork, with a revised effort estimate of ~8–15k LOC after independent review.
2. [`docs/research/powershell/carbide-powershell-subset-feasibility__2026-04-19__20-23-22-238572__8b6d83c519ba.md`](../research/powershell/carbide-powershell-subset-feasibility__2026-04-19__20-23-22-238572__8b6d83c519ba.md) — independent review; flagged "medium-to-large subproject, not a spike" framing and the two-feasibility-questions split (runtime-hosting vs source-build).

This proposal takes a different path than either report recommended, because Carbide Contributors' "need not be a direct port" direction retires most of what made those reports cautious. Specifically, the clean-room approach drops every one of the following concerns:

- `Expression.Compile()` audit across 23 sites in 13 files → gone (no IL emission anywhere in the clean-room interpreter).
- `CoreAdapter.DynamicMethod` + `EventManager.AssemblyBuilder` patches → gone (we don't use them).
- Threading-model collapse → gone (single-threaded by default).
- `InitialSessionState.CreateDefault2` curation → gone (we build our session state from scratch, never needing to subtract).
- `NativeCommandProcessor` refusal → gone (command dispatch is ours; we never attempt process spawning).
- `net9.0` vs `net10.0` mismatch → gone (we target `net10.0` from day one).
- Trimming + reflection surface audit → simpler (the reflection we do is for `[Type]::Member` only, easy to annotate with `DynamicDependency` if trimming gets turned on).
- Upstream-sync tax → gone (we don't track `lib/pwsh` upstream; we're a separate project).

What survives from the feasibility work is the *scope discipline*: what's in/out (§3), which cmdlets are useful (§5.7), and the "sandboxed shell, not pwsh.exe" framing. Both reports were right about that.

## 12. Carbide-side prerequisites

Phase 1 and 2 require **no changes** to `@carbide/core`. The shell is a normal Carbide-compiled C# app that uses already-shipped APIs: `await Console.In.ReadLineAsync()` (T2.1), `Console.ForegroundColor`/`Console.SetCursorPosition`/etc. (T3 forked `System.Console.dll`), and the streaming stdout writer that xterm.js consumes.

Phase 3 (invoking Carbide-compiled apps as commands) adds one optional host-surface cmdlet that's useful but not required:

- `Register-CarbideApp -Name <string> -Handle <ReferenceHandle>` needs the `ReferenceHandle` value to be plumbed into the shell from JS via a host-bridge object. That plumbing is a ~50–100-LOC addition to `CompilationInterop.cs` (a new JSExport method like `getReferenceBytes(handle)`) or, alternatively, can be done entirely inside the shell by reading DLLs from the VFS — no Carbide changes required.

Phase 4 (if pursued) may want:

- An `Add-Type` integration that calls into `CarbideSession.createProject()`, `project.addSource(…)`, `project.build()`. Requires the `@carbide/js-interop-bridge` to land or the shell to be given a Carbide session handle at construction time. Plausibly ~200-LOC Carbide-side surface.
- Live stdout streaming (already shipped in core via `StreamingStdOutWriter`) — already available.

## 13. Success criteria

A minimum-useful state looks like:

- `carbide-pwsh` is buildable and runnable via `node packages/carbide-pwsh/scripts/serve.mjs` and a browser tab.
- The banner prints, a prompt shows, `2 + 2` echoes `4`.
- `cd /tmp; New-Item -ItemType File foo.txt -Value 'hi'; Get-Content foo.txt` works.
- `@(1,2,3,4,5) | Where-Object { $_ -gt 2 } | ForEach-Object { $_ * 2 }` outputs `6`, `8`, `10`.
- `[System.Console]::BackgroundColor = 'DarkBlue'; [System.Console]::Clear()` visibly affects the xterm buffer.
- `[System.Text.Json.JsonSerializer]::Serialize(@{a=1;b=2})` returns `{"a":1,"b":2}`.
- A small Carbide-compiled DLL (e.g. an `echo` app) invoked by path prints its arguments to the shell.
- A headless smoke test runs a five-line script end-to-end and asserts expected output.

Each of those is testable; each corresponds to a phase exit gate.

## 14. Appendices

### 14.1 Example shell sessions

```powershell
PS /> 2 + 2
4
PS /> "hello, $env:USER"
hello, contributor
PS /> [System.Math]::PI * 2
6.283185307179586
PS /> [System.Console]::ForegroundColor = 'Green'; "world"; [System.Console]::ResetColor()
world
PS /> @(1,2,3) | ForEach-Object { $_ * $_ }
1
4
9
PS /> Set-Location /work
PS /work> New-Item -ItemType Directory logs
PS /work> New-Item -ItemType File logs/build.log -Value 'compiled ok'
PS /work> Get-ChildItem -Recurse
    Directory: /work
Mode  LastWriteTime   Length Name
----  -------------   ------ ----
d---  2026-04-21T21:34      logs
    Directory: /work/logs
Mode  LastWriteTime   Length Name
----  -------------   ------ ----
----  2026-04-21T21:34     11 build.log
PS /work> Get-Content logs/build.log
compiled ok
PS /work> $data = @{ items = @(1,2,3); name = 'demo' }
PS /work> $data | ConvertTo-Json -Depth 3
{
  "items": [ 1, 2, 3 ],
  "name": "demo"
}
PS /work> ./hello.dll Ada
hello, Ada!
PS /work> exit
```

### 14.2 Why not reuse `Microsoft.CodeAnalysis.Scripting`?

Roslyn scripting evaluates C#, not PowerShell. It'd give us C# expression evaluation for free but none of the shell idioms (pipelines, cmdlets, VFS-backed commands). The shell needs a PowerShell-shaped surface, and PowerShell's expression grammar has enough differences from C# (`$var`, `-eq`, `@{}`, pipelines, type literals, dash-prefixed parameters) that layering a PowerShell parser on top of Roslyn eval buys nothing over writing the interpreter directly. The `TypeBridge` for `[Type]::Member` access is far smaller than what Roslyn needs.

### 14.3 Why a tree-walking interpreter (not bytecode/IR)?

Three reasons:

1. **Simplicity** — tree-walking is the shortest path from parser output to working semantics.
2. **Mono-WASM fit** — no IL emission required; we avoid the entire `Expression.Compile` / `DynamicMethod` / `AssemblyBuilder` minefield the feasibility reports mapped.
3. **Scale** — shell-scale workloads rarely demand micro-optimized hot paths. If profiling ever shows one, we can add targeted fast paths (e.g. a member-access cache, or a small IR for hot functions) as opt-in enhancements.

### 14.4 File references

- Carbide current-state: [`docs/Carbide-Current-State-Guide.md`](../Carbide-Current-State-Guide.md)
- T3 System.Console fork: [`packages/core/src/Terminal/CarbideConsole.cs`](../../packages/core/src/Terminal/CarbideConsole.cs), [`packages/core/src/Terminal/CarbideTerminalInterop.cs`](../../packages/core/src/Terminal/CarbideTerminalInterop.cs)
- carbide-gh demo (reference implementation for host pattern): [`packages/carbide-gh/`](../../packages/carbide-gh/)
- JS-interop bridge proposal: [`carbide-js-interop-bridge-proposal__2026-04-18__22-00-00-000000__a2d2955163d1.md`](carbide-js-interop-bridge-proposal__2026-04-18__22-00-00-000000__a2d2955163d1.md)
- Feasibility reports (prior art, partially superseded by this proposal's clean-room direction):
  - [`docs/research/pwsh/carbide-pwsh-subset-feasibility__2026-04-19__10-30-00-000000__a7c3d4e9f1b2.md`](../research/pwsh/carbide-pwsh-subset-feasibility__2026-04-19__10-30-00-000000__a7c3d4e9f1b2.md)
  - [`docs/research/powershell/carbide-powershell-subset-feasibility__2026-04-19__20-23-22-238572__8b6d83c519ba.md`](../research/powershell/carbide-powershell-subset-feasibility__2026-04-19__20-23-22-238572__8b6d83c519ba.md)

### 14.5 Not committed by this proposal

- A final name. `carbide-pwsh` is the working title; `nutshell`, `carbidsh`, `ps-lite`, or something else entirely are all fine. Decide at Phase 1 kickoff.
- The exact cmdlet catalog boundary at each phase — this proposal lists a working set, but the final Phase 2 cmdlet list should be reviewed during implementation.
- Whether to depend on `Newtonsoft.Json` or stay on `System.Text.Json` for `ConvertTo-Json`. Suggest `System.Text.Json` (already in BCL, smaller runtime footprint); revisit if fidelity issues surface against real-PowerShell JSON output.
