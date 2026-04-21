# carbide-pwsh

A PowerShell-flavored shell, compiled from C# in the browser and run on Mono-WASM by
[Carbide](../../README.md). **Phase 2** (current): pipelines, command invocation, a
virtualized filesystem, ~25 curated cmdlets, script blocks, and multi-line REPL. Phase 1
shipped the expression evaluator; Phase 3+ will add control flow, functions, error
handling, and Carbide-compiled-app invocation.

Source docs:

- Parent proposal: [carbide-pwsh-subset-shell-proposal](../../docs/proposals/carbide-pwsh-subset-shell-proposal__2026-04-21__21-30-00-000000__e9c4b27a8f13.md)
- Phase 1 plan: [carbide-pwsh-phase1-detailed-plan](../../docs/planning/carbide-pwsh-phase1-detailed-plan__2026-04-21__21-45-00-000000__a5f8c3d192e0.md)
- Phase 2 plan: [carbide-pwsh-phase2-detailed-plan](../../docs/planning/carbide-pwsh-phase2-detailed-plan__2026-04-21__22-30-00-000000__b7e2c4a9d018.md)

## Running the demo

Prerequisites: the `@carbide/core` package must already be built — the demo fetches its
published `_framework/` directly from `/packages/core/src/bin/Release/net10.0/publish/`.

```bash
cd src/Carbide/packages/carbide-pwsh
node scripts/serve.mjs
# -> carbide-pwsh demo server: http://127.0.0.1:34571/packages/carbide-pwsh/
```

Open that URL in any modern browser.

## Phase 2 language surface (additions over Phase 1)

| Construct | Example |
|---|---|
| Pipeline | `cmd1 \| cmd2 \| cmd3` |
| Command | `Get-ChildItem -Path /tmp -Recurse` |
| Hyphenated command name | `Get-ChildItem`, `ConvertTo-Json`, `Set-Location` |
| Named parameter | `-Path value` |
| Switch parameter | `-Recurse`, `-Force`, `-Compress` |
| Bare-word path argument | `Set-Content /tmp/foo.json -Value hi` |
| Script block | `{ $_ -gt 2 }`, `{ param($x) $x * 2 }` |
| Assignment from pipeline | `$result = Get-ChildItem \| Sort-Object` |
| Multi-line input | open `(`, `@(`, `@{`, `{` or an unclosed string and press Enter; prompt becomes `>> ` |
| `$PWD` variable | reflects the VFS's current location |

## Cmdlet catalog

| Family | Cmdlets |
|---|---|
| Pipeline shape | `Where-Object` (`where`, `?`), `ForEach-Object` (`foreach`, `%`), `Select-Object` (`select`), `Sort-Object` (`sort`), `Group-Object` (`group`), `Measure-Object` (`measure`) |
| Output | `Write-Output` (`echo`, `write`), `Write-Host` (with `-ForegroundColor` / `-BackgroundColor`), `Write-Error`, `Out-String`, `Read-Host` |
| JSON | `ConvertTo-Json` (with `-Compress`), `ConvertFrom-Json` |
| Filesystem (VFS) | `Get-ChildItem` (`dir`, `ls`, `gci`), `Get-Content` (`cat`, `gc`, `type`), `Set-Content` (`sc`), `Add-Content` (`ac`), `New-Item` (`ni`), `Remove-Item` (`rm`, `del`, `ri`), `Test-Path`, `Set-Location` (`cd`, `sl`), `Get-Location` (`pwd`, `gl`), `Resolve-Path`, `Join-Path`, `Copy-Item` (`cp`, `copy`, `cpi`), `Move-Item` (`mv`, `move`, `mi`) |

## Virtualized filesystem

- Everything operates on an **in-memory tree** — FS cmdlets never touch the real disk.
- Default seed: `/` root with `/tmp` and `/home/user` preloaded; starting location is
  `/home/user`.
- Paths are forward-slash, case-insensitive, with `~` / `.` / `..` resolution.
- Snapshot/restore API (`VfsSnapshot.Save` / `.Load`) round-trips the tree to JSON;
  browser/Node persistence backends are wiring-ready and land in Phase 2.1.

## Example Phase 2 session

```
carbide-pwsh — Phase 2 (pipelines, VFS, cmdlets)
PS /home/user> New-Item -ItemType Directory /work
Mode  LastWriteTimeUtc Length Name
----- ---------------- ------ ----
d---- 2026-04-21 17:53        work
PS /home/user> cd /work
PS /work> @('apple','banana','cherry') | Where-Object { $_.Length -gt 5 } | Sort-Object
banana
cherry
PS /work> @(1..10) | ForEach-Object { $_ * $_ } | Measure-Object -Sum
Name  Value
----- -----
Count 10
Sum   385
PS /work> @{ repo = 'carbide'; star = 42 } | ConvertTo-Json -Compress
{"repo":"carbide","star":42}
PS /work> @(1,2,3
>> 4,5)
1
2
3
4
5
PS /work> exit
```

## Exit-gate script

The committed Phase 2 exit gate runs end-to-end in an `xUnit` integration test
(`IntegrationTests.cs`):

```powershell
Set-Location /tmp
@{ name = 'Vladimir'; langs = @('C#', 'PowerShell', 'TypeScript') } | ConvertTo-Json | Set-Content profile.json
Get-Content profile.json | ConvertFrom-Json | ForEach-Object { "Hello, $($_.name)!" }
# -> Hello, Vladimir!
```

## Layout

| path | purpose |
|---|---|
| `index.html` | xterm.js host page; fetches the ~40 C# sources, compiles, and runs `project.runInteractive({ terminal })`. |
| `src/Program.cs` | REPL entry point — banner + multi-line read loop + dispatch. |
| `src/Errors/` | `PwshException` hierarchy, `PwshIncompleteInputException`, `SourceLocation`. |
| `src/Lexer/` | Hand-rolled tokenizer with hyphenated-command-name folding, pipe token, incomplete-input detection on open strings / here-strings. |
| `src/Parser/` | Recursive-descent parser with command-mode, pipelines, script blocks, and bare-word synthesis. |
| `src/Runtime/` | Scope, coercion, operators, type-literal resolution, tree-walking interpreter, script-block closure. |
| `src/Vfs/` | Virtualized filesystem (tree, paths, snapshot). |
| `src/Cmdlets/` | Cmdlet base class, parameter binding, registry, pipeline runtime, and the Phase 2 cmdlet catalog. |
| `src/Host/` | Banner, output formatter (VfsNode-aware), persistent `ShellHost`. |
| `test/` | xUnit tests across lexer, parser, coercion, interpreter, type bridge, VFS, cmdlets, pipelines, and the exit-gate integration. |
| `scripts/serve.mjs` | Static server rooted at the Carbide repo root (port 34571). |
| `scripts/smoke.mjs` | Headless Playwright driver that asserts the REPL renders Phase 1 + Phase 2 expected outputs. |

## Local development

```bash
# Unit tests — 187 at Phase 2 landing:
cd src/Carbide/packages/carbide-pwsh/test
dotnet test CarbidePwsh.Tests.csproj

# Or run the REPL locally without the browser:
cd src/Carbide/packages/carbide-pwsh/src
dotnet run --project CarbidePwsh.csproj
```

## What's explicitly not in Phase 2

Control flow (`if`, `while`, `for`, `foreach`, `switch`), user-defined functions,
error handling (`try`/`catch`/`throw`, `$ErrorActionPreference`), regex operators
(`-match`, `-replace`, `-like`), format/join/split operators (`-f`, `-join`, `-split`),
containment operators (`-contains`, `-in`), tab completion, history, and running
Carbide-compiled apps as commands. Those land in Phase 3+.
