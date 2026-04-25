#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter()]
    [string] $OutDir = 'C:\TestData\pwsh\pwsh-github-corpus',

    [Parameter()]
    [switch] $OnlyPowerShellFiles,

    [Parameter()]
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoUrls = @(
    'https://github.com/PowerShell/PowerShell.git',
    'https://github.com/PowerShell/PowerShell-Tests.git',
    'https://github.com/dataplat/dbatools.git',
    'https://github.com/ScoopInstaller/Scoop.git',
    'https://github.com/pester/Pester.git',
    'https://github.com/microsoft/PowerShellForGitHub.git',
    'https://github.com/PowerShell/PowerShellGet.git',
    'https://github.com/dsccommunity/SqlServerDsc.git',
    'https://github.com/dsccommunity/ComputerManagementDsc.git',
    'https://github.com/actions/runner-images.git',
    'https://github.com/microsoft/azure-pipelines-tasks.git',
    'https://github.com/Azure/azure-quickstart-templates.git',
    'https://github.com/fleschutz/PowerShell.git'
)

$DownloadExts = @('.ps1', '.psm1', '.psd1', '.pssc', '.psrc', '.ps1xml', '.cdxml')
$AuditExts = @('.ps1', '.psm1', '.psd1', '.pssc', '.psrc')

function Assert-Tool([string] $Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required tool '$Name' was not found on PATH."
    }
}

function Get-RepoSlug([string] $Url) {
    $trimmed = $Url.TrimEnd('/')
    $trimmed = $trimmed -replace '\.git$',''
    $parts = $trimmed -split '/'
    if ($parts.Count -lt 2) {
        throw "Cannot parse repository URL: $Url"
    }

    return '{0}__{1}' -f $parts[$parts.Count - 2], $parts[$parts.Count - 1]
}

function Test-MatchingExtension([string] $Path, [string[]] $Extensions) {
    foreach ($ext in $Extensions) {
        if ($Path.EndsWith($ext, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Get-DirectoryOfGitPath([string] $Path) {
    $index = $Path.LastIndexOf('/')
    if ($index -lt 0) {
        return ''
    }

    return $Path.Substring(0, $index)
}

function Minimize-Directories([string[]] $Dirs) {
    $sorted = $Dirs | Sort-Object @{ Expression = { $_.Length } }, @{ Expression = { $_ } }
    $kept = [System.Collections.Generic.List[string]]::new()

    foreach ($dir in $sorted) {
        $isSubdir = $false
        foreach ($existing in $kept) {
            if ($dir.StartsWith($existing + '/', [StringComparison]::Ordinal)) {
                $isSubdir = $true
                break
            }
        }

        if (-not $isSubdir) {
            [void] $kept.Add($dir)
        }
    }

    return ,$kept.ToArray()
}

Assert-Tool git
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$env:GIT_TERMINAL_PROMPT = '0'
$manifest = [System.Collections.Generic.List[object]]::new()

foreach ($url in $RepoUrls) {
    $slug = Get-RepoSlug $url
    $dest = Join-Path $OutDir $slug
    $tmp = Join-Path $OutDir ('{0}.__tmp' -f $slug)

    Write-Host "==> $slug"

    if (Test-Path $dest) {
        if (-not $Force) {
            Write-Host "    already present; keeping existing snapshot"
            continue
        }

        Remove-Item -LiteralPath $dest -Recurse -Force
    }

    if (Test-Path $tmp) {
        Remove-Item -LiteralPath $tmp -Recurse -Force
    }

    git clone --filter=blob:none --no-checkout --depth 1 --single-branch $url $tmp | Out-Null

    Push-Location $tmp
    try {
        git config core.autocrlf false | Out-Null

        $commit = (git rev-parse HEAD).Trim()
        $allPaths = @(git ls-tree -r --name-only HEAD)
        $matchingPaths = @($allPaths | Where-Object { Test-MatchingExtension $_ $DownloadExts })
        if ($matchingPaths.Count -eq 0) {
            Write-Host '    no PowerShell-adjacent files found; skipping'
            Pop-Location
            Remove-Item -LiteralPath $tmp -Recurse -Force
            continue
        }

        $patterns = [System.Collections.Generic.List[string]]::new()
        if ($OnlyPowerShellFiles) {
            foreach ($ext in $DownloadExts) {
                [void] $patterns.Add("/*$ext")
                [void] $patterns.Add("/**/*$ext")
            }
        }
        else {
            $dirs = foreach ($path in $matchingPaths) {
                $dir = Get-DirectoryOfGitPath $path
                if ($dir) { $dir }
            }
            $dirs = @($dirs | Sort-Object -Unique)
            $dirs = Minimize-Directories $dirs

            foreach ($ext in $DownloadExts) {
                [void] $patterns.Add("/*$ext")
            }

            foreach ($dir in $dirs) {
                [void] $patterns.Add("/$dir/**")
            }
        }

        git sparse-checkout init --no-cone | Out-Null
        Set-Content -Path '.git/info/sparse-checkout' -Value $patterns -Encoding UTF8
        git checkout -f --quiet | Out-Null

        $downloadedFiles = Get-ChildItem -LiteralPath $tmp -Recurse -File | Where-Object { $_.FullName -notmatch '\\\.git(\\|$)' }
        $psAuditFiles = @($downloadedFiles | Where-Object { Test-MatchingExtension $_.FullName $AuditExts })
        $manifest.Add([pscustomobject]@{
            repoUrl = $url
            slug = $slug
            commit = $commit
            mode = if ($OnlyPowerShellFiles) { 'ps-files-only' } else { 'dirs-with-ps' }
            includedPatternCount = $patterns.Count
            downloadedFileCount = $downloadedFiles.Count
            auditFileCount = $psAuditFiles.Count
            downloadedExtensions = $DownloadExts
            auditExtensions = $AuditExts
        }) | Out-Null
    }
    finally {
        Pop-Location
    }

    $gitDir = Join-Path $tmp '.git'
    if (Test-Path $gitDir) {
        Remove-Item -LiteralPath $gitDir -Recurse -Force
    }

    Move-Item -LiteralPath $tmp -Destination $dest
    Write-Host "    done -> $dest"
}

$manifestPath = Join-Path $OutDir 'manifest.json'
$summary = [pscustomobject]@{
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    outDir = $OutDir
    repoCount = $manifest.Count
    auditFileCount = @($manifest | Measure-Object -Property auditFileCount -Sum).Sum
    repositories = $manifest
}

$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Host ''
Write-Host "Wrote manifest: $manifestPath"
Write-Host "Corpus root:    $OutDir"
