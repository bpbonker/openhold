<#
.SYNOPSIS
    Saves a PNG of a process's main window, for checking UI changes during development.
.EXAMPLE
    ./tools/screenshot-window.ps1 -ProcessName OpenholdExplorer -Output data\explorer.png
#>
param(
    [Parameter(Mandatory)] [string] $ProcessName,
    [Parameter(Mandatory)] [string] $Output
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OpenholdWindowCapture {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[void][OpenholdWindowCapture]::SetProcessDPIAware()

$process = Get-Process -Name $ProcessName | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $process) { throw "No window found for $ProcessName." }
[void][OpenholdWindowCapture]::ShowWindow($process.MainWindowHandle, 9)   # restore if minimised
[void][OpenholdWindowCapture]::SetForegroundWindow($process.MainWindowHandle)
Start-Sleep -Milliseconds 600

$rect = New-Object OpenholdWindowCapture+RECT
[void][OpenholdWindowCapture]::GetWindowRect($process.MainWindowHandle, [ref]$rect)
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
$bitmap.Save([IO.Path]::GetFullPath($Output), [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bitmap.Dispose()
Write-Host "Saved $Output ($width x $height)"
