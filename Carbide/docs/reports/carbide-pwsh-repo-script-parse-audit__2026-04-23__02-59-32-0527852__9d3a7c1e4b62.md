# carbide-pwsh repo PowerShell parse audit

- Created (UTC): 2026-04-23T02:59:44Z
- Scope: tracked `*.ps1`, `*.psm1`, `*.psd1` files in `C:\Tools2\Tools`
- Reference parser: `pwsh.exe 7.6` via `[System.Management.Automation.Language.Parser]::ParseFile(...)`
- Candidate parser: `src/Carbide/packages/carbide-pwsh/parity/CarbidePwsh.Parity`
- Repository HEAD: `5b5238c6175e7040d3a770ae2bbddb3cebd215e9`

## Executive summary

The authoritative repo-wide parse audit now passes cleanly:

- Tracked PowerShell files audited: `111`
- Parseable by `pwsh.exe 7.6`: `111 / 111`
- Parseable by `carbide-pwsh`: `111 / 111`
- Remaining parse mismatches: `0`

Artifacts written during the final pass:

- `src/Carbide/packages/carbide-pwsh/parity/repo-parse-baseline.json`
- `tmp-pwsh-parse-summary.json`
- `tmp-pwsh-parse-details.json`

This task also fixed one real repository script bug that `pwsh.exe` itself rejected, so the final green baseline reflects both `carbide-pwsh` parser work and one script correction in the repo.

## Method

Inventory source:

- `git ls-files -- '*.ps1' '*.psm1' '*.psd1'`

For each tracked file:

1. Parse with the real PowerShell 7.6 parser.
2. Parse with `CarbidePwsh.Parity parse <file>`.
3. Record:
   - `PwshOk`
   - `PwshError`
   - `CarbideOk`
   - `CarbideExitCode`
   - `CarbideOutput`
   - `Mismatch`

The final inventory is intentionally based on tracked files, not ad hoc filesystem crawling. Earlier exploratory passes touched broader surfaces and were useful for feature discovery, but the numbers above are the authoritative repo result.

## Fixed mismatch catalog

### 1. Repo script bug: string variable boundary before `:`

- File: `src/Scripts/Git-PR-Log-Annotate/git-pr-log-annotate.ps1`
- Symptom: real `pwsh.exe` rejected `"$path:$pos"` because `:` immediately after a variable name is parsed as part of variable syntax.
- Fix: changed it to `"${path}:$pos"`.
- Classification: repo script bug, not a `carbide-pwsh` bug.

### 2. Grouped command arguments in command mode could not continue with `[]` or `.Member`

Representative affected files:

- `src/Mdv/docs/WebView2/official/microsoftedge-webview2samples/SampleApps/webview2_sample_uwp/deploy_uwp_sample_wcos.ps1`
- `src/Scripts/Get-FileExtendedMetadata/Test-Get-FileExtendedMetadata.ps1`
- `lib/dotnet/runtime/src/libraries/Fuzzing/DotnetFuzzing/collect-coverage.ps1`
- `src/Mdv/docs/WebView2/scripts/Acquire-WebView2Research.ps1`
- `src/Mdv/scripts/Generate-AdversarialMarkdownCorpus.ps1`

Representative shapes:

- `pushd (Get-ChildItem ...)[0].FullName`
- `-Expected (Get-FileHash ...).Hash.ToUpperInvariant()`

Observed carbide failures before the fix:

- `Unexpected token LBracket '[' in command argument.`
- `Unexpected token RParen ')'.`

Fix:

- In command-mode argument parsing, `(`-grouped arguments now flow through `ParsePostfixContinuation(...)`, so grouped expressions can be indexed and member-chained exactly like normal expressions.

Execution impact:

- This was not only a parse fix.
- Lightweight execution now works for shapes like:
  - `Write-Output (@('zero','one'))[1]`
  - `Write-Output (Get-Date -Date '2020-01-02').Year`

### 3. Member invocation needed to be whitespace-sensitive

Affected file:

- `src/Scripts/Resolve-AssemblyDependencies/Resolve-AssemblyDependencies.ps1`

Representative shape:

- `Join-Path $vd.FullName (Join-Path 'ref' $tfmFolder)`

Observed carbide behavior before the fix:

- After parsing `$vd.FullName`, carbide treated the following whitespace-separated `(` as a method-invocation argument list on `FullName`, which is not what real PowerShell does here.

Reference PowerShell behavior:

- Accepts: `Join-Path $vd.FullName (Join-Path 'ref' $tfmFolder)`
- Rejects: `$obj.Method ()`

Fix:

- Member and static invocation now require the opening `(` to be lexically adjacent to the member name.

### 4. Long integer literal suffix `L`

Affected file:

- `src/Scripts/Resolve-AssemblyDependencies/Resolve-AssemblyDependencies.ps1`

Representative shape:

- `1000000000L`

Observed carbide failure before the fix:

- `Expected ')', got Identifier 'L'.`

Fix:

- The lexer now consumes `L` / `l` as an `Int64` suffix in integer literals.

Execution impact:

- `1000L` now lexes and evaluates as a `long`.

### 5. `switch -Wildcard`

Representative repo usage:

- `lib/dotnet/runtime/src/libraries/System.Net.Http/tests/StressTests/HttpStress/load-corefx-testhost.ps1`

Fix:

- Added parser support for the `-Wildcard` switch option on `switch`.
- Added runtime wildcard matching behavior.

### 6. Pre-increment / pre-decrement

Representative repo usage:

- `lib/far/enc/tools/contrib/nightroman/Build-FarEnc.ps1`

Representative shape:

- `++$nbIndex`

Fix:

- Added parser support for pre-increment / pre-decrement.
- Added runtime update semantics.

### 7. Dynamic static member names after `::` from variables or subexpressions

Representative repo usage:

- `src/Scripts/Get-FileExtendedMetadata/Test-Get-FileExtendedMetadata.ps1`
- `src/agent-tools/agent_tools/windows_gui_tools/WindowsGuiTools.psm1`

Representative shapes:

- `[Type]::$CaptureMethod`
- `[Type]::$($storeTokens[0])`

Fix:

- Added parse support for dynamic member names after `.` and `::`.
- Added runtime dispatch for dynamic member lookup and assignment targets.

### 8. Version literals

Representative repo usage:

- `lib/pwsh/src/Modules/Windows/PSDiagnostics/PSDiagnostics.psm1`

Representative shape:

- `6.3.7600`

Fix:

- Lexer now recognizes multi-segment version literals instead of mis-tokenizing them as floats plus punctuation.

### 9. Numeric size suffixes

Representative repo usage:

- `src/Mdv/docs/WebView2/official/microsoftedge-webview2feedback/diagnostics/resources/log_collection_script.ps1`

Representative shapes:

- `1KB`
- `1MB`

Fix:

- Lexer now supports PowerShell-style size suffix multipliers for `KB`, `MB`, `GB`, `TB`, and `PB`.

### 10. Chained `-replace` / `-split` style operator sequences

Representative repo usage:

- `src/Scripts/Get-FileExtendedMetadata/Test-Get-FileExtendedMetadata.ps1`
- `lib/dotnet/runtime/src/coreclr/nativeresources/processrc.ps1`

Representative shapes:

- `$StorePath -replace '^Cert:\\', '' -split '\\'`
- chained `-replace` operators

Fix:

- Comparison-like / replace-like operators are now parsed left-associatively in the way these script shapes require.

### 11. Assignment expressions in parenthesized conditions / subexpressions

Representative repo usage:

- `src/Near/docs/reports/pwsh/ResidentHooks/ResidentHooks.psm1`

Representative shape:

- `($line = $reader.ReadLine())`

Fix:

- Added assignment-expression parsing in expression position, including inside parenthesized expressions.

### 12. Conditional operator `?:`

Representative repo usage:

- `src/Scripts/Resolve-AssemblyDependencies/Resolve-AssemblyDependencies.ps1`

Representative shapes:

- `($a.TargetFramework ? $a.TargetFramework : 'unknown')`
- `($a.Source ? $a.Source : 'unknown')`

Fix:

- Added parser and runtime support for the PowerShell conditional operator.

## Code changes

Primary implementation files:

- `src/Carbide/packages/carbide-pwsh/src/Lexer/Lexer.cs`
- `src/Carbide/packages/carbide-pwsh/src/Parser/Parser.cs`
- `src/Carbide/packages/carbide-pwsh/src/Runtime/Interpreter.cs`
- `src/Carbide/packages/carbide-pwsh/src/Runtime/Operators.cs`
- `src/Carbide/packages/carbide-pwsh/src/Parser/Ast/AstNode.cs`
- `src/Carbide/packages/carbide-pwsh/src/Parser/Ast/AstNodes.cs`
- `src/Carbide/packages/carbide-pwsh/src/Parser/Ast/ControlFlowNodes.cs`
- `src/Carbide/packages/carbide-pwsh/src/Lexer/TokenKind.cs`
- `src/Carbide/packages/carbide-pwsh/src/Lexer/Token.cs`
- `src/Carbide/packages/carbide-pwsh/parity/ParseFile.cs`

Repo script fix:

- `src/Scripts/Git-PR-Log-Annotate/git-pr-log-annotate.ps1`

## Regression coverage added

Tests added or extended in:

- `src/Carbide/packages/carbide-pwsh/test/LexerTests.cs`
- `src/Carbide/packages/carbide-pwsh/test/ParserTests.cs`
- `src/Carbide/packages/carbide-pwsh/test/InterpreterTests.cs`
- `src/Carbide/packages/carbide-pwsh/test/PipelineTests.cs`
- `src/Carbide/packages/carbide-pwsh/test/Phase3ControlFlowTests.cs`

New coverage includes:

- `L` integer suffix
- `KB` / `MB` size suffixes
- version literals
- conditional operator `?:`
- assignment expressions in parens
- grouped command arguments followed by indexer/member chains
- rejection of whitespace-separated member invocation
- pre-increment execution
- wildcard switch execution
- command-argument execution for grouped-expression postfix cases

## Validation

Final validation performed:

- Repo-wide tracked-script parse audit: `111 / 111` pwsh-parseable files also parse in `carbide-pwsh`
- `dotnet test src/Carbide/packages/carbide-pwsh/test/CarbidePwsh.Tests.csproj`
  - Result: `245` passed, `0` failed

## Remaining caveats

- This audit is parse-first. It proves that the tracked repo scripts now parse the same way as `pwsh.exe 7.6` at the file level.
- It does not claim full PowerShell 7.6 execution parity for every script in the repo.
- Where execution support was cheap and directly coupled to the parse fixes, it was implemented and covered.
- Broader pwsh runtime compatibility remains a larger, separate workstream.
