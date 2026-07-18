# Carbide-pwsh — Phase 1 detailed plan (expression evaluator)

- Created (UTC): 2026-04-21T21:45:00Z
- Repository HEAD: ad9a5ea93897117cd90e2e6e36142bc90927cea2
- Status: detailed implementation plan for **Phase 1** of the [carbide-pwsh-subset-shell proposal](../proposals/carbide-pwsh-subset-shell-proposal__2026-04-21__21-30-00-000000__e9c4b27a8f13.md)
- Audience: Carbide Contributors; future Carbide contributors

## 1. Purpose

Phase 1 delivers the smallest shell that actually evaluates PowerShell-flavored expressions. After Phase 1, the user can:

1. Open the `carbide-pwsh` page in a browser, boot Carbide, and land on a prompt.
2. Type `2 + 2` and see `4`.
3. Assign and read variables: `$x = 5; $x * 2` → `10`.
4. Interpolate strings: `$name = 'Ada'; "hello, $name"` → `hello, Ada`.
5. Build arrays and hashtables: `@(1,2,3)`, `@{ a = 1; b = 2 }`.
6. Call .NET static members in pwsh syntax: `[System.Math]::Sqrt(2)`.
7. Mutate the terminal cosmetically: `[System.Console]::BackgroundColor = 'DarkBlue'; [System.Console]::Clear()`.
8. Cast values: `[int]'42'`, `[string]3.14`.

Phase 1 deliberately does **not** ship: cmdlets, pipelines, a virtual filesystem, control flow (`if`, `while`, `for`, `foreach`, `try`/`catch`), functions, script blocks, scope qualifiers (`$script:`, `$global:`, `$env:`), or multi-line submissions. Those land in Phase 2+.

## 2. Acceptance — expanded

### 2.1 Functional acceptance

Each of the following input → output pairs is covered by a test. They run as xUnit integration tests against the library and as Playwright smoke-test steps against the xterm.js host.

| Input | Expected output or effect |
|---|---|
| `2 + 2` | `4` |
| `2 * 3 + 4` | `10` |
| `(2 + 3) * 4` | `20` |
| `10 / 4` | `2.5` |
| `10 % 3` | `1` |
| `$x = 5` then `$x + 2` | `7` |
| `$x = 'Ada'; "hello, $x"` | `hello, Ada` |
| `"one + two = $(1 + 2)"` | `one + two = 3` |
| `'no $interpolation here'` | `no $interpolation here` |
| `@(1, 2, 3).Length` | `3` |
| `@{ a = 1; b = 2 }['a']` | `1` |
| `1..5` (written one per line) | `1`, `2`, `3`, `4`, `5` |
| `[System.Math]::PI` | `3.141592653589793` |
| `[System.Math]::Sqrt(2)` | `1.4142135623730951` |
| `[int]'42' + 1` | `43` |
| `[string]3.14` | `3.14` |
| `1 -eq 1` | `True` |
| `'a' -eq 'A'` | `True` (case-insensitive default) |
| `'a' -ceq 'A'` | `False` |
| `5 -gt 3` | `True` |
| `$true -and $false` | `False` |
| `!$true` | `False` |
| `[System.Console]::BackgroundColor = 'DarkBlue'; [System.Console]::Clear()` | xterm-visible color + clear |
| `[System.Text.StringBuilder]::new().Append('hi').ToString()` | `hi` |

### 2.2 Negative-path acceptance

- Parse error: `2 +` surfaces an `UnexpectedEndOfInput` error with line/column.
- Unresolved variable: `$undefined` yields `$null` (PowerShell semantics; not an error) — this must NOT throw.
- Unknown type: `[Nope.NotReal]::Foo` surfaces a `TypeNotFound` error naming `Nope.NotReal`.
- Unknown member: `[System.Math]::Nope()` surfaces a `MemberNotFound` error with closest matches (Levenshtein top-3).
- Overload mismatch: `[System.Math]::Sqrt('abc')` surfaces a `MethodBindingFailed` error explaining the argument type problem.
- Runtime exception from a .NET call: the shell formats the inner exception message cleanly (strips `TargetInvocationException`) and keeps the REPL alive.

### 2.3 Non-functional acceptance

- `dotnet build` of `CarbidePwsh.Shell.csproj` + `CarbidePwsh.App.csproj` + `CarbidePwsh.Tests.csproj` is warning-clean under `Directory.Build.props` (trimming analyzer on).
- `dotnet test` passes locally on the author's machine.
- `node packages/carbide-pwsh/scripts/serve.mjs` + Playwright smoke test drives at least three input→output pairs from §2.1 through xterm.js and asserts the buffer contents.
- Cold page-load-to-prompt budget: ≤ 10 s on a warm Carbide publish (no regression bar; just a sanity ceiling).
- REPL stays alive across exceptions: a failing expression must not terminate the loop.

## 3. Execution order (sub-phases)

Each sub-phase is landable as one PR. Earlier sub-phases must stay green as later ones build on them.

### 1.1 Package skeleton

Deliverables:
- `src/Carbide/packages/carbide-pwsh/` directory scaffold.
- `package.json`, `README.md`, `index.html`, `scripts/serve.mjs`, `scripts/smoke.mjs` — all mirroring `packages/carbide-gh/` shape with copy changes.
- `src/CarbidePwsh.Shell.csproj` (library), `src/App/CarbidePwsh.App.csproj` (entry-point assembly referencing Shell), `test/CarbidePwsh.Tests.csproj` (xUnit, references Shell).
- Empty `src/Shell/Program.cs` placeholder, empty folders for `Lexer/`, `Parser/`, `Runtime/`, `Host/`, `Errors/`.

Exit: `dotnet build` of all three csprojs succeeds producing empty DLLs; `dotnet test` reports zero tests but succeeds.

### 1.2 Errors and diagnostic types

Deliverables:
- `src/Shell/Errors/PwshException.cs` — base class.
- `PwshParseException`, `PwshRuntimeException`, `PwshTypeException`, `PwshCoercionException`, `PwshMemberNotFoundException`, `PwshMethodBindingException`.
- `src/Shell/Errors/SourceLocation.cs` — `(string Source, int Line, int Column, int Offset, int Length)`.

Exit: classes compile; constructors and `ToString()` are tested.

### 1.3 Lexer

Deliverables:
- `src/Shell/Lexer/TokenKind.cs` — enum of all token kinds.
- `src/Shell/Lexer/Token.cs` — struct `(TokenKind, string Text, object? Value, SourceLocation Location)`.
- `src/Shell/Lexer/Lexer.cs` — hand-rolled tokenizer.

Scope: integer/double/hex literals; single-quoted and double-quoted strings (with `$var` and `$(...)` sentinels embedded as sub-tokens); identifiers; variables (`$name`, `$script:name` parsed-but-Phase-1-treated-as-`$name`); operators (`+`, `-`, `*`, `/`, `%`, `=`, `(`, `)`, `[`, `]`, `{`, `}`, `,`, `;`, `.`, `::`, `..`, `@(`, `@{`); dashed operators (`-eq`, `-ne`, `-lt`, …, `-and`, `-or`, `-not`, `-ceq`, etc.); newlines as statement separators; whitespace and comments (`#`, `<#…#>`).

**Double-quoted string treatment:** the lexer emits a single `String` token whose `Value` is an `IReadOnlyList<StringPart>` where each `StringPart` is either a literal span or an embedded expression (parsed lazily by the parser when consumed). This avoids a complicated re-entrant lexer state.

Indicative size: ~800–1000 LOC for `Lexer.cs`; ~100 LOC across `TokenKind.cs` + `Token.cs`.

Exit: ~60 fixtures (input → token sequence) in `test/LexerTests.cs` pass; all input→output rows from §2.1 lex cleanly.

### 1.4 Parser and AST

Deliverables:
- `src/Shell/Parser/Ast/` — AST node classes (all `sealed record`s for value-equality in tests).
- `src/Shell/Parser/Parser.cs` — recursive-descent parser over `Lexer` output.

AST node catalog for Phase 1:

| Node | Fields |
|---|---|
| `ScriptAst` | `IReadOnlyList<StatementAst> Statements` |
| `ExpressionStatementAst` | `ExpressionAst Expression` |
| `AssignmentStatementAst` | `VariableAst Target`, `ExpressionAst Value` |
| `VariableAst` | `string Scope?`, `string Name` |
| `NumberLiteralAst` | `object Value` (int, long, double) |
| `StringLiteralAst` | `IReadOnlyList<StringPart> Parts`, `bool IsSingleQuoted` |
| `BooleanLiteralAst` | `bool Value` |
| `NullLiteralAst` | (no fields) |
| `ArrayExpressionAst` | `IReadOnlyList<ExpressionAst> Elements` |
| `HashtableExpressionAst` | `IReadOnlyList<(ExpressionAst Key, ExpressionAst Value)> Entries` |
| `BinaryExpressionAst` | `ExpressionAst Left`, `BinaryOp Op`, `ExpressionAst Right` |
| `UnaryExpressionAst` | `UnaryOp Op`, `ExpressionAst Operand` |
| `RangeExpressionAst` | `ExpressionAst Start`, `ExpressionAst End` |
| `ParenExpressionAst` | `ExpressionAst Inner` |
| `SubExpressionAst` | `ScriptAst Body` |
| `TypeLiteralAst` | `string TypeName`, `IReadOnlyList<TypeLiteralAst> GenericArgs` |
| `CastExpressionAst` | `TypeLiteralAst TargetType`, `ExpressionAst Value` |
| `MemberAccessAst` | `ExpressionAst Target`, `string MemberName`, `bool IsStatic`, `bool IsInvocation`, `IReadOnlyList<ExpressionAst>? Arguments` |
| `InvokeMemberAst` | (folded into MemberAccessAst with `IsInvocation = true`) |
| `IndexerAst` | `ExpressionAst Target`, `ExpressionAst Index` |

Operator enums:

- `BinaryOp`: `Add`, `Subtract`, `Multiply`, `Divide`, `Modulo`, `Equal`, `NotEqual`, `LessThan`, `LessOrEqual`, `GreaterThan`, `GreaterOrEqual`, `And`, `Or`, `Xor`, `CEqual`, `CNotEqual`, `CLessThan`, `CLessOrEqual`, `CGreaterThan`, `CGreaterOrEqual`.
- `UnaryOp`: `Negate`, `Plus`, `Not`.

Indicative size: ~500 LOC across AST records, ~800–1000 LOC in `Parser.cs`.

Exit: ~40 parser fixtures in `test/ParserTests.cs` pass; every §2.1 input round-trips to a non-throwing AST.

### 1.5 Coercion and core runtime

Deliverables:
- `src/Shell/Runtime/Coercion.cs` — PowerShell-flavored type conversion rules.
- `src/Shell/Runtime/Scope.cs` — single-scope variable table (Phase 1 keeps it flat; scope stack lands in Phase 2 when functions arrive).
- `src/Shell/Runtime/Operators.cs` — implementation of binary/unary operators with coercion.

Coercion rules for Phase 1 (covers everything §2.1 needs):

| From | To | Rule |
|---|---|---|
| `int`, `long`, `double` | any numeric | `Convert.ChangeType` with `InvariantCulture` |
| `string` | `int`, `long`, `double`, `decimal` | `int.TryParse` / `double.TryParse`, invariant culture; failure → `PwshCoercionException` |
| `string` | `bool` | `""` / `"0"` / `"False"` / `"false"` → false; anything else → true (matches PowerShell) |
| `bool` | `int` | `true → 1`, `false → 0` |
| anything | `string` | `ToString()` (invariant for numbers) |
| `null` | numeric | `0` |
| `null` | `string` | `""` |
| `null` | `bool` | `false` |
| `string` | `ConsoleColor` | `Enum.Parse<ConsoleColor>(s, ignoreCase: true)` |
| numeric-to-numeric widening | — | PowerShell promotion: `int op double → double`, etc. |
| array | bool | empty → false, any element → the first element coerced |

Indicative size: ~400 LOC (Coercion) + ~300 LOC (Operators) + ~100 LOC (Scope).

Exit: ~30 coercion fixtures pass; operator truth-tables (arithmetic, comparison, logical) are tested exhaustively for Int32/Double/String combinations.

### 1.6 Type bridge

Deliverables:
- `src/Shell/Runtime/TypeBridge.cs` — resolves `[Type]` and dispatches `::Member` / `.Member`.
- `src/Shell/Runtime/TypeAliases.cs` — alias table (`[int]` → `System.Int32`, etc.).

Phase 1 allow-list (only these are accepted for `[Type]::Member` by default; others raise a warning but still allowed):

- `System.Console` (via the T3 forked DLL)
- `System.Math`
- `System.String`
- `System.DateTime`, `System.TimeSpan`
- `System.Convert`
- `System.Text.Encoding`, `System.Text.StringBuilder`
- `System.Text.Json.JsonSerializer`, `System.Text.Json.JsonDocument`
- `System.Environment` (read-only members)
- Primitives: `Int32`, `Int64`, `Double`, `Single`, `Decimal`, `Boolean`, `Char`, `Byte`, `UInt32`, `UInt64`
- `System.Guid`
- `System.Array`
- `System.Linq.Enumerable`

Overload resolution strategy:

1. Collect all `MethodInfo`s matching name and arity.
2. For each candidate, score each argument by (a) identity match (+100), (b) assignable without conversion (+50), (c) coercible by Coercion (+10), (d) not convertible (-1000).
3. Sum per candidate; pick the max-scoring. Ties → first in declaration order (deterministic by `MethodHandle.Value`).
4. Convert arguments via Coercion, invoke `MethodInfo.Invoke`, unwrap `TargetInvocationException`, return the result.

Indicative size: ~500–700 LOC in `TypeBridge.cs`; ~80 LOC in `TypeAliases.cs`.

Exit: type-bridge fixtures cover `[int]`, `[System.Math]::Sqrt`, `[System.Math]::Max(int, int)` overload resolution, `[DateTime]::Now`, `[System.Console]::BackgroundColor` set, `[System.Text.StringBuilder]::new()`, and the full list of negative-path cases from §2.2.

### 1.7 Interpreter

Deliverables:
- `src/Shell/Runtime/Interpreter.cs` — tree-walking evaluator.

Shape:

```csharp
public sealed class Interpreter
{
    private readonly Scope _scope;
    private readonly TypeBridge _types;

    public object? Evaluate(ScriptAst script)
    {
        object? last = null;
        foreach (var statement in script.Statements)
        {
            last = EvaluateStatement(statement);
        }
        return last;
    }

    private object? EvaluateStatement(StatementAst statement) => statement switch
    {
        AssignmentStatementAst a => EvaluateAssignment(a),
        ExpressionStatementAst e => EvaluateExpression(e.Expression),
        _ => throw new InvalidOperationException($"Unsupported statement: {statement.GetType().Name}"),
    };

    private object? EvaluateExpression(ExpressionAst expr) => expr switch { /* dispatch */ };
}
```

Indicative size: ~600–800 LOC.

Exit: the interpreter correctness tests in `test/InterpreterTests.cs` drive every §2.1 input through `Parser → AST → Interpreter.Evaluate` and compare against expected output.

### 1.8 Host formatting

Deliverables:
- `src/Shell/Host/OutputFormatter.cs` — turns a result `object?` into a display string.

Phase 1 formatting rules (match PowerShell defaults for the shapes we return):

| Result type | Output |
|---|---|
| `null` | (nothing) |
| `bool` | `True` / `False` |
| `string` | the string, no quotes |
| numeric | `ToString(InvariantCulture)` |
| `IEnumerable<T>` | one line per element, each formatted recursively |
| `IDictionary` / `Hashtable` | `Name Value\n---- -----\na    1\nb    2` (PowerShell-ish table) |
| any other | `ToString()` |

Indicative size: ~200–300 LOC.

Exit: formatter unit tests plus visual inspection of the smoke test output.

### 1.9 REPL and Program.cs

Deliverables:
- `src/App/Program.cs` — the REPL entry point. Roughly the same shape as `carbide-gh/src/Program.cs`:

```csharp
using System;
using System.Threading.Tasks;
using CarbidePwsh.Shell;
using CarbidePwsh.Shell.Host;

Banner.Write();

var shell = new ShellHost();
while (true)
{
    Console.Out.Write(shell.BuildPrompt());
    string? line;
    try { line = await Console.In.ReadLineAsync(); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"readline failed: {ex.Message}");
        break;
    }
    if (line is null) break;
    var trimmed = line.Trim();
    if (trimmed.Length == 0) continue;
    if (trimmed is "exit" or "quit" or ":q") break;

    try
    {
        shell.SubmitAndRender(trimmed);
    }
    catch (Exception ex)
    {
        shell.RenderError(ex);
    }
}
```

- `src/Shell/Host/ShellHost.cs` — wraps `Parser` + `Interpreter` + `OutputFormatter`. Owns the single persistent `Scope` so variables survive across submissions.
- `src/Shell/Host/Banner.cs` — prints a small ASCII banner.

Indicative size: ~300 LOC total.

Exit: REPL boots, prints banner, accepts input, evaluates, renders, and exits on `exit`.

### 1.10 xterm.js host page

Deliverables:
- `index.html` — clone of `carbide-gh/index.html` with the Spectre-specific vendor-load removed (Phase 1 uses no external references) and source file names adjusted.
- `scripts/serve.mjs` — clone of `carbide-gh/scripts/serve.mjs`.
- `scripts/smoke.mjs` — Playwright harness that drives at least three §2.1 rows plus one negative-path row.

Exit: `npm run serve` + `npm run smoke` passes in CI (manual for now).

## 4. File-by-file deliverables

```
src/Carbide/packages/carbide-pwsh/
├── README.md                                                  (~150 LOC)
├── package.json                                               (~20 LOC)
├── index.html                                                 (~110 LOC, mirrors carbide-gh)
├── scripts/
│   ├── serve.mjs                                              (~70 LOC)
│   └── smoke.mjs                                              (~80 LOC)
└── src/
    ├── Shell/                                                 (library sources)
    │   ├── CarbidePwsh.Shell.csproj                           (lib; net10.0)
    │   ├── Errors/
    │   │   ├── PwshException.cs                               (~40 LOC)
    │   │   └── SourceLocation.cs                              (~30 LOC)
    │   ├── Lexer/
    │   │   ├── TokenKind.cs                                   (~90 LOC)
    │   │   ├── Token.cs                                       (~40 LOC)
    │   │   └── Lexer.cs                                       (~900 LOC)
    │   ├── Parser/
    │   │   ├── Parser.cs                                      (~900 LOC)
    │   │   └── Ast/
    │   │       ├── AstNode.cs                                 (~60 LOC)
    │   │       └── AstNodes.cs                                (~350 LOC)
    │   ├── Runtime/
    │   │   ├── Scope.cs                                       (~90 LOC)
    │   │   ├── Coercion.cs                                    (~400 LOC)
    │   │   ├── Operators.cs                                   (~300 LOC)
    │   │   ├── TypeAliases.cs                                 (~90 LOC)
    │   │   ├── TypeBridge.cs                                  (~600 LOC)
    │   │   └── Interpreter.cs                                 (~700 LOC)
    │   └── Host/
    │       ├── ShellHost.cs                                   (~150 LOC)
    │       ├── Banner.cs                                      (~30 LOC)
    │       └── OutputFormatter.cs                             (~250 LOC)
    └── App/
        ├── CarbidePwsh.App.csproj                             (exe; references Shell)
        └── Program.cs                                         (~50 LOC)
└── test/
    ├── CarbidePwsh.Tests.csproj                               (xunit; references Shell)
    ├── LexerTests.cs                                          (~300 LOC)
    ├── ParserTests.cs                                         (~250 LOC)
    ├── CoercionTests.cs                                       (~150 LOC)
    ├── InterpreterTests.cs                                    (~300 LOC)
    └── TypeBridgeTests.cs                                     (~200 LOC)
```

Rough total: ~6.3k LOC of production + test code. Within the ±10% budget the proposal set for Phase 1 (1.0–1.5k parser, 0.8–1.2k interpreter, 0.4–0.7k type bridge, 0.2k host, plus tests).

## 5. Design decisions

### D1 — Hand-rolled parser, not a parser generator

The PowerShell grammar is context-sensitive (the `>` token's meaning depends on whether we're in command mode vs expression mode; `-match` is an operator only in expression positions). Every ANTLR/PEG-generator approach would pay an ongoing translation-to-generator-DSL tax. A hand-rolled recursive-descent parser is ~1k LOC, fully debuggable, and isolates the context-sensitivity to explicit `ParserContext` flags on the `Parser` class.

### D2 — One flat scope (Phase 1 only)

Phase 2 introduces functions and script blocks, at which point a linked-list scope stack is required. For Phase 1, there is no enclosing scope to close over and no callable shape that introduces a new scope. A single `Dictionary<string, object?>` is sufficient.

### D3 — Arguments are passed to method invocations as positional `object?[]`

PowerShell's calling convention allows named parameters (`-Foo bar`), but named parameters only appear in cmdlet invocations, not in `[Type]::Method(…)` calls. Phase 1 supports positional-only. Named-parameter dispatch lands with cmdlets in Phase 2.

### D4 — Double interpolation is parsed lazily

The lexer emits a single `String` token whose payload is an ordered list of `LiteralSpan` / `ExpressionSpan` parts. The parser parses each `ExpressionSpan.Source` with a **recursive** `Parser.Parse` call when producing the `StringLiteralAst`. This keeps lexer state simple (no re-entrant lexing) at the cost of slightly slower parsing for interpolation-heavy inputs — fine for a REPL.

### D5 — Type-bridge allow-list default is "warn", not "deny"

Phase 1 ships with `$PwshPolicy::StrictTypes = $false` (the name is illustrative; the actual implementation is a `Host.Policy` property). Unknown types produce a one-shot stderr warning the first time they're referenced but are allowed. This is the friction-minimum default for an interactive shell; Phase 4 can tighten to strict.

### D6 — Numbers: int-first, promote to double on need

PowerShell's numeric-coercion rules are "smallest-containing type" with `int` → `long` → `decimal` → `double` escalation. Phase 1 implements the common cases: integer arithmetic stays `int` when it fits, overflows to `long`, overflows to `double`. `/` always returns `double` unless both operands are integers and the result is exact (matches PowerShell). Complicated cases (decimal, bigint) are documented as Phase-1 divergences and tracked.

### D7 — Output is synchronous and bounded

Phase 1 doesn't have pipelines or any enumerable output shape users could stream. The interpreter returns one value; the formatter renders it; the REPL prints it. If a result is a large collection, we format it eagerly. Phase 2's pipeline will introduce streaming output.

### D8 — Error recovery lives in the REPL, not the parser

The parser does not try to recover from errors and re-parse the rest of the input; it throws at the first unrecoverable state. The REPL catches the throw, prints a helpful message, and reads the next line. This is deliberate — partial-AST error recovery is expensive to get right for low user value at a REPL.

### D9 — No async in the interpreter

The evaluator is synchronous. Only the outer REPL uses `async` (for `await Console.In.ReadLineAsync()` and the bootup). This avoids propagating `async` into every AST visit method and matches the single-threaded Mono-WASM runtime invariant.

## 6. Tests

### 6.1 Coverage targets

| Area | Fixture count | Location |
|---|---:|---|
| Lexer | ~60 | `test/LexerTests.cs` — each fixture is `(input, expected tokens)` |
| Parser | ~40 | `test/ParserTests.cs` — each is `(input, expected AST shape)` |
| Coercion | ~30 | `test/CoercionTests.cs` — `(source, targetType, expected)` |
| Interpreter | ~40 | `test/InterpreterTests.cs` — `(script, expected result)`; covers every §2.1 row |
| TypeBridge | ~25 | `test/TypeBridgeTests.cs` — positive + every §2.2 negative-path row |

### 6.2 Integration & smoke

- `test/ShellHostTests.cs` — exercises the full REPL-submission pipeline (`Parser → Interpreter → OutputFormatter`) with ~15 end-to-end cases.
- `scripts/smoke.mjs` — Playwright; drives ≥3 inputs through xterm and asserts the buffer.

### 6.3 Running

```bash
cd Carbide/packages/carbide-pwsh/src
dotnet build Shell/CarbidePwsh.Shell.csproj
dotnet build App/CarbidePwsh.App.csproj
cd ../test
dotnet test CarbidePwsh.Tests.csproj
```

Browser smoke:

```bash
cd Carbide/packages/carbide-pwsh
node scripts/serve.mjs &
node scripts/smoke.mjs
```

## 7. Risks

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | Hand-rolled lexer's double-quoted-string state machine has bugs with escape sequences and nested `$(...)` | High | Medium | Fixture-heavy testing; ~15 of the Lexer's 60 fixtures target the string machine specifically. |
| R2 | Parser diverges from real PowerShell on operator precedence | Medium | Medium | Precedence table is an enumerated constant in `Parser.cs` with source comments citing the [PowerShell Language Spec §7.1](https://learn.microsoft.com/en-us/powershell/scripting/lang-spec/chapter-07). Divergences are tested, not discovered. |
| R3 | Reflection-based member dispatch is slow at scale | Low | Low | Shell-scale only; profile only if a real slowdown surfaces. |
| R4 | Mono-WASM's trimmer strips a member we resolve by reflection | Medium | High | The shell's `CarbidePwsh.Shell.csproj` sets `IsTrimmable=false` (override the `Directory.Build.props` default); the REPL runs untrimmed. Carbide's in-browser Roslyn build isn't trimmed either. Trim-compatibility is a Phase 4 optimization. |
| R5 | PowerShell's `-match` regex flavor differs from .NET's default | — | — | Phase 1 does not ship `-match`. Lands with the comparison operator set review in Phase 2. |
| R6 | `$(…)` subexpression inside a double-quoted string recursively re-enters the parser; mismatched quotes or unclosed parens confuse error recovery | Medium | Low | Parser catches and wraps inner exceptions with location info; dedicated fixtures cover the broken-quote cases. |
| R7 | Overload resolution picks the "wrong" overload when multiple candidates tie on score | Low | Medium | Ties resolved by `MethodHandle.Value` order (deterministic). If users hit tie cases, verbose-mode logging shows the candidate list. |
| R8 | PlatformNotSupportedException from a .NET type we allow-listed | Low | Medium | Wrapped in `PwshRuntimeException` by the bridge; the REPL stays alive. |

## 8. Explicitly deferred to later phases

- Control flow: `if`/`elseif`/`else`, `while`, `do`, `for`, `foreach`, `switch`.
- Functions, script blocks, `param()`, `return`.
- Cmdlets and pipelines.
- Error handling: `try`/`catch`/`finally`, `throw`, `$ErrorActionPreference`.
- Scope qualifiers: `$script:`, `$global:`, `$env:` (parse-accepted but Phase-1-treated as `$name`).
- Regex operators: `-match`, `-notmatch`, `-replace`, `-like`, `-notlike`.
- Format, join, split operators: `-f`, `-join`, `-split`.
- Containment operators: `-contains`, `-notcontains`, `-in`, `-notin`.
- Virtual filesystem and FS cmdlets.
- Invoking Carbide-compiled apps.
- Line editing beyond simple `Console.In.ReadLineAsync()`.
- Multi-line submissions (prompt changes on incomplete input).
- Tab completion.
- History.

## 9. Appendices

### 9.1 Example Phase 1 REPL session

```
PS > 2 + 2
4
PS > $name = 'Ada'
PS > "hello, $name"
hello, Ada
PS > [System.Math]::Sqrt(2)
1.4142135623730951
PS > @(1, 2, 3, 4, 5).Length
5
PS > [System.Console]::ForegroundColor = 'Green'
PS > 'green text'
green text
PS > [System.Console]::ResetColor()
PS > [DateTime]::Now.ToString('yyyy-MM-dd')
2026-04-21
PS > exit
```

### 9.2 Operator precedence (high → low)

1. `[Type]` cast and type literal (highest)
2. `.Member`, `::Member`, `()`, `[index]`
3. unary `-`, `+`, `!`, `-not`
4. `..` range
5. `*`, `/`, `%`
6. `+`, `-`
7. comparison (`-eq`, `-ne`, `-lt`, `-le`, `-gt`, `-ge`, `-ceq`, …)
8. `-and`
9. `-or`, `-xor`
10. `=` assignment (right-associative, lowest)

### 9.3 Links

- [Parent proposal](../proposals/carbide-pwsh-subset-shell-proposal__2026-04-21__21-30-00-000000__e9c4b27a8f13.md)
- [PowerShell Language Specification (v3, authoritative)](https://learn.microsoft.com/en-us/powershell/scripting/lang-spec/chapter-01)
- [`packages/carbide-gh/`](../../packages/carbide-gh/) — reference implementation for the xterm.js host pattern
- [`packages/core/src/Terminal/CarbideConsole.cs`](../../packages/core/src/Terminal/CarbideConsole.cs) — T3 forked `System.Console` surface
