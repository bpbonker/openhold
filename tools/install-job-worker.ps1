<#
.SYNOPSIS
    Installs or upgrades the Openhold job worker as a Windows service on a machine with AutoCAD installed.

.DESCRIPTION
    Run as an administrator on the job-worker machine. It:
      1. publishes Openhold.Jobs from this repository (or copies a release folder given with -From) into
         -InstallFolder, stopping the service first on an upgrade;
      2. installs the "Openhold Job Worker" service: delayed automatic start (after the network), restarts after a
         failure, runs as -ServiceAccount, and points it at -Server and -Vault;
      3. starts it. The log is written daily to <InstallFolder>\Logs (kept 90 days).

    The service account signs in to Openhold with Windows sign-in, so it must be a domain account (a group managed
    service account is best, e.g. EXAMPLE\openhold-jobs$) that is a member of the vault's "job-workers" group, and it
    must be allowed to run AutoCAD (it needs a licence, as Autodesk's own job processor does).

.EXAMPLE
    .\install-job-worker.ps1 -Server https://openhold.example.com:5443 -Vault Production -ServiceAccount 'EXAMPLE\openhold-jobs$'
#>
param(
    [Parameter(Mandatory)] [string] $Server,
    [string] $Vault,
    [Parameter(Mandatory)] [string] $ServiceAccount,
    [System.Security.SecureString] $ServiceAccountPassword,
    [string] $InstallFolder = (Join-Path $env:ProgramFiles "Openhold\Job Worker"),
    [string] $DefaultPaper,
    [string] $From
)

$ErrorActionPreference = "Stop"
$serviceName = "Openhold Job Worker"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this from an elevated PowerShell (Run as administrator)."
}
if (-not $Server.StartsWith("https://") -and -not $Server.StartsWith("http://localhost")) {
    throw "Use the server's https address, e.g. https://openhold.example.com:5443."
}

# 1. Program files. Stop first: Windows locks a running service's files.
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne "Stopped") {
    Write-Host "Stopping $serviceName for the upgrade..."
    Stop-Service -Name $serviceName -Force
    $existing.WaitForStatus("Stopped", [TimeSpan]::FromMinutes(5))   # lets a job in progress finish or be abandoned
}
New-Item -ItemType Directory -Force $InstallFolder | Out-Null
$staging = $From
if (-not $staging) {
    $staging = Join-Path ([IO.Path]::GetTempPath()) ("openhold-jobs-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
    dotnet publish (Join-Path $repo "src\Openhold.Jobs\Openhold.Jobs.csproj") -c Release -o $staging -v quiet | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Publishing the job worker failed." }
}
robocopy $staging $InstallFolder /MIR /XD Logs /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying the program files failed (robocopy exit $LASTEXITCODE)." }
New-Item -ItemType Directory -Force (Join-Path $InstallFolder "Logs") | Out-Null
$acl = Get-Acl (Join-Path $InstallFolder "Logs")
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($ServiceAccount, "Modify", "ContainerInherit, ObjectInherit", "None", "Allow")))
Set-Acl -Path (Join-Path $InstallFolder "Logs") -AclObject $acl

# 2. The service, with its command line (server, vault, options).
$exe = Join-Path $InstallFolder "OpenholdJobs.exe"
$arguments = "--server `"$Server`""
if ($Vault) { $arguments += " --vault `"$Vault`"" }
if ($DefaultPaper) { $arguments += " --default-paper $DefaultPaper" }
$binaryPath = "`"$exe`" $arguments"
if ($existing) {
    sc.exe config $serviceName binPath= $binaryPath | Out-Null
} else {
    $parameters = @{ Name = $serviceName; BinaryPathName = $binaryPath; DisplayName = $serviceName; StartupType = "Automatic"
                     Description = "Openhold job worker: publishes stamped PDFs of released drawings and other background jobs." }
    if ($ServiceAccountPassword) { $parameters.Credential = New-Object PSCredential($ServiceAccount, $ServiceAccountPassword) }
    New-Service @parameters | Out-Null
    if (-not $ServiceAccountPassword) { sc.exe config $serviceName obj= $ServiceAccount | Out-Null }
}
sc.exe config $serviceName start= delayed-auto | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null

# 3. Start, and show the first lines of today's log.
Start-Service -Name $serviceName
Start-Sleep 5
$log = Join-Path $InstallFolder ("Logs\jobs-{0:yyyy-MM-dd}.log" -f (Get-Date))
if (Test-Path $log) { Get-Content $log -Tail 5 } else { Write-Warning "No log yet in $InstallFolder\Logs; check the Windows event log if the service stops." }
Write-Host "$serviceName is $((Get-Service $serviceName).Status)."
