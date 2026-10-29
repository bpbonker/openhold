<#
.SYNOPSIS
    Indexes a DWG headlessly with accoreconsole and the Openhold AutoCAD plugin, writing JSON.

.DESCRIPTION
    The drawing is always copied to a temporary folder first and the copy is indexed, so the source
    file (for example in a live workspace) can never be modified. AutoCAD runs isolated
    (/isolate), so the SECURELOAD change needed to load the plugin never touches the user's real
    AutoCAD profile.

.EXAMPLE
    ./tools/index-dwg.ps1 -Drawing .\testdata\PRJ01-001.dwg -Output .\testdata\PRJ01-001.json
#>
param(
    [Parameter(Mandatory)] [string] $Drawing,
    [Parameter(Mandatory)] [string] $Output,
    [string] $AutoCadDir = "C:\Program Files\Autodesk\AutoCAD 2025",
    [string] $Plugin,
    [int] $TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
if (-not $Plugin) {
    # $PSScriptRoot is not available in param() defaults on Windows PowerShell 5.1.
    $Plugin = Join-Path $PSScriptRoot "..\src\Openhold.AutoCAD.Plugin\bin\Debug\net8.0-windows\win-x64\Openhold.AutoCAD.Plugin.dll"
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("openhold-index-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $copy = Join-Path $work (Split-Path $Drawing -Leaf)
    Copy-Item -LiteralPath $Drawing -Destination $copy
    $script = Join-Path $work "index.scr"
    Set-Content -Path $script -Encoding ascii -Value @(
        "SECURELOAD 0",
        "NETLOAD `"$((Resolve-Path $Plugin).Path)`"",
        "OPENHOLDINDEX",
        "_.QUIT",
        "_Y",
        "")

    $env:OPENHOLD_INDEX_OUT = [IO.Path]::GetFullPath($Output)
    $process = Start-Process -FilePath (Join-Path $AutoCadDir "accoreconsole.exe") `
        -ArgumentList "/i `"$copy`" /s `"$script`" /l en-US /isolate OpenholdIndexer `"$work\profile`"" `
        -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $work "accore.log")
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process | Stop-Process -Force
        throw "accoreconsole did not finish within $TimeoutSeconds s"
    }
    if (-not (Test-Path $env:OPENHOLD_INDEX_OUT)) {
        throw "No index produced. accoreconsole log:`n$(Get-Content (Join-Path $work 'accore.log') -Encoding Unicode -Raw)"
    }
    Write-Host "Indexed $Drawing -> $Output"
}
finally {
    Remove-Item Env:\OPENHOLD_INDEX_OUT -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
