# Try the AutoCAD workflow (development build)

This runs everything on one PC against a local development server. Use a **copy** of a drawing, never a
file in a working folder that another PDM (such as Autodesk Vault) manages.

## 1. Start the server

```powershell
docker compose -f deploy/docker-compose.dev.yml up -d
dotnet run --project src/Openhold.Server --launch-profile http     # http://localhost:5080
```

## 2. Put a drawing copy in the workspace

The default workspace is `C:\Openhold\Workspace`. The first time AutoCAD loads the plugin, it creates
`%APPDATA%\Openhold\client.json`, which holds the server address, workspace path and title-block mapping.

```powershell
New-Item -ItemType Directory -Force C:\Openhold\Workspace\Electrical
Copy-Item <path to one of your drawings>\PRJ01-E001.dwg C:\Openhold\Workspace\Electrical\
```

## 3. Install the plugin (once)

```powershell
./tools/install-addin.ps1 -App AutoCAD     # current user, no admin needed
```

Restart AutoCAD. Openhold loads automatically at startup, with no drawing open and no `NETLOAD` needed.
The plugin is not signed, so the first time AutoCAD may warn about it. Choose **Always load**.
`-AllUsers` (from an elevated PowerShell) installs under Program Files instead, a location AutoCAD trusts by
default. `-Uninstall` removes it.

## 4. Commands

| Command | What it does |
|---|---|
| `OPENHOLDCHECKIN` | Asks what changed, saves, reads the title block into vault properties, and checks in. A new drawing is added to the vault. It then reopens read-only. The revision is never changed. |
| `OPENHOLDCHECKOUT` | Checks the drawing out, plus its whole AutoCAD Electrical project if it belongs to one. Gets the latest version first and reopens it ready to edit. |
| `OPENHOLDUNDO` | Releases the drawing without checking in and restores the vault copy. Your changes are kept in a backup. |
| `OPENHOLDGET` | Updates the drawing to the latest version in the vault. |
| `OPENHOLDSTATUS` | Tells you, in one sentence, where the drawing stands. |

Things to try:

1. Open the copied drawing and run `OPENHOLDCHECKIN`. It's added as version 1 and reopens read-only.
2. Try `QSAVE`. Openhold stops it and explains why. Your changes stay open.
3. Run `OPENHOLDCHECKOUT`, edit, then `OPENHOLDCHECKIN`. That makes version 2.
4. Change one sheet's `REVISION_NUMBER` so it disagrees with the others, then check in. Openhold warns you before checking in.

Backups of anything Openhold would otherwise overwrite are kept under `C:\Openhold\Workspace\.openhold\backup`.
