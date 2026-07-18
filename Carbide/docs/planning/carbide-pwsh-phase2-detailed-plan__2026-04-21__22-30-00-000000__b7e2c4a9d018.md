# Carbide-pwsh — Phase 2 detailed plan (pipelines, VFS, core cmdlets)

- Created (UTC): 2026-04-21T22:30:00Z
- Repository HEAD: ad9a5ea93897117cd90e2e6e36142bc90927cea2
- Status: detailed implementation plan for **Phase 2** of the [carbide-pwsh-subset-shell proposal](../proposals/carbide-pwsh-subset-shell-proposal__2026-04-21__21-30-00-000000__e9c4b27a8f13.md)
- Audience: Carbide Contributors; future Carbide contributors

## 1. Purpose

Phase 1 landed an expression evaluator. Phase 2 turns it into an **actually useful shell**:

1. Pipelines — `a | b | c` streams output between cmdlets.
2. Command invocation syntax — `Get-ChildItem -Path '/tmp' -Recurse`.
3. A curated cmdlet catalog — ~25 cmdlets covering pipeline shaping, formatting, JSON, and filesystem.
4. A virtualized filesystem backing `Set-Location`, `Get-Content`, `Set-Content`, and friends.
5. Script blocks — `{ $_ * 2 }` used as `Where-Object`/`ForEach-Object` arguments.
6. Multi-line REPL — when parse is incomplete, the prompt becomes `>> ` and input accumulates.

Phase 2 still excludes: control flow (`if`, `while`, `for`, `foreach`, `switch`), functions, error handling (`try`/`catch`/`throw`), regex operators (`-match`), format/join/split operators, containment operators (`-contains`, `-in`), running Carbide-compiled apps as commands, tab completion, history, and real-FS mount points. Those land in Phase 3+.

## 2. Exit gate

The smoke-test script the proposal committed to:

```powershell
Set-Location /tmp
@{ name = 'Ada'; langs = @('C#', 'PowerShell', 'TypeScript') } | ConvertTo-Json | Set-Content profile.json
Get-Content profile.json | ConvertFrom-Json | ForEach-Object { "Hello, $($_.name)!" }
# expected final line: Hello, Ada!
```

Plus the Phase 1 exit gate must still pass (no regressions on expression evaluation). Plus these additional interactive checks:

| Input | Expected |
|---|---|
| `Get-ChildItem /` (empty VFS) | empty output, exit 0 |
| `New-Item -ItemType Directory /work; Set-Location /work; Get-Location` | `/work` |
| `@(5,3,1,4,2) \| Sort-Object` | `1`, `2`, `3`, `4`, `5` |
| `@(1,2,3,4,5) \| Where-Object { $_ -gt 2 }` | `3`, `4`, `5` |
| `@(1,2,3) \| ForEach-Object { $_ * $_ }` | `1`, `4`, `9` |
| `@(1,2,3) \| Measure-Object -Sum` | object with `Sum = 6` |
| `'foo', 'bar', 'foo' \| Group-Object` | two groups, counts 2 and 1 |
| `@(1..3) \| Select-Object -First 2` | `1`, `2` |
| `@{ a = 1; b = @(2,3) } \| ConvertTo-Json` | valid JSON |
| `'{"x":1}' \| ConvertFrom-Json` | object with `.x = 1` |
| `Write-Host 'hello' -ForegroundColor Cyan` | cyan-colored "hello" |
| multi-line `@(1,2<Enter>3)` | prompt `>> ` then evaluation on close |

Plus: full xUnit suite green (target ~250+ tests total across the phase).

## 3. Language surface additions

### 3.1 Pipelines

`|` is lexed as a new `Pipe` token. Syntactically, a statement is now:

```
statement        ::= assignment | pipeline
pipeline         ::= pipelineStage ( '|' pipelineStage )*
pipelineStage    ::= command | expression
```

Each `pipelineStage` receives the previous stage's output (as an `IEnumerable<object?>`) and yields its own output. The first stage receives no input.

### 3.2 Command invocation

A **command** is an identifier (possibly hyphenated) followed by zero or more arguments until end-of-statement, `|`, `;`, or newline. Arguments are:

- `-Name value` — named parameter with value.
- `-Name` alone — switch parameter (true).
- `-Name:value` — alternative named-parameter syntax (Phase 2 deferred; not required for exit gate).
- positional expression: `$var`, literal, `(expr)`, `@(...)`, `@{...}`, `{ scriptblock }`.
- **bare word**: a sequence of adjacent non-whitespace tokens that would otherwise not parse as an expression (e.g. `/tmp`, `foo.json`, `./script.ps1`). Interpreted as a string literal.

### 3.3 Command-vs-expression disambiguation

At statement start, the parser decides:

- If the first token is `Identifier` and it isn't immediately followed by `=`, `+=`, `-=`, etc., **or by `(`** — parse as **command**.
- Otherwise — parse as **expression-or-assignment**.

Rationale: `Get-ChildItem` is a command; `$x = 5` is an assignment; `(2+3)` is an expression; `@(1)` is an expression; `'foo'.ToUpper()` is an expression.

`Identifier(…)` is special-cased as **method-call-ish** (e.g. `MyFunction(1,2)` if we had functions) — but Phase 2 doesn't ship user functions, so this path simply emits a parse error telling the user to use command syntax. We'll revisit in Phase 3.

### 3.4 Script blocks

`{ statements }` at an expression/argument position produces a `ScriptBlockAst`. When the interpreter evaluates a `ScriptBlockAst`, it wraps the AST in a `ScriptBlock` runtime object that closes over the current scope. Cmdlets like `Where-Object` and `ForEach-Object` invoke the script block with a per-item `$_` binding.

### 3.5 Member access on pipeline result

`$pwd.Path` is member access on the result of `$pwd`. Already supported by Phase 1.

### 3.6 Parse-incomplete detection

A new `PwshIncompleteInputException` is thrown when the parser reaches EOF inside a grouping (`(`, `@(`, `@{`, `{`, `$(`, `"`, `'`, heredoc). The REPL catches this to drive multi-line mode.

## 4. Architecture additions

### 4.1 New directories / files

```
src/Carbide/packages/carbide-pwsh/src/
├── Cmdlets/                                       NEW
│   ├── Cmdlet.cs                                  base class + CmdletContext
│   ├── CmdletRegistry.cs                          name → factory lookup
│   ├── ParameterBinder.cs                         positional + named binding
│   ├── Pipeline.cs                                pipeline execution
│   ├── Output/
│   │   ├── WriteOutputCommand.cs
│   │   ├── WriteHostCommand.cs
│   │   ├── WriteErrorCommand.cs
│   │   ├── OutStringCommand.cs
│   │   └── ReadHostCommand.cs
│   ├── Shape/
│   │   ├── WhereObjectCommand.cs
│   │   ├── ForEachObjectCommand.cs
│   │   ├── SelectObjectCommand.cs
│   │   ├── SortObjectCommand.cs
│   │   ├── GroupObjectCommand.cs
│   │   └── MeasureObjectCommand.cs
│   ├── Json/
│   │   ├── ConvertToJsonCommand.cs
│   │   └── ConvertFromJsonCommand.cs
│   └── Fs/
│       ├── GetChildItemCommand.cs
│       ├── GetContentCommand.cs
│       ├── SetContentCommand.cs
│       ├── AddContentCommand.cs
│       ├── NewItemCommand.cs
│       ├── RemoveItemCommand.cs
│       ├── TestPathCommand.cs
│       ├── SetLocationCommand.cs
│       ├── GetLocationCommand.cs
│       ├── ResolvePathCommand.cs
│       ├── JoinPathCommand.cs
│       ├── CopyItemCommand.cs
│       └── MoveItemCommand.cs
└── Vfs/                                           NEW
    ├── VfsNode.cs                                 abstract node + VfsDirectory + VfsFile
    ├── VirtualFileSystem.cs                       top-level facade
    ├── VfsPath.cs                                 path normalization, resolution, join
    └── VfsSnapshot.cs                             JSON snapshot round-trip
```

### 4.2 Cmdlet shape

```csharp
public abstract class Cmdlet
{
    public abstract string Name { get; }

    // One call = one pipeline stage. `input` is the upstream stage's output (null for the
    // first stage). The cmdlet returns its output as an enumerable; downstream stages iterate
    // it lazily.
    public abstract IEnumerable<object?> Invoke(
        IEnumerable<object?>? input,
        ParameterBinding binding,
        CmdletContext context);
}

public sealed class CmdletContext
{
    public Interpreter Interpreter { get; }
    public VirtualFileSystem Vfs { get; }
    public Scope Scope => Interpreter.Scope;
    public TextWriter Output { get; }
    public TextWriter Error { get; }
}

public sealed class ParameterBinding
{
    public IReadOnlyList<object?> Positional { get; }
    public IReadOnlyDictionary<string, object?> Named { get; } // case-insensitive
    public bool HasSwitch(string name) => Named.TryGetValue(name, out var v) && v is true;
    public T? GetOrDefault<T>(string name, T? fallback) { ... }
    public bool TryGetPositional(int index, out object? value) { ... }
}
```

No `begin`/`process`/`end` split in Phase 2 — each cmdlet sees the whole input enumerable and yields whatever it wants. Lazy cmdlets (`Where-Object`, `ForEach-Object`, `Select-Object`) yield per-item; eager cmdlets (`Sort-Object`, `Measure-Object`, `Group-Object`) consume then yield.

### 4.3 VFS shape

```csharp
public abstract class VfsNode
{
    public string Name { get; internal set; } = "";
    public VfsDirectory? Parent { get; internal set; }
    public DateTime CreationTimeUtc { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public string AbsolutePath { get; }   // derived by walking Parent chain
    public bool IsDirectory => this is VfsDirectory;
    public bool IsFile => this is VfsFile;
}

public sealed class VfsDirectory : VfsNode
{
    public IDictionary<string, VfsNode> Children { get; }  // case-insensitive
}

public sealed class VfsFile : VfsNode
{
    public byte[] Content { get; set; }
    public string Encoding { get; set; } = "utf-8";
    public long Length => Content.Length;
    public string ReadText();
    public void WriteText(string text);
}

public sealed class VirtualFileSystem
{
    public VfsDirectory Root { get; }
    public string CurrentLocation { get; set; }   // absolute path

    public VfsNode? Resolve(string path);
    public VfsNode GetRequired(string path);
    public IEnumerable<VfsNode> List(string path, bool recursive, string? filter);
    public VfsFile CreateFile(string path, byte[] content, bool overwrite);
    public VfsDirectory CreateDirectory(string path);
    public void Delete(string path, bool recursive);
    public void Copy(string src, string dst, bool recursive);
    public void Move(string src, string dst);
    public bool Exists(string path);
    public string NormalizePath(string path);     // resolves `.`, `..`, relative → absolute
    public string JoinPath(string parent, string child);

    public string SaveSnapshot();                 // JSON
    public void LoadSnapshot(string json);
}
```

Persistence for Phase 2 is in-memory + `SaveSnapshot`/`LoadSnapshot` for JSON round-trips. The ShellHost wires it into the REPL lifecycle; the browser/Node-specific storage backends are Phase 2.1.

### 4.4 Pipeline evaluation

```csharp
public static class Pipeline
{
    public static object? Run(PipelineAst ast, CmdletContext ctx)
    {
        IEnumerable<object?>? input = null;
        foreach (var stage in ast.Stages)
        {
            input = RunStage(stage, input, ctx);
        }
        // Materialize the final enumerable so downstream code (REPL rendering) can inspect.
        if (input == null) return null;
        var results = input.ToList();
        return results.Count switch {
            0 => null,
            1 => results[0],
            _ => results.ToArray(),
        };
    }
}
```

## 5. Execution order

### 2.1 Lexer extensions
- Add `Pipe` token (`|`).
- Fold hyphenated command names: after an `Identifier`, if followed immediately (no whitespace) by `-Identifier` and `-Word` is NOT a known dashed operator, consume into one `Identifier`. Enables `Get-ChildItem`, `ConvertTo-Json` to lex as single tokens.
- Add one-shot `LabelColonSafe` handling so `$env:PATH` still works (already works in Phase 1).

### 2.2 Parser extensions
- Add `PipelineAst`, `CommandAst`, `CommandParameterAst`, `CommandArgumentAst`, `ScriptBlockAst`.
- Statement-start mode decision: command vs expression/assignment.
- Command parsing: command name → args loop → stop on EOL/semicolon/pipe.
- Bare-word synthesis: if current token is not an expression-start and is adjacent to non-whitespace tokens, consume until whitespace/delimiter and build a `StringLiteralAst`.
- Parse-incomplete detection: throw `PwshIncompleteInputException` on EOF inside a grouping.

### 2.3 VFS layer
- `VfsNode`, `VfsFile`, `VfsDirectory`, `VfsPath`, `VirtualFileSystem`, `VfsSnapshot`.
- Path normalization (`/`, `~`, `.`, `..`, relative resolution).
- Tests: 30–50 fixtures.

### 2.4 Cmdlet base + pipeline runtime
- `Cmdlet`, `ParameterBinding`, `CmdletContext`, `CmdletRegistry`.
- `Pipeline.Run` that threads stages.
- Interpreter wires pipeline evaluation to cmdlet dispatch.

### 2.5 Cmdlet catalog
- Pipeline-shaping: `Where-Object`, `ForEach-Object`, `Select-Object`, `Sort-Object`, `Group-Object`, `Measure-Object`.
- Output: `Write-Output`, `Write-Host` (color), `Write-Error`, `Out-String`, `Read-Host`.
- JSON: `ConvertTo-Json`, `ConvertFrom-Json`.
- FS: `Get-ChildItem` (+ `dir`, `ls` aliases), `Get-Content` (`cat`), `Set-Content`, `Add-Content`, `New-Item`, `Remove-Item` (`rm`, `del`), `Test-Path`, `Set-Location` (`cd`, `sl`), `Get-Location` (`pwd`, `gl`), `Resolve-Path`, `Join-Path`, `Copy-Item` (`cp`, `copy`), `Move-Item` (`mv`, `move`).

### 2.6 Multi-line REPL
- Program.cs catches `PwshIncompleteInputException`, switches prompt to `>> `, appends next line and retries.
- Ctrl+C to abandon a multi-line buffer (defer — Phase 2 uses a blank-line break instead).

### 2.7 Tests
- `VfsTests.cs` (path normalization, create/delete/copy/move, snapshot round-trip).
- `CmdletTests.cs` (each cmdlet: happy path + at least one edge).
- `PipelineTests.cs` (pipelines with 2/3 stages).
- `IntegrationTests.cs` (the full exit-gate script).
- Extend `ParserTests.cs` with pipelines, commands, script blocks.

### 2.8 Host wiring
- `ShellHost` now owns a `VirtualFileSystem` and a `CmdletRegistry`.
- `$PWD` variable is bound to `vfs.CurrentLocation` on read.
- Interpreter consults registry when evaluating a `CommandAst`.

## 6. Design decisions

### D1 — Pipelines are synchronous enumerables

Each stage is a C# method returning `IEnumerable<object?>`. Downstream enumeration drives upstream production lazily. This is simpler than true async streams, cheaper than coroutines, and fine for shell-scale workloads.

### D2 — Script blocks close over the live interpreter scope

Phase 2 has one scope. A `ScriptBlock` captures a reference to the `Interpreter` and sets `$_` before each invocation. Phase 3 (when functions introduce scope stacks) will revise this to capture a scope chain.

### D3 — Parameter binding is manual per cmdlet

Each cmdlet receives a `ParameterBinding` and calls `binding.GetOrDefault<T>("Path", defaultValue)` to pull typed parameter values. No reflection-based attribute binding yet — that's ~100 LOC we don't need in Phase 2 and clarifies each cmdlet's intent.

### D4 — Bare-word parsing lives in the parser, not the lexer

The lexer stays context-free. The parser, when in command-arg mode, detects "this isn't an expression-start token" and consumes adjacent tokens until whitespace or a command-delimiting token. The resulting string becomes a `StringLiteralAst`.

### D5 — VFS is case-insensitive on all platforms

PowerShell on Windows is case-insensitive for FS; on Linux it's case-sensitive. Phase 2 picks **case-insensitive** uniformly for simplicity. Dedicated case-sensitivity policy is a Phase 4 option.

### D6 — No I/O provider layer

Phase 2 hard-codes "FS cmdlets operate on the VFS". Phase 3+ can introduce a provider abstraction (Variable/Function/Env providers, etc.) once cmdlets genuinely need them.

### D7 — `Write-Host` uses ANSI codes, not `[Console]::ForegroundColor`

Writing via ANSI SGR is simpler and doesn't leave console state between invocations. The T3 System.Console fork already understands our SGR output through `Console.Out`, so this is a no-op architectural choice.

### D8 — `Get-ChildItem` and friends emit `VfsNode`-derived values

The formatter renders them as a table. That lets `ls | Where-Object { $_.Name -like 'foo*' }` work (when Phase 2.1 adds `-like`; Phase 2 uses `$_.Name -eq 'foo'` or member access on `$_`).

### D9 — Bare words don't interpolate

A bare word like `/tmp` or `foo.json` is a literal string, not an interpolating one. If users want interpolation, they quote.

## 7. Risks

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | Command-vs-expression disambiguation breaks Phase 1 inputs (e.g. `2 + 2` mis-parsed as command `2`) | Medium | High | Rule in §3.3: only `Identifier` at statement-start triggers command mode. Numbers/strings/variables/brackets/parens all stay expression mode. Phase 1 xUnit tests enforce this. |
| R2 | Bare-word synthesis mis-combines tokens | Medium | Medium | Adjacency check uses source offsets; extensive fixtures over path-like args. |
| R3 | Script-block closure captures wrong scope at pipeline time | Medium | Medium | Tests capture-variant behavior; Phase 3 will revisit with real scope stacks. |
| R4 | VFS path normalization differs from PowerShell on edge cases (trailing `/`, case, `..` past root) | Medium | Low | Tests pin the behavior we want; differences are documented in README. |
| R5 | Cmdlet parameter binding diverges from real PowerShell (ambiguous names, partial matches) | Low | Low | Phase 2 matches exact parameter names. Partial-match is a Phase 4 nicety. |
| R6 | JSON round-trip loses `OrderedDictionary` ordering | Medium | Low | `System.Text.Json` preserves order by default; tests pin this. |
| R7 | Pipeline enumerating twice (once for count, once for data) causes re-invocation of a script block | Medium | Medium | `Pipeline.Run` materializes once at the end; cmdlets that need to enumerate twice must `.ToArray()` their input. |
| R8 | Multi-line REPL can't tell "user wants to abort" apart from "keep typing" | Medium | Low | Blank line inside multi-line mode cancels; document it prominently. |

## 8. Out of scope (for Phase 2)

- Control flow (`if`, `while`, `for`, `foreach`, `switch`).
- User-defined functions / `param(...)` blocks.
- Error handling: `try`, `catch`, `finally`, `throw`, `$ErrorActionPreference`.
- Regex operators (`-match`, `-notmatch`, `-replace`, `-like`, `-notlike`).
- Format, join, split operators (`-f`, `-join`, `-split`).
- Containment operators (`-contains`, `-notcontains`, `-in`, `-notin`).
- Running Carbide-compiled apps as commands (Phase 3).
- `Add-Type`, binary module loading.
- PSReadLine-style line editor, history, completion.
- Concrete browser/Node VFS persistence backends (Phase 2.1 — snapshot API is ready; wiring is deferred).

## 9. File-by-file deliverables

```
src/Shell/
  Lexer/Lexer.cs                          +40 LOC (hyphen-folding, Pipe)
  Lexer/TokenKind.cs                      +1 entry (Pipe)
  Parser/Parser.cs                        +400 LOC (pipelines, commands, script blocks, bare words, incomplete-input)
  Parser/Ast/AstNodes.cs                  +120 LOC (PipelineAst, CommandAst, CommandArgumentAst, CommandParameterAst, ScriptBlockAst)
  Errors/PwshException.cs                 +15 LOC (PwshIncompleteInputException)
  Runtime/Interpreter.cs                  +150 LOC (pipeline + command dispatch, script-block wrapping)
  Runtime/ScriptBlock.cs                  +50 LOC (new)
  Host/ShellHost.cs                       +60 LOC (owns Vfs + CmdletRegistry)
  Host/OutputFormatter.cs                 +50 LOC (renders VfsNode rows)
  Program.cs                              +30 LOC (multi-line REPL)

src/Vfs/                                  ~900 LOC new
  VfsNode.cs                              ~100
  VirtualFileSystem.cs                    ~450
  VfsPath.cs                              ~200
  VfsSnapshot.cs                          ~150

src/Cmdlets/                              ~3000 LOC new
  Cmdlet.cs + ParameterBinder.cs          ~200
  CmdletRegistry.cs                       ~100
  Pipeline.cs                             ~120
  Output/*                                ~250
  Shape/*                                 ~400
  Json/*                                  ~250
  Fs/*                                    ~1700

test/
  VfsTests.cs                             ~250
  CmdletTests.cs                          ~500
  PipelineTests.cs                        ~200
  IntegrationTests.cs                     ~120
  ParserTests.cs extensions               ~150
```

Rough total: ~5.5k LOC of new/modified production + test code.

## 10. Links

- [Parent proposal](../proposals/carbide-pwsh-subset-shell-proposal__2026-04-21__21-30-00-000000__e9c4b27a8f13.md)
- [Phase 1 plan](carbide-pwsh-phase1-detailed-plan__2026-04-21__21-45-00-000000__a5f8c3d192e0.md)
- [Phase 1 package](../../packages/carbide-pwsh/)
- [PowerShell Language Specification §7 Operators](https://learn.microsoft.com/en-us/powershell/scripting/lang-spec/chapter-07)
