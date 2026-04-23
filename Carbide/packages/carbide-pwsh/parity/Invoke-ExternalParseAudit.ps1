#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter()]
    [string] $CorpusRoot = 'C:\TestData\pwsh-github-corpus',

    [Parameter()]
    [string] $SummaryPath = 'C:\TestData\pwsh-parse-audits\external-github-corpus\tmp-pwsh-external-parse-summary.json',

    [Parameter()]
    [string] $DetailsPath = 'C:\TestData\pwsh-parse-audits\external-github-corpus\tmp-pwsh-external-parse-details.json',

    [Parameter()]
    [string] $CarbideResultsPath = 'C:\TestData\pwsh-parse-audits\external-github-corpus\tmp-pwsh-external-carbide-results.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $CorpusRoot)) {
    throw "Corpus root not found: $CorpusRoot"
}

foreach ($path in @($SummaryPath, $DetailsPath, $CarbideResultsPath)) {
    $directory = Split-Path -Parent $path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }
}

$auditExts = @('.ps1', '.psm1', '.psd1', '.pssc', '.psrc')
$allFiles = Get-ChildItem -LiteralPath $CorpusRoot -Recurse -File | Where-Object {
    $name = $_.Name
    foreach ($ext in $auditExts) {
        if ($name.EndsWith($ext, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
} | Sort-Object FullName

if ($allFiles.Count -eq 0) {
    throw "No auditable PowerShell files found under $CorpusRoot"
}

$fileListPath = [System.IO.Path]::ChangeExtension($CarbideResultsPath, '.files.txt')
$allFiles.FullName | Set-Content -LiteralPath $fileListPath -Encoding UTF8

$parityProject = 'C:\Tools2\Tools\src\Carbide\packages\carbide-pwsh\parity\CarbidePwsh.Parity.csproj'
$carbideCommand = @(
    'run',
    '--project', $parityProject,
    '--',
    'audit',
    $fileListPath,
    '--write-json',
    $CarbideResultsPath
)

Write-Host "Running carbide batch parser over $($allFiles.Count) files..."
dotnet @carbideCommand | Out-Host

$carbidePayload = Get-Content -LiteralPath $CarbideResultsPath -Raw | ConvertFrom-Json -Depth 6
$carbideByPath = @{}
foreach ($entry in $carbidePayload.Files) {
    $carbideByPath[$entry.Path] = $entry
}

$details = [System.Collections.Generic.List[object]]::new()
$pwshOkCount = 0
$carbideOkCount = 0
$mismatchCount = 0
$pwshFailedCount = 0
$carbideFailedCount = 0

foreach ($file in $allFiles) {
    $tokens = $null
    $errors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref] $tokens, [ref] $errors)

    $pwshOk = ($errors.Count -eq 0)
    if ($pwshOk) { $pwshOkCount++ } else { $pwshFailedCount++ }

    $carbide = $carbideByPath[$file.FullName]
    $carbideOk = [bool] $carbide.CarbideOk
    if ($carbideOk) { $carbideOkCount++ } else { $carbideFailedCount++ }

    $pwshError = $null
    if (-not $pwshOk) {
        $firstError = $errors[0]
        $pwshError = [pscustomobject]@{
            message = $firstError.Message
            errorId = $firstError.ErrorId
            line = $firstError.Extent.StartLineNumber
            column = $firstError.Extent.StartColumnNumber
            text = $firstError.Extent.Text
        }
    }

    $mismatch = $pwshOk -ne $carbideOk
    if ($mismatch) {
        $mismatchCount++
    }

    $details.Add([pscustomobject]@{
        path = $file.FullName
        relativePath = [System.IO.Path]::GetRelativePath($CorpusRoot, $file.FullName)
        extension = $file.Extension
        pwshOk = $pwshOk
        pwshError = $pwshError
        carbideOk = $carbideOk
        carbideError = if ($carbideOk) { $null } else {
            [pscustomobject]@{
                kind = $carbide.ErrorKind
                message = $carbide.ErrorMessage
                line = $carbide.Line
                column = $carbide.Column
                offset = $carbide.Offset
                context = $carbide.Context
            }
        }
        mismatch = $mismatch
    }) | Out-Null
}

$summary = [pscustomobject]@{
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    corpusRoot = $CorpusRoot
    totalFiles = $allFiles.Count
    pwshOkCount = $pwshOkCount
    pwshFailedCount = $pwshFailedCount
    carbideOkCount = $carbideOkCount
    carbideFailedCount = $carbideFailedCount
    mismatchCount = $mismatchCount
    mismatchFiles = @($details | Where-Object mismatch | Select-Object -ExpandProperty path)
}

$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $SummaryPath -Encoding UTF8
$details | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $DetailsPath -Encoding UTF8

Write-Host ''
Write-Host "Summary written: $SummaryPath"
Write-Host "Details written: $DetailsPath"
Write-Host "Carbide raw results: $CarbideResultsPath"
Write-Host "Files audited: $($allFiles.Count)"
Write-Host "Mismatches: $mismatchCount"
