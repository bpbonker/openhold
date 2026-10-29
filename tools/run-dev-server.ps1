<#
.SYNOPSIS
    Starts (or restarts) the local development server at http://localhost:5080.

.DESCRIPTION
    Starts the dev database (Docker), publishes the server to data\server, and runs it hidden in the
    background. Running from a published copy means rebuilding the source never collides with the
    running server. Logs: data\server.log and data\server-errors.log. Stop with -Stop.
#>
param([switch] $Stop)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$data = Join-Path $repo "data"

Get-Process -Name Openhold.Server -ErrorAction SilentlyContinue | Stop-Process -Force
if ($Stop) {
    Write-Host "Openhold dev server stopped."
    return
}

docker compose -f (Join-Path $repo "deploy\docker-compose.dev.yml") up -d | Out-Null

$publish = Join-Path $data "server"
dotnet publish (Join-Path $repo "src\Openhold.Server\Openhold.Server.csproj") -c Release -o $publish -v quiet | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

# The published copy runs from data\server, so the dev settings' relative chunk paths are replaced with absolute ones.
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://localhost:5080"
$env:Openhold__Vaults__0__ChunkStorePath = Join-Path $data "dev-chunks"
$env:Openhold__Vaults__1__ChunkStorePath = Join-Path $data "dev-chunks-sandbox"
# Whoever runs the dev server administers its vaults (categories, lifecycles, groups).
$env:Openhold__Administrators__0 = $env:USERNAME
Copy-Item (Join-Path $repo "src\Openhold.Server\appsettings.Development.json") $publish -Force

# Redirect through cmd rather than Start-Process -Redirect*: those make the server inherit this script's
# output pipe, so anything piping this script's output would wait for as long as the server runs.
$log = Join-Path $data "server.log"
$errors = Join-Path $data "server-errors.log"
$exe = Join-Path $publish "Openhold.Server.exe"
Start-Process -FilePath "cmd.exe" -WorkingDirectory $publish -WindowStyle Hidden `
    -ArgumentList "/c `"`"$exe`" > `"$log`" 2> `"$errors`"`""

foreach ($i in 1..30) {
    try {
        $health = Invoke-RestMethod http://localhost:5080/health -TimeoutSec 2
        Write-Host "Openhold dev server running at http://localhost:5080 (site $($health.site))."
        return
    }
    catch { Start-Sleep 1 }
}
throw "Server did not start; see $data\server-errors.log"
