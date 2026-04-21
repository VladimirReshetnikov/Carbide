# carbide-pwsh

A PowerShell-flavored shell, compiled from C# in the browser and run on Mono-WASM by
[Carbide](../../README.md). Phase 1 ships an **expression evaluator**: arithmetic,
variables, string interpolation, arrays, hashtables, ranges, and .NET interop via
`[Type]::Member` — no cmdlets, no pipelines, no virtualized filesystem. Those land in
Phase 2+. The parent proposal is
[carbide-pwsh-subset-shell-proposal](../../docs/proposals/carbide-pwsh-subset-shell-proposal__2026-04-21__21-30-00-000000__e9c4b27a8f13.md);
the Phase 1 plan is at
[carbide-pwsh-phase1-detailed-plan](../../docs/planning/carbide-pwsh-phase1-detailed-plan__2026-04-21__21-45-00-000000__a5f8c3d192e0.md).

## Running the demo

Prerequisites: the `@carbide/core` package must already be built — the demo fetches its
published `_framework/` directly from `/packages/core/src/bin/Release/net10.0/publish/`.

```bash
cd src/Carbide/packages/carbide-pwsh
node scripts/serve.mjs
# -> carbide-pwsh demo server: http://127.0.0.1:34571/packages/carbide-pwsh/
```

Open that URL in any modern browser.

## Phase 1 language subset

| Category | Syntax |
|---|---|
| Integer / double literals | `42`, `0x2A`, `3.14`, `1.5e-3` |
| Single-quoted string | `'literal, no $interpolation'` |
| Double-quoted string | `"hello, $name"`, `"sum = $(1 + 2)"`, backtick escapes |
| Here-strings | `@"…"@`, `@'…'@` (closer must be at column 1) |
| Boolean / null | `$true`, `$false`, `$null` |
| Array literal | `@(1, 2, 3)` |
| Hashtable literal | `@{ a = 1; b = 2 }` |
| Variable | `$name`, `${spaces ok}`, `$env:PATH` |
| Arithmetic | `+ - * / %` (with PowerShell promotion rules) |
| Comparison | `-eq -ne -lt -le -gt -ge` (and `-ceq`/`-ieq` variants) |
| Logical | `-and -or -xor -not`, `!` |
| Range | `1..10`, `10..1` (descending), `'a'..'z'` |
| Type literal | `[int]`, `[System.Math]`, `[System.Text.StringBuilder]` |
| Cast | `[int]'42'`, `[string]3.14`, `[ConsoleColor]'Red'` |
| Static member | `[System.Math]::PI`, `[System.Math]::Sqrt(2)` |
| Instance member | `'hello'.ToUpper()`, `@(1,2,3).Length`, `$d.Year` |
| Indexer | `$arr[0]`, `$hash['key']`, `'abc'[1]` |
| Constructor | `[System.Text.StringBuilder]::new()` |
| Assignment | `$x = …`, `+=`, `-=`, `*=`, `/=`, `%=` |
| Static property assign | `[System.Console]::BackgroundColor = 'DarkBlue'` |

Explicitly **not** in Phase 1 (see the plan):

- cmdlets and pipelines (`|`)
- control flow (`if`, `while`, `for`, `foreach`, `switch`)
- functions, script blocks, `try`/`catch`
- virtualized filesystem + FS cmdlets
- running other Carbide-compiled apps as commands

## Layout

| path | purpose |
|---|---|
| `index.html` | host page; loads xterm.js + `@carbide/core`, fetches the 18 C# sources, compiles, and runs `project.runInteractive({ terminal })`. |
| `src/Program.cs` | REPL entry point — banner + `while (true) { read line; parse; evaluate; render; }`. |
| `src/Errors/` | `PwshException` hierarchy + `SourceLocation`. |
| `src/Lexer/` | Hand-rolled tokenizer. |
| `src/Parser/` | Recursive-descent parser producing an AST (`src/Parser/Ast/`). |
| `src/Runtime/` | Scope, coercion rules, binary/unary operator implementations, type-literal resolution + reflection bridge, and the tree-walking interpreter. |
| `src/Host/` | Banner, output formatter, persistent `ShellHost` (one long-lived scope across submissions). |
| `src/CarbidePwsh.csproj` | Standalone project for `dotnet build` / `dotnet run` on Windows/Linux. |
| `test/` | xUnit tests covering lexer, parser, coercion, interpreter, and type-bridge. Run with `dotnet test`. |
| `scripts/serve.mjs` | Static server rooted at the Carbide repo root (port 34571). |
| `scripts/smoke.mjs` | Headless Playwright driver that asserts the REPL renders the expected output for seven Phase 1 expressions. |

## Local development

```bash
# Unit tests (123 at Phase 1 landing):
cd src/Carbide/packages/carbide-pwsh/test
dotnet test CarbidePwsh.Tests.csproj

# Or run the REPL locally without the browser:
cd src/Carbide/packages/carbide-pwsh/src
dotnet run --project CarbidePwsh.csproj
```

Example session:

```
PS > 2 + 2
4
PS > $name = 'Vladimir'
PS > "hello, $name"
hello, Vladimir
PS > [System.Math]::Sqrt(2)
1.4142135623730951
PS > @(1,2,3,4,5) -gt 2
3
4
5
PS > exit
```

## Smoke test

```bash
node scripts/serve.mjs &
node scripts/smoke.mjs
# -> smoke: PASSED
```

The smoke test drives six input→output pairs through xterm.js and fails loudly if any
answer doesn't appear in the buffer.
