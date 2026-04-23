# carbide-pwsh external GitHub corpus parse audit

- Created (UTC): 2026-04-23T06:20:22Z
- Repository HEAD: e5c9260ef8608c859997cf9b4acd715e0690a93f
- Corpus root: `C:\TestData\pwsh-github-corpus`
- Manifest: `C:\TestData\pwsh-github-corpus\manifest.json`
- Audit artifacts:
  - `C:\TestData\pwsh-parse-audits\external-github-corpus\tmp-pwsh-external-parse-summary.json`
  - `C:\TestData\pwsh-parse-audits\external-github-corpus\tmp-pwsh-external-parse-details.json`
  - `C:\TestData\pwsh-parse-audits\external-github-corpus\tmp-pwsh-external-carbide-results.json`
  - `C:\TestData\pwsh-parse-audits\README.md`

## Goal

Extend the earlier repository-local PowerShell parse audit to a much larger real-world GitHub corpus, compare `carbide-pwsh` against local `pwsh.exe 7.6`, fix all feasible parser discrepancies discovered by that corpus, and preserve the downloaded scripts under `C:\TestData`.

## Corpus

The corpus was downloaded under `C:\TestData\pwsh-github-corpus` and intentionally left in place after auditing.

Sources included these repositories:

1. `dataplat/dbatools`
2. `ScoopInstaller/Scoop`
3. `pester/Pester`
4. `PowerShell/PowerShell-Tests`
5. `microsoft/PowerShellForGitHub`
6. `PowerShell/PowerShellGet`
7. `dsccommunity/SqlServerDsc`
8. `dsccommunity/ComputerManagementDsc`
9. `actions/runner-images`
10. `microsoft/azure-pipelines-tasks`
11. `Azure/azure-quickstart-templates`
12. `fleschutz/PowerShell`
13. `PowerShell/PowerShell`

The checked-in helper used for acquisition/auditing is:

- `src/Carbide/packages/carbide-pwsh/parity/Acquire-GitHubPowerShellCorpus.ps1`
- `src/Carbide/packages/carbide-pwsh/parity/Invoke-ExternalParseAudit.ps1`
- `src/Carbide/packages/carbide-pwsh/parity/BatchParseAudit.cs`

Audited file count:

- `6070` PowerShell-family files (`.ps1`, `.psm1`, `.psd1`, `.pssc`, `.psrc`)

## Final result

Final external-corpus summary:

- `pwshOkCount = 5762`
- `pwshFailedCount = 308`
- `carbideOkCount = 6056`
- `carbideFailedCount = 14`
- `mismatchCount = 294`

Most importantly for parser parity:

- Remaining `pwsh-ok / carbide-fail` files: `0`
- Remaining `pwsh-fail / carbide-fail` files: `14`
- Remaining `pwsh-fail / carbide-ok` files: `294`

So after the fixes below, every file that local `pwsh.exe 7.6` parses successfully is now also parsed successfully by `carbide-pwsh` in this corpus.

## Discrepancy catalog

### Resolved `pwsh-ok / carbide-fail` cases

The final three real parser gaps found by the corpus were:

1. `PowerShell/PowerShell/test/powershell/Language/Parser/RedirectionOperator.Tests.ps1`
   - Root cause: statement-start `1>>variable:a` was lexed as a digit-led stream redirection token instead of `Number(1)` followed by plain append redirection.
   - Fix: statement-start digit-led redirection is now disambiguated in the lexer; `1>>...` at expression/statement start lexes as number plus redirection, while command-stream redirections such as `2>file` still lex as stream redirection when they continue an existing command/pipeline stage.

2. `PowerShell/PowerShell/test/powershell/Modules/Microsoft.PowerShell.Core/Job.Tests.ps1`
   - Root cause: the parser only allowed `&` as a background suffix on command pipelines, not on expression-shaped pipelines such as a parenthesized/member-access-heavy assignment RHS.
   - Fix: expression pipelines now accept background suffixes in the same places they already accepted `|` and redirection, including assignment RHS and parenthesized expression-pipeline forms.

3. `PowerShell/PowerShell/test/powershell/Language/Parser/Parser.Tests.ps1`
   - Root cause: `${fooxyzzy`u{2195}}` was tokenized incorrectly. The lexer stopped the braced variable at the `}` belonging to the unicode escape, which flattened nested `It { ... }` scriptblocks and eventually produced an unexpected closing brace at file end.
   - Fix: braced variable lexing now understands backtick-unicode escapes and decodes them inside `${...}` variable names.

### Remaining `pwsh-fail / carbide-fail` cases

There are `14` files where both parsers fail:

- `13` files fail with `WorkflowNotSupportedInPowerShellCore`
- `1` file fails with `TerminatorExpectedAtEndOfString`

These are not Carbide-specific regressions.

### Remaining `pwsh-fail / carbide-ok` mismatches

There are `294` files that local `pwsh.exe 7.6` rejects but `carbide-pwsh` parses.

They cluster as:

- `283` with `ModuleNotFoundDuringParse`
- `9` with `TypeNotFound`
- `2` with `RequiresModuleInvalid`

Top repositories in this bucket:

- `dsccommunity/SqlServerDsc`: `165`
- `dsccommunity/ComputerManagementDsc`: `86`
- `Azure/azure-quickstart-templates`: `42`
- `PowerShell/PowerShell`: `1`

These are mostly environment-sensitive parse failures in stock `pwsh.exe 7.6`, not parser bugs in `carbide-pwsh`. In other words, the mismatch is usually caused by local module/type availability during parse-time validation, while Carbide intentionally accepts the syntax.

## Implemented fixes

### Lexer

Changed in `src/Carbide/packages/carbide-pwsh/src/Lexer/Lexer.cs`:

- Added proper scanning/decoding for backtick escapes, including `` `u{...} ``, inside braced variable names.
- Applied the same braced-variable fix both to standalone `${...}` variables and to interpolated `${...}` fragments inside double-quoted strings.
- Added statement-start disambiguation for digit-led redirection so `1>>variable:a` is no longer forced into a stream-redirection token.

### Parser

Changed in `src/Carbide/packages/carbide-pwsh/src/Parser/Parser.cs`:

- Allowed expression pipelines to accept background suffix `&`, not just command pipelines.
- Extended parenthesized expression-pipeline parsing to recognize background and redirection, not only `|`.
- Allowed assignment RHS parsing to lower expression-plus-background into the same subexpression-wrapped pipeline form already used for expression pipes/redirections.

### Runtime

Changed in `src/Carbide/packages/carbide-pwsh/src/Runtime/Interpreter.cs`:

- Unicode braced-variable names now round-trip through assignment and lookup because the decoded variable name now reaches the runtime intact.

This task did not implement full background-job execution semantics for `... &`; the work here closes parsing parity for that syntax and enables downstream runtime work without further parser changes.

## Regression coverage

Added/updated tests in:

- `src/Carbide/packages/carbide-pwsh/test/LexerTests.cs`
- `src/Carbide/packages/carbide-pwsh/test/ParserTests.cs`
- `src/Carbide/packages/carbide-pwsh/test/InterpreterTests.cs`

New regression coverage includes:

- braced unicode variable lexing
- statement-start numeric redirection lexing
- numeric expression redirection parsing
- background suffix parsing on expression assignment RHS
- nested `Describe` / `Context` / `It` scriptblock preservation with `${...`u{...}...}`
- runtime assignment/lookup of unicode braced variables

## Validation

Local validation completed with:

- `dotnet test src/Carbide/packages/carbide-pwsh/test/CarbidePwsh.Tests.csproj -c Debug --no-restore`
  - Result: `314 / 314` passing
- `pwsh -NoProfile -File src/Carbide/packages/carbide-pwsh/parity/Invoke-ExternalParseAudit.ps1`
  - Result: `0` remaining `pwsh-ok / carbide-fail` files in the `6070`-file external corpus

## Conclusion

The external GitHub corpus materially improved confidence in `carbide-pwsh` parser parity.

The important parity claim now supported by evidence is:

> For this `6070`-file real-world corpus, every file that local `pwsh.exe 7.6` parses successfully also parses successfully in `carbide-pwsh`.

The remaining mismatches are either:

- files that both parsers reject, mostly unsupported legacy workflows, or
- files that local `pwsh.exe` rejects for environment/module/type reasons while Carbide still accepts the syntax.
