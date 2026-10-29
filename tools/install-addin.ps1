<#
.SYNOPSIS
    Builds an Openhold CAD add-in and installs it so the application loads it at every startup.

.DESCRIPTION
    -App AutoCAD   AutoCAD 2025/2026 and verticals (AutoCAD Electrical, Mechanical)
    -App Inventor  Inventor 2025/2026

    Installs as a bundle in the current user's ApplicationPlugins folder (no admin rights), or with
    -AllUsers under Program Files, a location both applications trust by default.

    The application locks the add-in's DLLs while it runs, so the bundle is only replaced while it is
    closed. With -WaitForClose the add-in is built first, copied the moment the application exits, and a
    message says when it is safe to start it again. Remove with -Uninstall.
#>
param(
    [Parameter(Mandatory)] [ValidateSet("AutoCAD", "Inventor")] [string] $App,
    [switch] $AllUsers,
    [switch] $Uninstall,
    [switch] $WaitForClose
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")

$target = @{
    AutoCAD  = @{ Project = "Openhold.AutoCAD.Plugin"; Bundle = "Openhold.bundle"; Manifest = "deploy\autocad\PackageContents.xml"; Processes = @("acad", "accoreconsole") }
    Inventor = @{ Project = "Openhold.Inventor.AddIn"; Bundle = "OpenholdInventor.bundle"; Manifest = "deploy\inventor\Openhold.Inventor.addin"; Processes = @("Inventor") }
}[$App]

$pluginsRoot = if ($AllUsers) { Join-Path $env:ProgramFiles "Autodesk\ApplicationPlugins" } else { Join-Path $env:APPDATA "Autodesk\ApplicationPlugins" }
$bundle = Join-Path $pluginsRoot $target.Bundle

function Test-Running { [bool](Get-Process -Name $target.Processes -ErrorAction SilentlyContinue) }

Add-Type -AssemblyName System.Windows.Forms
function Show-Message([string] $text) {
    if ($WaitForClose) { [System.Windows.Forms.MessageBox]::Show($text, "Openhold") | Out-Null }
    Write-Host $text
}

if ($Uninstall) {
    if (Test-Running) { Write-Host "Close $App first, then run this again. Nothing was changed."; exit 1 }
    if (Test-Path $bundle) { Remove-Item -LiteralPath $bundle -Recurse -Force }
    Write-Host "Openhold removed from $App."
    return
}

# Build before touching anything, so the copy itself takes well under a second.
dotnet build (Join-Path $repo "src\$($target.Project)\$($target.Project).csproj") -c Release -v quiet | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Build failed. Nothing was changed." }
$output = Join-Path $repo "src\$($target.Project)\bin\Release\net8.0-windows\win-x64"

while (Test-Running) {
    if (-not $WaitForClose) {
        Write-Host "$App is running, so Openhold can't be updated yet. Close $App (save your work first), then run this again. Nothing was changed."
        exit 1
    }
    Write-Host "Waiting for $App to close..."
    # Wait for the processes to finish exiting (closing the window isn't enough).
    Get-Process -Name $target.Processes -ErrorAction SilentlyContinue | Wait-Process -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    if (Test-Running) {
        Show-Message "$App was started again before Openhold could update. Please close $App once more and wait for the 'Openhold updated' message before reopening it."
    }
}

# Stage the new bundle, then swap by renaming. A rename fails as a whole if anything is still locked,
# so the add-in is never left half-installed.
$staging = "$bundle.new"
$old = "$bundle.old"
foreach ($dir in $staging, $old) { if (Test-Path $dir) { Remove-Item -LiteralPath $dir -Recurse -Force } }
New-Item -ItemType Directory -Force (Join-Path $staging "Contents\Win64") | Out-Null
Copy-Item (Join-Path $repo $target.Manifest) $staging
Copy-Item (Join-Path $output "*") (Join-Path $staging "Contents\Win64") -Recurse

if (Test-Path $bundle) { Rename-Item -LiteralPath $bundle -NewName (Split-Path $old -Leaf) }
Rename-Item -LiteralPath $staging -NewName (Split-Path $bundle -Leaf)
if (Test-Path $old) { Remove-Item -LiteralPath $old -Recurse -Force -ErrorAction SilentlyContinue }
# Pilot builds were installed under the working name Strata; remove them so the add-in never loads twice.
$pilot = Join-Path $pluginsRoot ($target.Bundle -replace '^Openhold', 'Strata')
if (Test-Path $pilot) { Remove-Item -LiteralPath $pilot -Recurse -Force -ErrorAction SilentlyContinue }

Show-Message "Openhold updated for $App. You can start $App now."
