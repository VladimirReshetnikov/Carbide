# Carbide-pwsh — Phase 3 detailed plan (control flow, functions, errors, apps, classes — extended scope)

- Created (UTC): 2026-04-21T23:00:00Z
- Repository HEAD: ad9a5ea93897117cd90e2e6e36142bc90927cea2
- Status: detailed implementation plan for **Phase 3** of the [carbide-pwsh-subset-shell proposal](../proposals/carbide-pwsh-subset-shell-proposal__2026-04-21__21-30-00-000000__e9c4b27a8f13.md). Extends the original Phase 3 scope (control flow + try/catch + scripts + app invocation) with stretch features from the proposal's Phase 4 bucket: regex/format/join/split/containment operators, scope stack, classes, enums, and a broader cmdlet catalog.
- Audience: Vladimir; future Carbide contributors

## 1. Purpose

Phase 2 delivered a useful shell with pipelines, cmdlets, and a virtualized filesystem. Phase 3 turns the shell into a **complete scripting language** for the subset PowerShell-flavored niche we're targeting. After Phase 3, users can:

1. Write imperative logic — `if`, `while`, `for`, `foreach`, `switch`, with `break`/`continue`/`return` semantics that match real PowerShell.
2. Define and call their own **functions** with typed parameters, defaults, and optional pipeline-participating `begin`/`process`/`end` blocks.
3. Handle errors with **`try`/`catch`/`finally`**, `throw`, `$Error`, `$?`, `$LASTEXITCODE`, and `$ErrorActionPreference`.
4. Run **PowerShell-subset scripts** (`./script.ps1`) from the virtualized filesystem, including dot-sourcing.
5. Invoke **Carbide-compiled .NET console apps** by path or by registered name, with stdin/stdout/stderr routed to the shell and the exit code surfacing as `$LASTEXITCODE`.
6. Use the **standard comparison / formatting operators**: `-match`, `-replace`, `-like`, `-f`, `-join`, `-split`, `-contains`, `-in`.
7. Define their own **classes** (properties + methods + constructor) and **enums**, usable from the same `[Type]::Member` surface as BCL types.
8. Work with properly-scoped variables (`$script:`, `$global:`, `$local:`, `$private:`) that behave correctly across function call boundaries.
9. Rely on additional built-in cmdlets: `Start-Sleep`, `Get-Date`, `Get-Random`, `New-Guid`, `Invoke-WebRequest`/`Invoke-RestMethod` (stretch).

Phase 3 is the "feature-complete-for-the-scope" milestone. After it, the remaining proposal-committed work is mostly polish: tab completion, PSReadLine-style line editing, richer parameter binding, and deferred stretch items like `Add-Type` wired to Carbide's Roslyn.

## 2. Extended scope rationale

The **original** Phase 3 scope in the proposal was:

> Scripts in the VFS run. Carbide-compiled DLLs with entry points run when invoked by name. `try`/`catch`/`throw`/`$ErrorActionPreference = 'Stop'` behave sensibly.

This plan extends that in four directions, each with a specific reason:

| Extension | Reason |
|---|---|
| Control flow (`if`, `while`, `for`, `foreach`, `switch`) | The original proposal deferred this to Phase 3+ but *didn't actually commit Phase 3 to it*. Without it, scripts can't really do useful work — the first `./script.ps1` a user writes will almost certainly want `if` or `foreach`. Ship it. |
| User-defined functions | Same argument as control flow. `try` without `function` is awkward. Combining them in one phase avoids a three-way split where "scripts run but can't define functions". |
| Classes + enums | Proposal's Phase 4 deferral. Pulled forward because (a) PowerShell scripts with classes are common, (b) the reflection-based `RuntimeClass` approach works without IL emission, and (c) it proves out the "shell as general-purpose language" story. |
| Regex/format/join/split/contains operators | Proposal's Phase 4 deferral. Pulled forward because they're small (~100-300 LOC each), frequently used (`-match` in particular), and the implementation is a thin wrapper over `System.Text.RegularExpressions` + `String.Format`. |

What's **still** deferred (Phase 4+):

- Advanced parameter attributes (`[Parameter(ValueFromPipeline)]`, `[ValidateSet]` enforcement).
- Class inheritance, static members beyond trivial, property getters/setters with bodies.
- Named parameter sets (`[CmdletBinding(DefaultParameterSetName = 'A')]`).
- `Add-Type` and dynamic C# compilation.
- PSReadLine-style interactive line editor (syntax highlighting, history, completion).
- Custom formatting directives (`format.ps1xml`, `Format-Table -Property`).
- Real async cancellation via `CancelKeyPress` propagation into running scripts.
- Module loading (`using module`, `Import-Module`).

## 3. Acceptance — exit gate (expanded)

Every row below must pass as an xUnit integration test AND as a Playwright smoke step.

### 3.1 Control flow

| Input | Expected |
|---|---|
| `if (1 -eq 1) { 'yes' } else { 'no' }` | `yes` |
| `if ($false) { 'a' } elseif ($true) { 'b' } else { 'c' }` | `b` |
| `$i = 0; while ($i -lt 3) { $i; $i++ }` | `0`, `1`, `2` |
| `$i = 3; do { $i; $i-- } while ($i -gt 0)` | `3`, `2`, `1` |
| `for ($i = 0; $i -lt 3; $i++) { $i }` | `0`, `1`, `2` |
| `foreach ($x in 1..3) { $x * $x }` | `1`, `4`, `9` |
| `foreach ($x in 1..5) { if ($x -eq 3) { break }; $x }` | `1`, `2` |
| `foreach ($x in 1..5) { if ($x % 2 -eq 0) { continue }; $x }` | `1`, `3`, `5` |
| `switch (2) { 1 { 'one' } 2 { 'two' } default { '?' } }` | `two` |

### 3.2 Functions

| Input | Expected |
|---|---|
| `function f { param($x) $x * 2 }; f 5` | `10` |
| `function Sum { param($a, $b = 0) $a + $b }; Sum 3 4` | `7` |
| `function Sum { param($a, $b = 0) $a + $b }; Sum 3` | `3` |
| `function Greet { param([string] $name) "hello $name" }; Greet 'V'` | `hello V` |
| Multi-statement function with `return` | correct early exit |
| Recursive: `function Fact { param($n) if ($n -le 1) { 1 } else { $n * (Fact ($n - 1)) } }; Fact 5` | `120` |
| Pipeline-participating (stretch): `function Double { process { $_ * 2 } }; 1..3 \| Double` | `2`, `4`, `6` |

### 3.3 Error handling

| Input | Expected |
|---|---|
| `try { throw 'boom' } catch { "caught: $($_.Exception.Message)" }` | `caught: boom` |
| `try { 1/0 } catch { 'zero' }` | `zero` |
| `try { 'ok' } catch { 'no' } finally { 'always' }` | `ok`, `always` |
| `try { throw 'x' } finally { 'f' }` (no catch → rethrow) | `f` then error propagates |
| `$ErrorActionPreference = 'Stop'; try { Get-Content /nope } catch { 'missing' }` | `missing` |
| Typed catch: `try { throw [System.ArgumentException]::new('bad') } catch [System.ArgumentException] { 'arg' }` | `arg` |
| `$?` after success → `$true` |
| `$?` after failure → `$false` |

### 3.4 Script execution

| Input | Expected |
|---|---|
| Save `'hi from script'` to `/tmp/s.ps1`; run `./tmp/s.ps1` | `hi from script` |
| Dot-source: `. /tmp/init.ps1` injects its variables into current scope | caller sees them |
| Relative path: `cd /tmp; ./s.ps1` | resolves via `$PWD` |

### 3.5 App invocation

| Setup | Input | Expected |
|---|---|---|
| Carbide-compiled `./hello.dll` that `Console.WriteLine($"hi {args[0]}")` | `./hello.dll Vladimir` | prints `hi Vladimir`; `$LASTEXITCODE` = 0 |
| Register by name | `Register-CarbideApp -Name greet -Path ./hello.dll; greet 'V'` | prints `hi V` |
| Non-zero exit | app returns 7 | `$LASTEXITCODE` = 7 |

### 3.6 Operators

| Input | Expected |
|---|---|
| `'hello world' -match 'hello (\w+)'` | `True`; `$Matches[1]` = `world` |
| `'hello world' -replace 'world', 'universe'` | `hello universe` |
| `'foo.bar' -like '*.bar'` | `True` |
| `'0x{0:X}' -f 255` | `0xFF` |
| `@('a','b','c') -join ','` | `a,b,c` |
| `'a,b,c' -split ','` | `@('a','b','c')` |
| `@(1,2,3) -contains 2` | `True` |
| `2 -in @(1,2,3)` | `True` |
| `'abc' -notmatch '^\d+$'` | `True` |
| `'abc' -notlike 'x*'` | `True` |

### 3.7 Classes and enums

| Input | Expected |
|---|---|
| `class P { [int] $X; [int] $Y }` + `$p = [P]::new(); $p.X = 3; $p.X` | `3` |
| Class with constructor: `class P { [int] $V; P([int] $v) { $this.V = $v } }; [P]::new(7).V` | `7` |
| Class with method: `class P { [int] $V = 5; [int] Double() { return $this.V * 2 } }; [P]::new().Double()` | `10` |
| `enum Color { Red; Green; Blue }; [Color]::Green` | `Green` (prints name) |
| Enum compare: `[Color]::Red -eq [Color]::Red` | `True` |
| Enum cast: `[Color] 2` | `Blue` |

### 3.8 Scope

| Input | Expected |
|---|---|
| `$a = 1; function f { $a = 2 }; f; $a` | `1` (function creates local) |
| `$a = 1; function f { $script:a = 2 }; f; $a` | `2` |
| `$global:xyz = 42; function f { $global:xyz }; f` | `42` |

### 3.9 Aggregate

The **headline Phase 3 exit-gate script**:

```powershell
function Retry {
    param([scriptblock] $Action, [int] $Times = 3)
    for ($i = 1; $i -le $Times; $i++) {
        try { return & $Action }
        catch { if ($i -eq $Times) { throw } }
    }
}

class Counter {
    [int] $N = 0
    [int] Inc() { $this.N++; return $this.N }
}

$c = [Counter]::new()
$names = @('alice', 'bob', 'carol')
$greetings = foreach ($n in $names) {
    $i = $c.Inc()
    "$i. Hello, $($n -replace '^(.)', { $_.Groups[1].Value.ToUpper() })!"
}
$greetings -join "`n" | Set-Content /tmp/greetings.txt
Get-Content /tmp/greetings.txt | ForEach-Object { $_ }
```

Expected final stdout:

```
1. Hello, Alice!
2. Hello, Bob!
3. Hello, Carol!
```

This exercises: functions with typed parameters + defaults, script blocks as args, `for` loop, `try`/`catch`/`throw`, classes with fields and methods, `foreach` assigned to variable, `-replace` with a callback, `-join`, pipelining through `Set-Content` / `Get-Content`, `ForEach-Object` with a script block, and variable interpolation with sub-expressions. If this script runs end-to-end, Phase 3 is done.

## 4. Language surface additions

### 4.1 Control flow

```
ifStatement        ::= 'if' '(' pipeline ')' block ( 'elseif' '(' pipeline ')' block )* ( 'else' block )?
whileStatement     ::= 'while' '(' pipeline ')' block
doWhileStatement   ::= 'do' block ( 'while' | 'until' ) '(' pipeline ')'
forStatement       ::= 'for' '(' statement? ';' pipeline? ';' statement? ')' block
foreachStatement   ::= 'foreach' '(' '$' name 'in' pipeline ')' block
switchStatement    ::= 'switch' switchOptions? '(' pipeline ')' '{' switchCase* ( 'default' block )? '}'
switchCase         ::= switchPattern block
switchPattern      ::= literal | 'default'
break              ::= 'break' identifier?
continue           ::= 'continue' identifier?
return             ::= 'return' pipeline?
block              ::= '{' statements '}'
```

Phase 3's `switch` supports only literal patterns (numbers, strings, `$variable`, `default`) — no regex or script-block patterns (Phase 4).

### 4.2 Functions

```
functionDefinition ::= 'function' identifier '{' (paramBlock | functionBody) '}'
paramBlock         ::= 'param' '(' parameter ( ',' parameter )* ')' functionBody
parameter          ::= typeLiteral? '$' identifier ( '=' expression )?
functionBody       ::= namedBlocks? statements
namedBlocks        ::= ( beginBlock | processBlock | endBlock )+
beginBlock         ::= 'begin' block
processBlock       ::= 'process' block
endBlock           ::= 'end' block
```

Call sites mirror cmdlet invocation: `MyFunc arg1 -NamedParam val`. Positional binding by declaration order; named binding by `-Name`. Pipeline-participating functions (those with `process`) consume `$_` per pipeline item in the `process` block.

### 4.3 Error handling

```
tryStatement       ::= 'try' block catchClause* finallyClause?
catchClause        ::= 'catch' ( '[' typeName ']' ( ',' '[' typeName ']' )* )? block
finallyClause      ::= 'finally' block
throwStatement     ::= 'throw' expression?
```

Automatic variables set/read during error handling:

- `$_` inside `catch` is the `ErrorRecord`.
- `$Error` is a list of recent errors (bounded to 256 entries).
- `$?` is `false` after an error surfaced through non-`catch` or as a command failure; `true` otherwise.
- `$LASTEXITCODE` is set only by app invocation.
- `$ErrorActionPreference` controls default behavior of non-terminating errors:
  - `'Stop'` — errors terminate the current pipeline / script.
  - `'Continue'` (default) — error written to stderr, script continues.
  - `'SilentlyContinue'` — swallowed.
  - `'Inquire'` — treated as `Continue` for Phase 3 (no interactive UI).

### 4.4 Operators

Added to `Operators.cs`:

| Operator | Semantics |
|---|---|
| `-match`, `-imatch` | regex match, case-insensitive by default, populates `$Matches` |
| `-cmatch` | regex match, case-sensitive |
| `-notmatch`, `-inotmatch`, `-cnotmatch` | negation |
| `-replace`, `-ireplace` | regex-based replacement, case-insensitive default |
| `-creplace` | case-sensitive replacement |
| `-like`, `-ilike` | shell-glob match (`*`, `?`) |
| `-notlike`, `-inotlike` | negation |
| `-clike`, `-cnotlike` | case-sensitive |
| `-contains`, `-notcontains` | left-side collection contains element |
| `-in`, `-notin` | left-side element in right-side collection |
| `-f` | format: `'formatString' -f arg1, arg2, …` uses `String.Format` |
| `-join` | `array -join 'sep'` or `-join array` (unary) |
| `-split` | `'str' -split 'sep'` or `-split 'a b c'` |
| `-replace` with callback | stretch; script-block replacement evaluated per match |

### 4.5 Classes

```
classDefinition    ::= 'class' identifier '{' classMember* '}'
classMember        ::= propertyMember | methodMember
propertyMember     ::= typeLiteral? '$' identifier ( '=' expression )? ';'?
methodMember       ::= typeLiteral? identifier '(' parameter? ( ',' parameter )* ')' block
```

Phase 3 class subset:

- Fields with optional type annotations and defaults.
- Instance methods.
- A single constructor (overloads deferred).
- `$this` refers to the current instance inside methods/constructors.
- `[Type]::new(…)` calls the constructor.
- `$instance.Method(…)` calls an instance method.
- `$instance.Field` reads/writes a field.
- `-is [Type]` works with runtime classes.
- Cast `[Type] $hashtable` sets fields from hashtable keys.

Not in Phase 3:

- Inheritance (`class A : B`).
- Static members.
- Method overloading.
- Property getters/setters with bodies.
- Hidden members.
- Interfaces.

### 4.6 Enums

```
enumDefinition     ::= 'enum' identifier '{' enumMember ( ','? enumMember )* '}'
enumMember         ::= identifier ( '=' integer )?
```

Runtime semantics:

- `[Color]::Red` yields an `EnumValue` with name `Red` and underlying int `0` (or the assigned integer).
- `[Color] 2` casts an int to the enum value with that numeric value.
- Comparison (`-eq`, `-lt`) compares underlying ints.
- Display: `EnumValue.ToString()` returns the member name; used by formatters and string interpolation.
- `-is [Color]` is true for values of that enum; false otherwise.

### 4.7 Scope qualifiers

The scope stack adds these rings (innermost to outermost):

- `Local` — current function or script block.
- `Function` — the surrounding function body (alias for Local inside a function).
- `Script` — the enclosing script file's scope.
- `Global` — the shell-wide scope (persists across REPL submissions).

Qualifier semantics:

| Qualifier | Meaning |
|---|---|
| `$foo` | Look up in Local, then walk up (dynamic scope resolution). Assignment writes Local. |
| `$local:foo` | Same lookup as `$foo` but assignment forced to Local. |
| `$private:foo` | Local only — invisible to callees. |
| `$script:foo` | The enclosing script scope (or Global if not in a script). |
| `$global:foo` | The global scope. |
| `$env:NAME` | Environment variable (carries over from Phase 1). |

## 5. Cmdlet additions

New cmdlets registered in Phase 3:

| Cmdlet | Purpose |
|---|---|
| `Start-Sleep -Seconds n` / `-Milliseconds m` | Pause (implemented as `Task.Delay`; synchronous wait on the current thread on WASM). |
| `Get-Date` | Returns `DateTime.UtcNow`; `-Format` applies `ToString(format)`. |
| `Get-Random -Minimum -Maximum -Count` | `System.Random` with optional seed. |
| `New-Guid` | `Guid.NewGuid()`. |
| `Register-CarbideApp -Name -Path` | Registers a VFS-resident DLL under a shell command name. |
| `Unregister-CarbideApp -Name` | Removes an app registration. |
| `Invoke-Expression` | Parses and evaluates a string as script. |

Stretch (ship if scope allows):

| Cmdlet | Purpose |
|---|---|
| `Invoke-WebRequest` / `Invoke-RestMethod` | HTTP via `HttpClient` — browser uses fetch bridge. |
| `Format-Table`, `Format-List` | Trivial wrappers around the existing `OutputFormatter`. |
| `Select-String` | Pattern match on strings. |

## 6. Runtime architecture

### 6.1 Scope stack

Current `Scope` becomes a stack-of-frames structure:

```csharp
public enum ScopeKind { Global, Script, Function, Local }

public sealed class ScopeFrame
{
    public ScopeKind Kind { get; }
    public ScopeFrame? Parent { get; }
    public Dictionary<string, object?> Variables { get; }
    public bool IsPrivateBarrier { get; } // private scope qualifier stops at here
}

public sealed class Scope
{
    public ScopeFrame Global { get; }
    public ScopeFrame Script { get; private set; } // currently-active script scope
    public ScopeFrame Current { get; private set; } // innermost

    public object? Get(string? qualifier, string name);
    public void Set(string? qualifier, string name, object? value);
    public IDisposable Push(ScopeKind kind); // returns handle that pops on dispose
    public IDisposable PushScript(); // when running a script file
}
```

Default variable lookup walks `Current → Current.Parent → … → Global`, skipping `private` frames that aren't on our call path.

Default variable assignment writes to `Current`. Qualifiers route:

- `$script:x = 1` → writes to `Script`.
- `$global:x = 1` → writes to `Global`.
- `$local:x = 1` → writes to `Current`.
- `$private:x = 1` → writes to `Current` with private barrier semantics.
- `$env:PATH = 'x'` → existing env var path.

### 6.2 Function model

```csharp
public sealed class ScriptFunction
{
    public string Name { get; }
    public IReadOnlyList<ParameterInfo> Parameters { get; }
    public ScriptAst? BeginBlock { get; }
    public ScriptAst? ProcessBlock { get; }
    public ScriptAst? EndBlock { get; }
    public ScriptAst? SimpleBody { get; }     // used when no begin/process/end blocks

    public IEnumerable<object?> InvokeAsCommand(
        IEnumerable<object?>? input,
        IReadOnlyList<object?> positional,
        IReadOnlyDictionary<string, object?> named,
        Interpreter interpreter);
}

public sealed class ParameterInfo
{
    public string Name { get; }
    public Type? TypeConstraint { get; }
    public ExpressionAst? DefaultValue { get; }
}
```

The function registry sits next to the cmdlet registry. When `Pipeline.RunCommand` fails to find a cmdlet, it falls through to the function registry before reporting "not recognized".

### 6.3 Error model

```csharp
public sealed class ErrorRecord
{
    public Exception Exception { get; init; } = null!;
    public object? TargetObject { get; init; }
    public string FullyQualifiedErrorId { get; init; } = "";
    public string CategoryInfo { get; init; } = "";
    public SourceLocation Location { get; init; }

    public override string ToString() => Exception.Message;
}

public sealed class PwshTerminatingException : Exception
{
    public ErrorRecord Error { get; }
    public PwshTerminatingException(ErrorRecord error) : base(error.Exception.Message, error.Exception)
    { Error = error; }
}

// Flow control
public sealed class PwshBreakException : Exception { public string? Label { get; } }
public sealed class PwshContinueException : Exception { public string? Label { get; } }
public sealed class PwshReturnException : Exception { public object? Value { get; } }
```

Try/catch catches `PwshTerminatingException` and other exceptions; the catch body binds `$_` to the `ErrorRecord`. `finally` always runs (even through break/return/throw). Return/break/continue propagate as exceptions caught by the innermost loop or function body.

### 6.4 Script loader

A new helper on `ShellHost`:

```csharp
public object? RunScriptFile(string vfsPath, bool dotSource, IReadOnlyList<object?> args)
{
    var file = Vfs.Resolve(vfsPath) as VfsFile
        ?? throw new PwshRuntimeException($"Script '{vfsPath}' not found.");
    var source = file.ReadText();
    var script = Parser.ParseString(source);

    if (dotSource)
    {
        // Evaluate in current scope.
        return Interpreter.Evaluate(script);
    }
    // Push a new Script scope; set $args, $PSScriptRoot, $PSCommandPath.
    using var scope = Interpreter.Scope.PushScript();
    Interpreter.Scope.Set(null, "args", args.ToArray());
    Interpreter.Scope.Set(null, "PSScriptRoot", VfsPath.SplitLeaf(vfsPath).Parent);
    Interpreter.Scope.Set(null, "PSCommandPath", vfsPath);
    return Interpreter.Evaluate(script);
}
```

Integration: the command dispatcher detects bare-word command names that look like VFS paths (`/`, `./`, `../`, `~`, or a name registered via `Register-CarbideApp`), and routes them to `RunScriptFile` (`.ps1`) or `RunApp` (`.dll`).

### 6.5 App invocation

```csharp
public int RunApp(string vfsPath, IReadOnlyList<object?> args)
{
    var file = Vfs.Resolve(vfsPath) as VfsFile
        ?? throw new PwshRuntimeException($"App '{vfsPath}' not found.");
    var asm = Assembly.Load(file.Content);
    var entry = asm.EntryPoint ?? throw new PwshRuntimeException($"No entry point in '{vfsPath}'.");

    var stringArgs = args.Select(a => Coercion.FormatAsString(a)).ToArray();
    object? result;
    try
    {
        var parameters = entry.GetParameters();
        result = parameters.Length switch
        {
            0 => entry.Invoke(null, null),
            1 when parameters[0].ParameterType == typeof(string[]) => entry.Invoke(null, new object?[] { stringArgs }),
            _ => throw new PwshRuntimeException("Unsupported entry-point signature."),
        };
    }
    catch (TargetInvocationException tie)
    {
        throw new PwshRuntimeException(tie.InnerException?.Message ?? tie.Message, SourceLocation.None, tie.InnerException);
    }
    int code = result switch
    {
        int i => i,
        Task<int> ti => ti.GetAwaiter().GetResult(),
        Task t => (t.GetAwaiter().GetResult(), 0).Item2,
        _ => 0,
    };
    Interpreter.Scope.Set("global", "LASTEXITCODE", code);
    return code;
}
```

Stdin/stdout/stderr aren't rebound — the app runs in the same `Console.Out` context as the shell, which means its output interleaves with the REPL naturally.

App registration maps a shell command name to a VFS path. At dispatch time, if the command name isn't a cmdlet/function/script path but is a registered app name, we invoke it via `RunApp`.

### 6.6 Runtime classes and enums

```csharp
public sealed class RuntimeClass
{
    public string Name { get; }
    public IReadOnlyList<ClassProperty> Properties { get; }
    public MethodDefinition? Constructor { get; }
    public IReadOnlyDictionary<string, MethodDefinition> Methods { get; }
}

public sealed class ClassProperty
{
    public string Name { get; }
    public Type? TypeConstraint { get; }
    public ExpressionAst? Default { get; }
}

public sealed class MethodDefinition
{
    public string Name { get; }
    public IReadOnlyList<ParameterInfo> Parameters { get; }
    public Type? ReturnTypeConstraint { get; }
    public ScriptAst Body { get; }
}

public sealed class RuntimeInstance
{
    public RuntimeClass Class { get; }
    public Dictionary<string, object?> Fields { get; }
}
```

The `TypeBridge` gets a `RuntimeClass` table; `ResolveType` checks it before BCL resolution. When `[MyClass]::new(args)` is evaluated, the interpreter builds a `RuntimeInstance`, initializes fields from defaults, binds constructor parameters, and runs the constructor body with `$this` set to the new instance. Instance method calls look up by name, bind args, push a Function scope with `$this` in Local, and evaluate the method body.

For enums, `RuntimeEnum` is a simple `(string name, Dictionary<string, int>)` structure. `[Color]::Red` yields an `EnumValue(Name: "Red", Value: 0, EnumTypeName: "Color")` that compares by `Value` and prints by `Name`.

### 6.7 Keyword handling in the parser

The lexer still emits plain `Identifier` tokens for `if`, `while`, etc. The parser checks the token text against a keyword set when at statement start (and only then) to dispatch to specialized statement parsers. Outside statement start, `if`, `while`, etc. can appear as identifiers (e.g., `$if = 5` is a valid assignment to the `if` variable since `if` is lexed as `Identifier` but the parser sees `$` first). This keeps the lexer context-free.

Keyword set (statement start only):

```
if, while, do, for, foreach, switch
function, filter, param, begin, process, end
try, catch, finally, throw, return, break, continue
class, enum
```

## 7. Execution order (sub-phases)

Each sub-phase is landable as one PR. Phase 3 is larger than Phase 2; the sub-phase split keeps review bounded.

### 3.1 Scope stack refactor
Refactor `Scope` from a single flat dictionary to a stack of frames. All callers keep working via the preserved `Get`/`Set` API. Add `Push`/`Pop` affordances and qualifier routing.

### 3.2 AST additions
Add all new AST node types (control flow, functions, errors, classes, enums). Compiles cleanly; no parser wiring yet.

### 3.3 Parser extensions (control flow + functions)
Parse keywords at statement start; produce new AST nodes. Cover if/while/do/for/foreach/switch, function definitions, param blocks, `break`/`continue`/`return`.

### 3.4 Runtime flow-control exceptions + interpreter
`PwshBreakException`, `PwshContinueException`, `PwshReturnException`. Interpreter evaluates all control-flow AST. Loop cmdlets (Where/ForEach) already work so no conflict.

### 3.5 Parser + interpreter: errors
`try`/`catch`/`finally`, `throw`. `ErrorRecord`, `$Error`, `$?`, `$ErrorActionPreference`.

### 3.6 Script file loader + dot-sourcing
Statement-start detection of VFS path commands; `ShellHost.RunScriptFile`; `.` dot-sourcing keyword at statement start.

### 3.7 Carbide-compiled app invocation
VFS `.dll` detection; `Assembly.Load` + `EntryPoint` invocation; `$LASTEXITCODE`; `Register-CarbideApp` / `Unregister-CarbideApp` cmdlets.

### 3.8 Operators (regex, format, join/split, contains)
Extend `Operators.Binary`, add lexer tokens for any new dashed operators (there are several: `-match`, `-notmatch`, `-cmatch`, `-imatch`, `-replace`, `-creplace`, `-ireplace`, `-like`, `-notlike`, `-clike`, `-ilike`, `-cnotlike`, `-inotlike`, `-f`, `-join`, `-split`, `-contains`, `-notcontains`, `-in`, `-notin`).

### 3.9 Classes + enums
Parser for `class`/`enum`; runtime `RuntimeClass`/`RuntimeEnum`; interpreter integration (`[T]::new`, `$this`, method dispatch, enum value display + comparison).

### 3.10 New cmdlets
`Start-Sleep`, `Get-Date`, `Get-Random`, `New-Guid`, `Invoke-Expression`, `Register-CarbideApp`, `Unregister-CarbideApp`.

### 3.11 Tests
New xUnit test files covering each area. Total target: ~100 new tests on top of Phase 2's 187.

### 3.12 Host + smoke test wiring
`index.html` source manifest, `smoke.mjs` drives the aggregate Phase 3 exit-gate script, `README` updated.

## 8. File-by-file deliverables

```
src/Shell/
  Errors/PwshException.cs                +40 LOC  (PwshTerminatingException, $?-aware bases)
  Errors/ErrorRecord.cs                  ~90 LOC  (new)
  Runtime/Scope.cs                       rewrite to ScopeFrame stack  (~180 LOC)
  Runtime/LoopControl.cs                 ~50 LOC  (Break/Continue/Return exceptions)
  Runtime/ScriptFunction.cs              ~220 LOC (new)
  Runtime/FunctionRegistry.cs            ~60 LOC  (new)
  Runtime/ClassRegistry.cs               ~100 LOC (new)
  Runtime/RuntimeClass.cs                ~120 LOC (new)
  Runtime/RuntimeInstance.cs             ~60 LOC  (new)
  Runtime/RuntimeEnum.cs                 ~100 LOC (new; enum type + EnumValue)
  Runtime/AppRegistry.cs                 ~60 LOC  (new)
  Runtime/Interpreter.cs                 +600 LOC (control flow, functions, errors, classes, apps)
  Runtime/Operators.cs                   +350 LOC (-match, -replace, -like, -f, -join, -split, -contains, -in)

  Lexer/TokenKind.cs                     +20 entries (new dashed operators)
  Lexer/Lexer.cs                         +40 LOC (new DashedOps entries)

  Parser/Ast/ControlFlowNodes.cs         ~150 LOC (new)
  Parser/Ast/FunctionNodes.cs            ~100 LOC (new)
  Parser/Ast/ErrorNodes.cs               ~60 LOC  (new)
  Parser/Ast/ClassEnumNodes.cs           ~80 LOC  (new)
  Parser/Parser.cs                       +800 LOC (keyword dispatch + all statement parsers + call operator)

  Cmdlets/System/StartSleepCommand.cs    ~30 LOC
  Cmdlets/System/GetDateCommand.cs       ~40 LOC
  Cmdlets/System/GetRandomCommand.cs     ~60 LOC
  Cmdlets/System/NewGuidCommand.cs       ~20 LOC
  Cmdlets/System/InvokeExpressionCommand.cs ~40 LOC
  Cmdlets/App/RegisterCarbideAppCommand.cs ~40 LOC
  Cmdlets/App/UnregisterCarbideAppCommand.cs ~20 LOC

  Host/ShellHost.cs                      +150 LOC (ScopeStack wiring, function/class/app registries, script loader, keyword dispatch)
  Host/OutputFormatter.cs                +30 LOC  (RuntimeInstance / EnumValue rendering)

test/
  Phase3ScopeTests.cs                    ~120
  Phase3ControlFlowTests.cs              ~200
  Phase3FunctionTests.cs                 ~180
  Phase3ErrorTests.cs                    ~150
  Phase3ScriptTests.cs                   ~120
  Phase3AppTests.cs                      ~100
  Phase3OperatorTests.cs                 ~220
  Phase3ClassEnumTests.cs                ~180
  Phase3IntegrationTests.cs              ~120
```

Rough total: ~5.8k LOC of new/modified production + ~1.4k LOC of tests.

## 9. Design decisions

### D1 — Loop / return flow via exceptions

`break`, `continue`, `return` throw specialized exceptions caught by the innermost relevant construct. Alternative approaches (state-machine codegen, explicit tuple returns) are either impossible on WASM or unnecessarily complex for a tree-walking interpreter. Exception-based flow matches PowerShell's own internal implementation shape and is cheap enough for shell-scale workloads.

### D2 — Tree-walked classes, not reflection-emitted

Classes live as `RuntimeClass` descriptors in a registry. Instances are `RuntimeInstance` objects with a field dictionary. Method calls bind the body to a new scope with `$this` in Local and execute via the same interpreter. No `System.Reflection.Emit` — the WASM runtime can't do that. Interop with the rest of the shell works because `TypeBridge` checks the class registry before BCL resolution.

### D3 — One constructor, no overloads

Keeps parser + method-binding code simple. Phase 4 can add overload resolution using the same scoring strategy `TypeBridge` already uses for BCL methods.

### D4 — Dynamic scope for variables, lexical for functions

Variable lookup walks the runtime scope stack (dynamic). Function bodies close over the *script-scope* at definition time — so a function defined at script scope still sees script variables when called from nested scopes. This matches PowerShell's own variable-binding behavior.

### D5 — `switch` is literal-only in Phase 3

Regex / wildcard / script-block switch patterns are deferred. Phase 3 supports integer, string, `$var`, and `default` labels.

### D6 — `$LASTEXITCODE` is global

App-exit codes should survive across REPL submissions, so `$LASTEXITCODE` is written to the Global scope. The same applies to `$?` and `$Error`.

### D7 — `throw` wraps non-exception values

`throw 'message'` creates a `RuntimeException("message")` internally, wrapped in an `ErrorRecord`. `throw [ErrorRecord]::new(...)` works too. This matches PowerShell behavior for both common forms.

### D8 — `-match` populates `$Matches`

The automatic `$Matches` variable gets an array of capture groups (index 0 is the whole match, 1+ are captures) on a successful `-match`. `-notmatch` does not clear or modify `$Matches` (matches PowerShell behavior).

### D9 — Enum values are a lightweight wrapper, not real .NET enums

We can't synthesize real `System.Enum` types at runtime on WASM (it needs `Reflection.Emit`). `EnumValue` is a small class that stores the enum type name, member name, and integer value. Compare and cast behave like real enums; reflection against `EnumValue.GetType()` just returns `typeof(EnumValue)` — callers that need actual enum typing would need a runtime integration path that's beyond Phase 3's scope.

### D10 — Keyword recognition is parser-side, not lexer-side

Keeping `if`/`while`/etc. as `Identifier` tokens at lex time means `$if = 5` still works and we don't have to split into command-mode-specific keyword handling. The parser dispatches on token text at statement start only.

### D11 — `begin`/`process`/`end` blocks are declared by label, not braces

The parser recognizes `begin { … }`, `process { … }`, `end { … }` at function top-level. A function body that uses at least one of these enters "pipeline-participating" mode; otherwise the whole body is treated as a simple script. Pipeline-participating functions receive `$_` in `process` and can emit via `return` / expressions.

### D12 — Typed parameter constraints use `Coercion.To<T>`

A function parameter `[int] $x` coerces its argument to `int` via the same `Coercion` machinery the interpreter already uses. Failure yields a `PwshCoercionException` with a helpful message.

## 10. Risks

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | Scope stack refactor breaks Phase 1/2 tests | High | Medium | Keep the public `Scope.Get`/`Set` shape exactly; add frames underneath; run full test suite between every commit. |
| R2 | Keyword detection mis-identifies `if`/`for`/etc. as cmdlets when user writes `$if = 5` | Low | Low | Keyword-check only at statement start after confirming Identifier kind; expression parsing sees `$if` via `$`, not Identifier. |
| R3 | Exception-based `break`/`continue`/`return` is slow | Low | Low | Shell-scale workloads don't hit this. If they do, Phase 4 can replace with cooperative signals. |
| R4 | `try`/`finally` doesn't run `finally` on `break`/`return` | Medium | High | Explicit test: `foreach … { try { break } finally { 'f' } }` must print `f`. Interpreter uses `try { ... } finally { eval finally block }` wrapping the flow-exception. |
| R5 | Class fields default to `$null` but PowerShell uses type-appropriate zeroes | Low | Low | Match PowerShell: `[int] $X` defaults to 0, `[string] $S` defaults to `""`. Coercion table handles this. |
| R6 | Enum comparison with integer equality edge cases | Low | Low | Match: `[Color]::Red -eq 0` is `True` in PowerShell; we coerce the right side to int before compare. |
| R7 | Method dispatch on RuntimeInstance conflicts with reflection-based instance members | Low | Medium | Interpreter checks `RuntimeInstance` first; if no class method matches, fall through to reflection via `TypeBridge`. |
| R8 | App invocation blocks the shell until the app exits | Medium | Low | Document it. Ctrl+C cancellation into app code is Phase 4 (needs cancellation-token threading). |
| R9 | `-match` regex flavor differs from .NET's default (e.g., multiline anchors) | Low | Low | Document our regex engine = `System.Text.RegularExpressions` with `RegexOptions.None` default, `IgnoreCase` on `-imatch` / default. |
| R10 | `Start-Sleep` blocks the single Mono-WASM thread | High | Medium | `Start-Sleep` uses `Thread.Sleep` on non-WASM, `Task.Delay(...).Wait()` on WASM if permitted by the runtime. If neither works, fall through to a busy-loop with bounded duration + warning. |
| R11 | Script file stdin isn't wired up | Low | Low | Phase 3 scripts don't read stdin; if they do, they get the shell's stdin (Console.In). |
| R12 | Dot-sourced scripts leak variables across invocations | Medium | Low | That's the *definition* of dot-sourcing. Document it. |

## 11. Out of scope (even for extended Phase 3)

- Class inheritance and static members beyond simple data.
- Method overloading inside user-defined classes.
- Property getters/setters with bodies.
- Generic type parameters in user-defined classes.
- `using module`, module loading, `Import-Module`.
- `Add-Type` for inline C# / VB compilation.
- PSReadLine-style line editor, history across shell sessions, tab completion.
- Custom format views (`format.ps1xml`).
- Interactive `Inquire` / `Confirm` prompts.
- Parameter attribute enforcement beyond type (`[ValidateSet]` parsing only, no enforcement).
- Full `$ErrorActionPreference = 'Inquire'` semantics.
- `Register-ObjectEvent` / event sourcing.
- Remoting, jobs, remoting-related `Invoke-Command`.
- Async cancellation propagation into running scripts on Ctrl+C.
- A concrete browser/Node VFS persistence backend (still wiring-ready; Phase 2.1 item).

## 12. Appendices

### 12.1 Keyword list (recognized at statement start)

`if`, `elseif`, `else`, `while`, `do`, `until`, `for`, `foreach`, `in`, `switch`, `default`,
`function`, `filter`, `param`, `begin`, `process`, `end`,
`try`, `catch`, `finally`, `throw`, `return`, `break`, `continue`,
`class`, `enum`.

### 12.2 New dashed operator tokens

`-match`, `-imatch`, `-cmatch`, `-notmatch`, `-inotmatch`, `-cnotmatch`,
`-replace`, `-ireplace`, `-creplace`,
`-like`, `-ilike`, `-clike`, `-notlike`, `-inotlike`, `-cnotlike`,
`-contains`, `-notcontains`, `-ccontains`, `-cnotcontains`, `-icontains`, `-inotcontains`,
`-in`, `-notin`, `-cin`, `-cnotin`, `-iin`, `-inotin`,
`-f`, `-join`, `-split`.

### 12.3 Automatic variables added in Phase 3

| Variable | Semantics |
|---|---|
| `$_` / `$PSItem` | Pipeline item (already in Phase 2); also error object inside `catch`. |
| `$Matches` | Capture groups from the most recent `-match`. |
| `$Error` | Bounded list of recent `ErrorRecord`s (newest first). Max 256. |
| `$?` | `True` after success, `False` after the last error. |
| `$LASTEXITCODE` | Exit code from the last app / native invocation. |
| `$args` | Positional arguments to the current script. |
| `$PSScriptRoot` | Directory of the running script file. |
| `$PSCommandPath` | Full path of the running script file. |
| `$ErrorActionPreference` | Read by the runtime when handling non-terminating errors. |
| `$this` | Inside class methods, the current `RuntimeInstance`. |
| `$input` | Inside pipeline-participating functions, an enumerable of upstream input (stretch). |

### 12.4 Links

- [Parent proposal](../proposals/carbide-pwsh-subset-shell-proposal__2026-04-21__21-30-00-000000__e9c4b27a8f13.md)
- [Phase 1 plan](carbide-pwsh-phase1-detailed-plan__2026-04-21__21-45-00-000000__a5f8c3d192e0.md)
- [Phase 2 plan](carbide-pwsh-phase2-detailed-plan__2026-04-21__22-30-00-000000__b7e2c4a9d018.md)
- [PowerShell Language Specification — full chapter index](https://learn.microsoft.com/en-us/powershell/scripting/lang-spec/chapter-01)
- [`about_Functions_Advanced`](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_functions_advanced) — reference for `begin`/`process`/`end` semantics.
