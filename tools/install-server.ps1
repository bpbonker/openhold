<#
.SYNOPSIS
    Installs or upgrades the Openhold server as a Windows service.

.DESCRIPTION
    Run as an administrator on the server. It:
      1. publishes Openhold.Server from this repository (or copies a release folder given with -From)
         into -InstallFolder, keeping the existing appsettings.Production.json;
      2. creates appsettings.Production.json from deploy\appsettings.Production.example.json the first time;
      3. creates the data folder and gives only the service account and administrators access;
      4. installs the "Openhold Server" service: automatic start, restarts after a failure, runs as -ServiceAccount
         (a group managed service account is best, e.g. EXAMPLE\openhold$);
      5. puts the database password in the service's own environment (PGPASSWORD), never in a settings file;
      6. opens the HTTPS port in Windows Firewall, starts the service and waits until it answers.

    An upgrade (the same command again) stops the service, replaces the program files, and starts it; the server
    upgrades its databases itself on start. Take a backup first (tools\schedule-backup.ps1 or openhold-admin backup).

    For Windows sign-in over HTTPS, register the service principal name once (needs a domain admin):
        setspn -S HTTP/<server's DNS name> <service account>

.EXAMPLE
    .\install-server.ps1 -DataFolder D:\OpenholdData -ServiceAccount 'EXAMPLE\openhold$' -DatabasePassword (Read-Host -AsSecureString)
#>
param(
    [string] $InstallFolder = (Join-Path $env:ProgramFiles "Openhold\Server"),
    [Parameter(Mandatory)] [string] $DataFolder,
    [string] $ServiceAccount = "NT AUTHORITY\LocalService",
    [System.Security.SecureString] $ServiceAccountPassword,
    [System.Security.SecureString] $DatabasePassword,
    [int] $Port = 5443,
    [string] $From
)

$ErrorActionPreference = "Stop"
$serviceName = "Openhold Server"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this from an elevated PowerShell (Run as administrator)."
}

# 1. Program files. Stop first: Windows locks a running service's files.
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne "Stopped") {
    Write-Host "Stopping $serviceName for the upgrade..."
    Stop-Service -Name $serviceName -Force
    $existing.WaitForStatus("Stopped", [TimeSpan]::FromMinutes(2))
}
New-Item -ItemType Directory -Force $InstallFolder | Out-Null
$staging = Join-Path ([IO.Path]::GetTempPath()) ("openhold-install-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
if ($From) {
    $staging = $From
} else {
    dotnet publish (Join-Path $repo "src\Openhold.Server\Openhold.Server.csproj") -c Release -o $staging -v quiet | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Publishing the server failed." }
}
# Mirror the program, but never the production settings or anything under data.
robocopy $staging $InstallFolder /MIR /XF appsettings.Production.json /XD data /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying the program files failed (robocopy exit $LASTEXITCODE)." }

# 2. Settings, created once from the example for the administrator to complete.
$settings = Join-Path $InstallFolder "appsettings.Production.json"
if (-not (Test-Path $settings)) {
    Copy-Item (Join-Path $repo "deploy\appsettings.Production.example.json") $settings
    Write-Warning "Created $settings from the example. Edit it (certificate, domains, vaults, data paths) and run this again."
    return
}

# 3. Data folder, readable only by the service and administrators (it holds the files and the signing key).
foreach ($sub in "", "chunks", "keys", "logs") { New-Item -ItemType Directory -Force (Join-Path $DataFolder $sub) | Out-Null }
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
$inherit = [Security.AccessControl.InheritanceFlags]"ContainerInherit, ObjectInherit"
foreach ($who in "BUILTIN\Administrators", "NT AUTHORITY\SYSTEM", $ServiceAccount) {
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($who, "Modify", $inherit, "None", "Allow")))
}
Set-Acl -Path $DataFolder -AclObject $acl
# The settings file can hold sensitive values too: the service may read it, ordinary users may not.
$settingsAcl = New-Object Security.AccessControl.FileSecurity
$settingsAcl.SetAccessRuleProtection($true, $false)
foreach ($who in "BUILTIN\Administrators", "NT AUTHORITY\SYSTEM") {
    $settingsAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($who, "FullControl", "Allow")))
}
$settingsAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($ServiceAccount, "Read", "Allow")))
Set-Acl -Path $settings -AclObject $settingsAcl

# 4. The service: automatic start, restart after failures (1 minute, three times a day).
$exe = Join-Path $InstallFolder "Openhold.Server.exe"
if (-not $existing) {
    $credential = $null
    if ($ServiceAccountPassword) { $credential = New-Object PSCredential($ServiceAccount, $ServiceAccountPassword) }
    $parameters = @{ Name = $serviceName; BinaryPathName = "`"$exe`""; DisplayName = $serviceName; StartupType = "Automatic"
                     Description = "Openhold engineering vault: files, check-out, lifecycles and sign-in." }
    if ($credential) { $parameters.Credential = $credential }
    New-Service @parameters | Out-Null
    if (-not $credential) { sc.exe config $serviceName obj= $ServiceAccount | Out-Null }
}
sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

# 5. The service's environment: production mode, and the database password kept out of files.
# Variables an administrator added (e.g. Openhold__Replication__Password) are kept.
$serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
$current = @((Get-ItemProperty -Path $serviceKey -Name Environment -ErrorAction SilentlyContinue).Environment)
$environment = @("ASPNETCORE_ENVIRONMENT=Production") + @($current | Where-Object { $_ -and $_ -notlike "ASPNETCORE_ENVIRONMENT=*" -and ($_ -notlike "PGPASSWORD=*" -or -not $DatabasePassword) })
if ($DatabasePassword) {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($DatabasePassword))
    $environment += "PGPASSWORD=$plain"
}
New-ItemProperty -Path $serviceKey -Name Environment -PropertyType MultiString -Value $environment -Force | Out-Null

# 6. Firewall, start, and wait until it answers.
if (-not (Get-NetFirewallRule -DisplayName "Openhold Server (HTTPS)" -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName "Openhold Server (HTTPS)" -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow | Out-Null
}
Start-Service -Name $serviceName
foreach ($i in 1..60) {
    try {
        $health = Invoke-RestMethod -Uri "https://localhost:$Port/health" -TimeoutSec 3
        Write-Host "Openhold Server is running: site $($health.site) ($($health.siteName)), https://localhost:$Port"
        return
    } catch { Start-Sleep 2 }
}
throw "The service started but isn't answering on port $Port. See the log in $DataFolder\logs and the Windows event log."
