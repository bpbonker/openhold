<#
.SYNOPSIS
    Schedules a nightly encrypted backup of the Openhold server with openhold-admin.

.DESCRIPTION
    Run as an administrator on the server. It publishes openhold-admin next to the server, stores the backup
    passphrase in a file only the backup account can read, and registers a scheduled task that runs every night:
        openhold-admin backup --server <server folder> --to <destination>

    The destination should be off the server, e.g. a NAS share (\\nas\openhold-backups) or a separate disk that is
    itself copied off site. Keep a copy of the passphrase in the company's password manager: without it the
    backups can't be restored.

    Check backups regularly with:  openhold-admin verify --from <destination>
    and rehearse a restore at least twice a year (see docs\administration.md).

.EXAMPLE
    .\schedule-backup.ps1 -Destination \\nas\openhold-backups -Account 'EXAMPLE\openhold$' -Passphrase (Read-Host -AsSecureString)
#>
param(
    [string] $ServerFolder = (Join-Path $env:ProgramFiles "Openhold\Server"),
    [Parameter(Mandatory)] [string] $Destination,
    [Parameter(Mandatory)] [string] $Account,
    [Parameter(Mandatory)] [System.Security.SecureString] $Passphrase,
    [string] $At = "01:00",
    [int] $KeepDays = 35,
    [string] $PgBin
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this from an elevated PowerShell (Run as administrator)."
}

$adminFolder = Join-Path (Split-Path $ServerFolder) "Admin"
dotnet publish (Join-Path $repo "src\Openhold.Admin\Openhold.Admin.csproj") -c Release -o $adminFolder -v quiet | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Publishing openhold-admin failed." }

# Secret files: readable only by the backup account and administrators.
function Write-Secret([string] $Path, [string] $Value) {
    Set-Content -Path $Path -Value $Value -Encoding UTF8 -NoNewline
    $acl = New-Object Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($who in "BUILTIN\Administrators", "NT AUTHORITY\SYSTEM", $Account) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($who, "Read", "Allow")))
    }
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule("BUILTIN\Administrators", "FullControl", "Allow")))
    Set-Acl -Path $Path -AclObject $acl
}

$passphraseFile = Join-Path $adminFolder "backup-passphrase.txt"
$plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($Passphrase))
if ($plain.Length -lt 16) { throw "Use a passphrase of at least 16 characters." }
Write-Secret $passphraseFile $plain

$arguments = "backup --server `"$ServerFolder`" --to `"$Destination`" --keep-days $KeepDays --passphrase-file `"$passphraseFile`""

# The database password: the task doesn't run in the service's environment, so it gets the same password the
# installer gave the service (install-server.ps1), in a file only the backup account can read.
$serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\Openhold Server"
$serviceEnvironment = (Get-ItemProperty -Path $serviceKey -Name Environment -ErrorAction SilentlyContinue).Environment
$dbPassword = $serviceEnvironment | Where-Object { $_ -like "PGPASSWORD=*" } | Select-Object -First 1
if ($dbPassword) {
    $dbPasswordFile = Join-Path $adminFolder "db-password.txt"
    Write-Secret $dbPasswordFile $dbPassword.Substring("PGPASSWORD=".Length)
    $arguments += " --db-password-file `"$dbPasswordFile`""
} else {
    Write-Warning "The Openhold Server service has no database password set; the backup will rely on pgpass.conf or Windows sign-in for $Account."
}
if ($PgBin) { $arguments += " --pg-bin `"$PgBin`"" }
$action = New-ScheduledTaskAction -Execute (Join-Path $adminFolder "openhold-admin.exe") -Argument $arguments -WorkingDirectory $adminFolder
$trigger = New-ScheduledTaskTrigger -Daily -At $At
$principal = if ($Account.EndsWith('$')) {
    New-ScheduledTaskPrincipal -UserId $Account -LogonType Password -RunLevel Highest   # group managed service account
} else {
    New-ScheduledTaskPrincipal -UserId $Account -LogonType ServiceAccount -RunLevel Highest
}
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 6) -RestartCount 2 -RestartInterval (New-TimeSpan -Minutes 30)
Register-ScheduledTask -TaskName "Openhold nightly backup" -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Write-Host "Scheduled 'Openhold nightly backup' at $At to $Destination. Run it now with: Start-ScheduledTask 'Openhold nightly backup'"
Write-Host "Use the server's service account for -Account: it can read the keys and the file store. Each run logs to $Destination\logs and records its outcome in $Destination\last-backup.json."
