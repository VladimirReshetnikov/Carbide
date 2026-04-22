# carbide-pwsh vs pwsh.exe 7.6 — parity report

- Reference: `C:\Program Files\PowerShell\7\pwsh.exe`
- Candidate: in-process `CarbidePwsh.Host.ShellHost`
- Scenarios: 64

## int arithmetic  ✅

```powershell
2 + 2
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
4⟨CR⟩

```

</details>

## string concat  ✅

```powershell
'a' + 'b'
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
ab⟨CR⟩

```

</details>

## range 1..5  ✅

```powershell
1..5
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
1⟨CR⟩
2⟨CR⟩
3⟨CR⟩
4⟨CR⟩
5⟨CR⟩

```

</details>

## array literal  ✅

```powershell
'hello', 'world'
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
hello⟨CR⟩
world⟨CR⟩

```

</details>

## hashtable literal  ✅

```powershell
@{ a = 1; b = 2 }
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
⟨CR⟩
⟨ESC⟩[32;1mName                          ⟨ESC⟩[0m⟨ESC⟩[32;1m Value⟨ESC⟩[0m⟨CR⟩
⟨ESC⟩[32;1m----                          ⟨ESC⟩[0m ⟨ESC⟩[32;1m-----⟨ESC⟩[0m⟨CR⟩
a                              1⟨CR⟩
b                              2⟨CR⟩
⟨CR⟩

```

</details>

## int max value  ✅

```powershell
[int]::MaxValue
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
2147483647⟨CR⟩

```

</details>

## string length  ✅

```powershell
'hello'.Length
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
5⟨CR⟩

```

</details>

## boolean  ✅

```powershell
$true
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
True⟨CR⟩

```

</details>

## null  ✅

```powershell
$null
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```

```

</details>

## double  ✅

```powershell
3.14
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
3.14⟨CR⟩

```

</details>

## if-else true  ✅

```powershell
if ($true) { 'yes' } else { 'no' }
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
yes⟨CR⟩

```

</details>

## if-else false  ✅

```powershell
if ($false) { 'yes' } else { 'no' }
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
no⟨CR⟩

```

</details>

## for 1..3 squared  ✅

```powershell
foreach ($x in 1..3) { $x * $x }
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
1⟨CR⟩
4⟨CR⟩
9⟨CR⟩

```

</details>

## function def + call  ✅

```powershell
function Add ($a, $b) { $a + $b }; Add 3 4
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
7⟨CR⟩

```

</details>

## Write-Output string  ✅

```powershell
Write-Output 'hello'
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
hello⟨CR⟩

```

</details>

## Write-Output numeric  ✅

```powershell
Write-Output 42
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
42⟨CR⟩

```

</details>

## Write-Output array  ✅

```powershell
Write-Output 1,2,3
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
1⟨CR⟩
2⟨CR⟩
3⟨CR⟩

```

</details>

## Get-Date format  ✅

```powershell
Get-Date -Format 'yyyy-MM-dd'
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
2026-04-22⟨CR⟩

```

</details>

## pipeline map  ✅

```powershell
1..5 | ForEach-Object { $_ * $_ }
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
1⟨CR⟩
4⟨CR⟩
9⟨CR⟩
16⟨CR⟩
25⟨CR⟩

```

</details>

## pipeline filter  ✅

```powershell
1..5 | Where-Object { $_ -gt 3 }
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
4⟨CR⟩
5⟨CR⟩

```

</details>

## pipeline select  ✅

```powershell
1..3 | Select-Object -First 2
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
1⟨CR⟩
2⟨CR⟩

```

</details>

## pipeline sort  ✅

```powershell
@(5,3,1,4,2) | Sort-Object
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
1⟨CR⟩
2⟨CR⟩
3⟨CR⟩
4⟨CR⟩
5⟨CR⟩

```

</details>

## double-quote interp  ✅

```powershell
$n = 3; "n is $n"
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
n is 3⟨CR⟩

```

</details>

## single-quote literal  ✅

```powershell
'$n is literal'
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
$n is literal⟨CR⟩

```

</details>

## string format -f  ✅

```powershell
'{0:X}' -f 255
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
FF⟨CR⟩

```

</details>

## string replace  ✅

```powershell
'hello world' -replace 'world', 'universe'
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
hello universe⟨CR⟩

```

</details>

## string match  ✅

```powershell
'hello world' -match 'hello'
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
True⟨CR⟩

```

</details>

## comparison -eq  ✅

```powershell
3 -eq 3
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
True⟨CR⟩

```

</details>

## comparison -gt  ✅

```powershell
5 -gt 3
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
True⟨CR⟩

```

</details>

## array -contains  ✅

```powershell
@(1,2,3) -contains 2
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
True⟨CR⟩

```

</details>

## array -join  ✅

```powershell
@('a','b','c') -join ','
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
a,b,c⟨CR⟩

```

</details>

## pstypename  ✅

```powershell
(42).GetType().Name
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
Int32⟨CR⟩

```

</details>

## string to int  ✅

```powershell
[int] '42'
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
42⟨CR⟩

```

</details>

## empty array  ✅

```powershell
@()
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```

```

</details>

## empty pipeline  ✅

```powershell
@() | ForEach-Object { $_ }
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```

```

</details>

## try-catch throw  ✅

```powershell
try { throw 'boom' } catch { "caught: $($_.Exception.Message)" }
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
caught: boom⟨CR⟩

```

</details>

## nested array index  ✅

```powershell
@(@(1,2), @(3,4))[1][0]
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
3⟨CR⟩

```

</details>

## range length  ✅

```powershell
(1..10).Length
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
10⟨CR⟩

```

</details>

## string split  ✅

```powershell
'a,b,c' -split ','
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
a⟨CR⟩
b⟨CR⟩
c⟨CR⟩

```

</details>

## sort desc  ✅

```powershell
5,3,1,4,2 | Sort-Object -Descending
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
5⟨CR⟩
4⟨CR⟩
3⟨CR⟩
2⟨CR⟩
1⟨CR⟩

```

</details>

## where pipe  ✅

```powershell
1..3 | Where-Object { $_ -ne 2 }
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
1⟨CR⟩
3⟨CR⟩

```

</details>

## ordered hashtable  ✅

```powershell
[ordered]@{a=1; b=2; c=3}
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
⟨CR⟩
⟨ESC⟩[32;1mName                          ⟨ESC⟩[0m⟨ESC⟩[32;1m Value⟨ESC⟩[0m⟨CR⟩
⟨ESC⟩[32;1m----                          ⟨ESC⟩[0m ⟨ESC⟩[32;1m-----⟨ESC⟩[0m⟨CR⟩
a                              1⟨CR⟩
b                              2⟨CR⟩
c                              3⟨CR⟩
⟨CR⟩

```

</details>

## switch 1  ✅

```powershell
switch (2) { 1 { 'one' } 2 { 'two' } default { 'other' } }
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
two⟨CR⟩

```

</details>

## math mod  ✅

```powershell
10 % 3
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
1⟨CR⟩

```

</details>

## math div  ✅

```powershell
10 / 3
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
3.33333333333333⟨CR⟩

```

</details>

## string interp quote  ✅

```powershell
$x = 'world'; "hello, $x!"
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
hello, world!⟨CR⟩

```

</details>

## string mult  ✅

```powershell
'=' * 5
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
=====⟨CR⟩

```

</details>

## negative index  ✅

```powershell
(1..5)[-1]
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
5⟨CR⟩

```

</details>

## slice 1..3  ✅

```powershell
(1..5)[1..3]
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
2⟨CR⟩
3⟨CR⟩
4⟨CR⟩

```

</details>

## explicit array  ✅

```powershell
@(1)
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
1⟨CR⟩

```

</details>

## single-elem count  ✅

```powershell
@(1).Count
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
1⟨CR⟩

```

</details>

## nested hash access  ✅

```powershell
(@{a=@{b=42}}).a.b
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
42⟨CR⟩

```

</details>

## set and read var  ✅

```powershell
$x = 42; $x
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
42⟨CR⟩

```

</details>

## multi-assign  ✅

```powershell
$a, $b = 1, 2; "$a,$b"
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
1,2⟨CR⟩

```

</details>

## env read via $env  ✅

```powershell
$env:PARITY_ONE = 'hi'; $env:PARITY_ONE
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
hi⟨CR⟩

```

</details>

## env read via Get-Item  ✅

```powershell
$env:PARITY_TWO = 'ok'; (Get-Item Env:PARITY_TWO).Value
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
ok⟨CR⟩

```

</details>

## env Test-Path exists  ✅

```powershell
$env:PARITY_THREE = 'x'; Test-Path Env:PARITY_THREE
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
True⟨CR⟩

```

</details>

## env Test-Path absent  ✅

```powershell
Test-Path Env:NEVER_SET_ZZZZ999
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
False⟨CR⟩

```

</details>

## variable via $var:  ✅

```powershell
$xv = 7; $variable:xv
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
7⟨CR⟩

```

</details>

## variable Get-Item  ✅

```powershell
$xvi = 7; (Get-Item Variable:xvi).Value
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
7⟨CR⟩

```

</details>

## cd Variable then pwd  ✅

```powershell
Set-Location Variable:; (Get-Location).ToString()
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
Variable:\⟨CR⟩

```

</details>

## cd Function then pwd  ✅

```powershell
Set-Location Function:; (Get-Location).ToString()
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
Function:\⟨CR⟩

```

</details>

## cd Alias then pwd  ✅

```powershell
Set-Location Alias:; (Get-Location).ToString()
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
Alias:\⟨CR⟩

```

</details>

## cd Env then pwd  ✅

```powershell
Set-Location Env:; (Get-Location).ToString()
```

<details><summary>Output (identical after ANSI-stripping)</summary>

```
Env:\⟨CR⟩

```

</details>

