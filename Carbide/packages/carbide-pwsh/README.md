# carbide-pwsh

A PowerShell-flavored shell, compiled from C# in the browser and run on Mono-WASM by
[Carbide](../../README.md). **Phase 3** (current) closes the loop on "useful scripting
language": control flow, user-defined functions, error handling, classes, enums, script
files, Carbide-compiled app invocation, regex/format/join/split/containment operators,
and a proper scope stack. `carbide-pwsh` is now also the single public Carbide shell
endpoint in the browser: it starts with the richer pwsh prompt/editor UX, but it boots a
shared session that can enter nested `cmd` and `bash` shells and exposes one shared
virtual executable catalog across all three.

Source docs:

- Parent proposal: [carbide-pwsh-subset-shell-proposal](../../docs/proposals/carbide-pwsh-subset-shell-proposal__2026-04-21__21-30-00-000000__e9c4b27a8f13.md)
- Phase 1 plan: [carbide-pwsh-phase1-detailed-plan](../../docs/planning/carbide-pwsh-phase1-detailed-plan__2026-04-21__21-45-00-000000__a5f8c3d192e0.md)
- Phase 2 plan: [carbide-pwsh-phase2-detailed-plan](../../docs/planning/carbide-pwsh-phase2-detailed-plan__2026-04-21__22-30-00-000000__b7e2c4a9d018.md)
- Phase 3 plan (extended): [carbide-pwsh-phase3-detailed-plan](../../docs/planning/carbide-pwsh-phase3-detailed-plan__2026-04-21__23-00-00-000000__f8c3e2a9b471.md)

## Running the demo

Prerequisites:

- `@carbide/core` must already be built — the demo fetches its published `_framework/`
  directly from `/packages/core/src/bin/Release/net10.0/publish/`.
- `src/CarbidePwsh.csproj` should be built at least once so the browser host can fetch
  `SharpCompress.dll` for the shared executable catalog.

```bash
cd src/Carbide/packages/carbide-pwsh
dotnet build src/CarbidePwsh.csproj
node scripts/serve.mjs
# -> carbide-pwsh demo server: http://127.0.0.1:34571/packages/carbide-pwsh/
```

Open that URL in any modern browser.

## Shared shell session

The browser page under `packages/carbide-pwsh/` is the public entrypoint for the whole
shared shell runtime:

- typing `cmd` enters the cmd subset shell
- typing `bash` enters the bash subset shell
- typing `exit` leaves the current nested shell and returns to the previous one
- pwsh, cmd, and bash share one VFS, one environment-variable store, one current
  directory flow, and one dispatcher-backed virtual executable catalog

That means state round-trips the way you would expect in one session:

- a variable exported in bash is visible later in pwsh as `$env:NAME`
- files created in cmd or bash are immediately visible from pwsh
- shared tools such as `grep`, `findstr`, `tar`, and related VFS-backed executables are
  invokable from any shell flavor, and pwsh command discovery now surfaces them through
  `Get-Command` and tab completion

## Interactive prompt conveniences

The current prompt editor intentionally stays small and host-friendly, but it covers the
basic typing workflows you expect from day-to-day `pwsh` use:

- `Esc` clears the current input line.
- `Ctrl+C` abandons the current line, prints red `^C`, and returns to a fresh prompt.
- `UpArrow` / `DownArrow` walk recent command history.
- `Tab` completes command names from cmdlets, aliases, functions, registered apps, and
  shared virtual executables; it also completes cmdlet/function parameters, variables,
  `$env:` names, and VFS paths, quoting paths with spaces when needed. Repeated `Tab`
  cycles forward and `Shift+Tab` cycles backward.
- `LeftArrow` / `RightArrow`, `Home` / `End`, `Ctrl+A` / `Ctrl+E`, `Backspace`,
  `Delete`, and `Ctrl+L` all work in the prompt.
- `Ctrl+Left` / `Ctrl+Right` and `Alt+B` / `Alt+F` move by word; `Ctrl+Backspace`,
  `Ctrl+W`, `Alt+Backspace`, and `Alt+D` delete by word.
- `Ctrl+U` clears from the cursor to the beginning of the line, `Ctrl+K` clears from the
  cursor to the end, `Ctrl+D` deletes the character under the cursor (or ends input on an
  empty line), and `Insert` toggles overwrite mode.

## What you can write in Phase 3

### Control flow

```powershell
if ($x -gt 10) { 'big' } elseif ($x -gt 5) { 'medium' } else { 'small' }
while ($i -lt 10) { $i; $i++ }
do { $i-- } until ($i -le 0)
for ($i = 0; $i -lt 5; $i++) { $i * $i }
foreach ($name in $names) { "hello $name" }
switch ($code) { 200 { 'ok' } 404 { 'missing' } default { "?" } }
break; continue; return $value
```

### Functions

```powershell
function Greet { param([string] $name = 'world') "hello, $name" }
Greet           # "hello, world"
Greet 'V'       # "hello, V"

function Double { process { $_ * 2 } }
1..3 | Double   # 2, 4, 6

function Counter { begin { $s = 0 } process { $s += $_ } end { $s } }
1..10 | Counter # 55
```

### Error handling

```powershell
try { Risky } catch { "oops: $($_.Exception.Message)" } finally { 'cleanup' }
try { throw [System.ArgumentException]::new('bad') } catch [System.ArgumentException] { 'caught-arg' }
if (-not $?) { 'the last command failed' }
```

### Classes and enums

```powershell
class Point {
    [double] $X = 0
    [double] $Y = 0
    Point([double] $x, [double] $y) { $this.X = $x; $this.Y = $y }
    [double] Distance() { return [Math]::Sqrt($this.X * $this.X + $this.Y * $this.Y) }
}
$p = [Point]::new(3, 4); $p.Distance()   # 5

enum Color { Red = 1; Green = 2; Blue = 4 }
[Color]::Green                           # Green
[Color] 4                                # Blue
```

### Operators

```powershell
'hello world' -match 'hello (\w+)'        # True, $Matches[1] = 'world'
'hello world' -replace 'world', 'universe'# 'hello universe'
'foo.bar' -like '*.bar'                   # True
'{0:N2}' -f 3.14159                       # '3.14'
@('a','b','c') -join ','                  # 'a,b,c'
'a,b,c' -split ','                        # @('a','b','c')
@(1,2,3) -contains 2                      # True
2 -in @(1,2,3)                            # True
```

### Scripts and apps

```powershell
# Script file in the VFS:
Set-Content /tmp/greet.ps1 -Value '"hello $($args[0])"'
./tmp/greet.ps1 Vladimir                  # 'hello Vladimir'

# Dot-source (runs in caller scope):
Set-Content /tmp/init.ps1 -Value '$shared = 42'
. /tmp/init.ps1
$shared                                   # 42

# Carbide-compiled .NET app:
Register-CarbideApp -Name greet -Path /apps/hello.dll
greet Vladimir                            # runs EntryPoint("Vladimir"); $LASTEXITCODE updates
```

### Scope qualifiers

```powershell
$a = 1                           # Global (or Script if inside one)
function f { $a = 2 }; f; $a     # 1 — function's $a is a new Local
function f2 { $script:a = 2 }; f2; $a  # 2
function f3 { $global:x = 99 }   # writes to Global always
$env:PATH                        # environment variables
```

## Cmdlet catalog (Phase 3)

| Family | Cmdlets |
|---|---|
| Pipeline shape | Where-Object, ForEach-Object, Select-Object, Sort-Object, Group-Object, Measure-Object |
| Output | Write-Output, Write-Host (colored), Write-Error, Out-String, Read-Host |
| Discovery / session state | Get-Command, Get-Alias, New/Set/Remove-Alias, Get/New/Set/Remove/Clear-Variable, Get-PSDrive, Get-PSProvider, Get-Module, Import-Module, Get-Help |
| JSON | ConvertTo-Json, ConvertFrom-Json |
| Filesystem | Get-ChildItem, Get/Set/Add-Content, New-Item, `mkdir`, Remove-Item, Test-Path, Set/Get/Push/Pop-Location, Resolve/Convert-Path, Join-Path, Copy-Item, Move-Item |
| System | Start-Sleep, Get-Date, Get-Random, New-Guid, Invoke-Expression |
| App | Register-CarbideApp, Unregister-CarbideApp, Get-CarbideApp |

Aliases follow PowerShell conventions: `ls`, `dir`, `cat`, `cd`, `pwd`, `mv`, `cp`, `rm`,
`sort`, `where`, `foreach`, `select`, `group`, `measure`, `echo`, `sleep`, `iex`, `gcm`,
`gal`, `gv`, `set`, `md`, `mkdir`, `rd`, `rmdir`, `pushd`, `popd`, plus the broader
PowerShell 7.6 builtin alias surface as recognized command-discovery metadata.

## Automatic variables

| Variable | Meaning |
|---|---|
| `$_` / `$PSItem` | Pipeline current item; also the `ErrorRecord` inside `catch` |
| `$Matches` | Capture groups from the last successful `-match` |
| `$?` | `True` if the last command succeeded |
| `$LASTEXITCODE` | Exit code from the last Carbide-compiled app |
| `$PWD` | Current VFS location |
| `$HOME` | `/home/user` |
| `$args` | Positional arguments to the current script/function |
| `$PSScriptRoot` | Directory of the running script |
| `$PSCommandPath` | Full path of the running script |
| `$ErrorActionPreference` | `Continue` (default), `Stop`, `SilentlyContinue` |
| `$PSVersionTable` | `PSVersion`, `Edition`, `Phase` |
| `$this` | Current instance inside a class method |

## Layout

| path | purpose |
|---|---|
| `index.html` | Public browser page for the pwsh-first shared shell session; imports the shared browser manifest/helper, compiles the runtime, and runs `project.runInteractive`. |
| `src/Program.cs` | pwsh-first outer runner: keeps `PwshPromptEditor` for pwsh, but pushes/pops nested cmd/bash kernels on one shared shell stack. |
| `src/Errors/` | Exception hierarchy + `SourceLocation` + `ErrorRecord`. |
| `src/Lexer/` | Tokenizer with hyphenated-command-name folding, pipe token, keyword identifiers, all dashed operators. |
| `src/Parser/` | Recursive-descent parser producing an AST covering control flow, functions, try/catch, classes, enums. |
| `src/Runtime/` | Scope stack, coercion, operators (incl. regex/format/join/split/contains), type bridge, interpreter, script blocks, script functions, runtime classes/enums. |
| `src/Vfs/` | Virtualized filesystem (tree, paths, snapshot). |
| `src/Cmdlets/` | Cmdlet base, registry, pipeline runtime, catalog. |
| `src/Host/` | Banner, output formatter, persistent `ShellHost`, pwsh prompt editor, script loader, and shared-command discovery bridge. |
| `test/` | xUnit tests spanning Phase 1, 2, and 3 surfaces. |

## Local development

```bash
# Build + unit tests:
cd src/Carbide/packages/carbide-pwsh
dotnet build src/CarbidePwsh.csproj
dotnet test test/CarbidePwsh.Tests.csproj

# Run the pwsh-first shared session locally (no browser):
dotnet run --project src/CarbidePwsh.csproj
```

### Browser interactive tests

The package has a browser-level harness that starts the real demo endpoint, launches
Chromium, waits for the xterm-hosted pwsh prompt, and sends keyboard/paste input through
the terminal:

```bash
cd src/Carbide/packages/core
npm run build:dotnet
npm run build:ts

cd ../carbide-pwsh
npm run test:browser:dotnet
npm run test:browser:dotnet-nested
```

The operational contract lives in
[`carbide-pwsh browser interactive test infrastructure`](../../docs/carbide-pwsh-browser-interactive-test-infrastructure__2026-04-25__22-33-27__a819b7f46f3d.md).

Sample Phase 3 session:

```
carbide-pwsh — Phase 3 (flow, functions, errors, classes, apps)
PS /home/user> function Retry { param([scriptblock] $Action, [int] $Times = 3) `
>>     for ($i = 1; $i -le $Times; $i++) { `
>>         try { return & $Action } catch { if ($i -eq $Times) { throw } } `
>>     } }
PS /home/user> class Counter { [int] $N = 0; [int] Inc() { $this.N++; return $this.N } }
PS /home/user> $c = [Counter]::new()
PS /home/user> $greetings = foreach ($n in @('alice','bob','carol')) {
>>     "$($c.Inc()). Hello, $($n -replace '^(.)', { $_.Groups[1].Value.ToUpper() })!"
>> }
PS /home/user> $greetings -join "`n" | Set-Content /tmp/greetings.txt
PS /home/user> Get-Content /tmp/greetings.txt
1. Hello, Alice!
2. Hello, Bob!
3. Hello, Carol!
PS /home/user> exit
```

## What's not in Phase 3 (still deferred)

Class inheritance, static class members beyond trivial, property getters/setters with
bodies, method overloading inside user classes, `using module`/`Import-Module`,
`Add-Type` for inline C#, a full PSReadLine-style editor (persistent history, predictive
completion, syntax coloring, reverse search, member completion, multi-line-aware editing),
`format.ps1xml` custom views, interactive `Confirm`/`Inquire`, `Register-ObjectEvent`,
remoting, async cancellation into running scripts on Ctrl+C, `Invoke-WebRequest` /
`Invoke-RestMethod`. Phase 4+ territory.
