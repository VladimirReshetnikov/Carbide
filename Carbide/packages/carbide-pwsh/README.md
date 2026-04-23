# carbide-pwsh

A PowerShell-flavored shell, compiled from C# in the browser and run on Mono-WASM by
[Carbide](../../README.md). **Phase 3** (current) closes the loop on "useful scripting
language": control flow, user-defined functions, error handling, classes, enums, script
files, Carbide-compiled app invocation, regex/format/join/split/containment operators,
and a proper scope stack.

Source docs:

- Parent proposal: [carbide-pwsh-subset-shell-proposal](../../docs/proposals/carbide-pwsh-subset-shell-proposal__2026-04-21__21-30-00-000000__e9c4b27a8f13.md)
- Phase 1 plan: [carbide-pwsh-phase1-detailed-plan](../../docs/planning/carbide-pwsh-phase1-detailed-plan__2026-04-21__21-45-00-000000__a5f8c3d192e0.md)
- Phase 2 plan: [carbide-pwsh-phase2-detailed-plan](../../docs/planning/carbide-pwsh-phase2-detailed-plan__2026-04-21__22-30-00-000000__b7e2c4a9d018.md)
- Phase 3 plan (extended): [carbide-pwsh-phase3-detailed-plan](../../docs/planning/carbide-pwsh-phase3-detailed-plan__2026-04-21__23-00-00-000000__f8c3e2a9b471.md)

## Running the demo

Prerequisites: `@carbide/core` must already be built — the demo fetches its published
`_framework/` directly from `/packages/core/src/bin/Release/net10.0/publish/`.

```bash
cd src/Carbide/packages/carbide-pwsh
node scripts/serve.mjs
# -> carbide-pwsh demo server: http://127.0.0.1:34571/packages/carbide-pwsh/
```

Open that URL in any modern browser.

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
| `index.html` | xterm.js host page; fetches the ~50 C# sources, compiles, runs `project.runInteractive`. |
| `src/Program.cs` | REPL entry point with multi-line input. |
| `src/Errors/` | Exception hierarchy + `SourceLocation` + `ErrorRecord`. |
| `src/Lexer/` | Tokenizer with hyphenated-command-name folding, pipe token, keyword identifiers, all dashed operators. |
| `src/Parser/` | Recursive-descent parser producing an AST covering control flow, functions, try/catch, classes, enums. |
| `src/Runtime/` | Scope stack, coercion, operators (incl. regex/format/join/split/contains), type bridge, interpreter, script blocks, script functions, runtime classes/enums. |
| `src/Vfs/` | Virtualized filesystem (tree, paths, snapshot). |
| `src/Cmdlets/` | Cmdlet base, registry, pipeline runtime, catalog. |
| `src/Host/` | Banner, output formatter, persistent `ShellHost` with script loader + app invoker. |
| `test/` | xUnit tests spanning Phase 1, 2, and 3 surfaces. |

## Local development

```bash
# Build + unit tests:
cd src/Carbide/packages/carbide-pwsh
dotnet build src/CarbidePwsh.csproj
dotnet test test/CarbidePwsh.Tests.csproj

# Run the REPL locally (no browser):
dotnet run --project src/CarbidePwsh.csproj
```

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
`Add-Type` for inline C#, PSReadLine-style line editor (history, tab completion),
`format.ps1xml` custom views, interactive `Confirm`/`Inquire`, `Register-ObjectEvent`,
remoting, async cancellation into running scripts on Ctrl+C, `Invoke-WebRequest` /
`Invoke-RestMethod`. Phase 4+ territory.
