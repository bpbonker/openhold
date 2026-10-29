<#
.SYNOPSIS
    Security checks to run before every release: known-vulnerable dependencies, a software bill of
    materials, and secrets accidentally committed.

.DESCRIPTION
    1. Lists every NuGet package (including transitive ones) with a known vulnerability; fails if there are any.
    2. Writes a software bill of materials, every package and version in every project, to -SbomPath.
    3. Scans the files git tracks for things that look like secrets (private keys, passwords in connection strings,
       tokens); fails if any are found. Known development-only values are allowed.
    Exit code 0 means all clear.
#>
param([string] $SbomPath = (Join-Path $PSScriptRoot "..\data\sbom.json"))

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repo
try {
    $failed = $false

    Write-Host "1. Known-vulnerable packages..."
    $vulnerable = dotnet list Openhold.sln package --vulnerable --include-transitive 2>&1 | Out-String
    if ($vulnerable -match "has the following vulnerable packages") {
        Write-Host $vulnerable
        $failed = $true
    } else {
        Write-Host "   None."
    }

    Write-Host "2. Software bill of materials..."
    $json = dotnet list Openhold.sln package --include-transitive --format json 2>$null | Out-String
    $report = $json | ConvertFrom-Json
    $components = foreach ($project in $report.projects) {
        foreach ($framework in $project.frameworks) {
            foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
                if ($package) {
                    [pscustomobject]@{ project = [IO.Path]::GetFileNameWithoutExtension($project.path); framework = $framework.framework
                                       package = $package.id; version = $package.resolvedVersion }
                }
            }
        }
    }
    New-Item -ItemType Directory -Force (Split-Path $SbomPath) | Out-Null
    [pscustomobject]@{ generated = (Get-Date).ToUniversalTime().ToString("o"); components = @($components) } |
        ConvertTo-Json -Depth 4 | Set-Content -Path $SbomPath -Encoding UTF8
    Write-Host "   $(@($components | Select-Object package, version -Unique).Count) distinct packages written to $SbomPath"

    Write-Host "3. Secrets in tracked files..."
    $patterns = @(
        "-----BEGIN (RSA |EC |OPENSSH |)PRIVATE KEY-----",
        "Password=(?!openhold-dev\b|\$)[^;`"'\s]{3,}",
        "(?i)(api[_-]?key|client[_-]?secret|access[_-]?token)\s*[:=]\s*[`"'][A-Za-z0-9_\-]{16,}",
        "AKIA[0-9A-Z]{16}",
        "xox[baprs]-[0-9A-Za-z-]{10,}"
    )
    # This script is skipped: it necessarily contains the patterns themselves.
    $hits = git ls-files | Where-Object { $_ -notmatch "\.(png|ico|dll|pdf|dwg|ipt|iam)$" -and $_ -ne "tools/security-check.ps1" } | ForEach-Object {
        $file = $_
        $text = Get-Content -Raw -LiteralPath $file -ErrorAction SilentlyContinue
        if ($text) {
            foreach ($pattern in $patterns) {
                if ($text -match $pattern) { "   $file matches $pattern" }
            }
        }
    }
    if ($hits) {
        $hits | ForEach-Object { Write-Host $_ }
        $failed = $true
    } else {
        Write-Host "   None found."
    }

    if ($failed) { Write-Host "Security check FAILED."; exit 1 }
    Write-Host "Security check passed."
} finally {
    Pop-Location
}
