# Set up the PCs

Every engineer's PC needs **Openhold Explorer**. PCs with Inventor or AutoCAD also get the matching **add-in**, so
check-out and check-in happen from inside the CAD program.

PCs run Windows 10 or 11 (x64) and need the
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). The import tool and the job worker
also need the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).

## Openhold Explorer

Prebuilt downloads and an installer are on the [roadmap](../../ROADMAP.md). For now, build it once on any PC with
the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and copy the folder to each PC:

```powershell
git clone https://github.com/bpbonker/openhold.git
cd openhold
dotnet publish src\Openhold.Desktop -c Release -o C:\Openhold\Explorer
```

Run `C:\Openhold\Explorer\OpenholdExplorer.exe`. The first time, Settings asks for:

| Setting | What to enter |
|---|---|
| Server | The server's address, e.g. `https://openhold.example.com` or `http://server:8080` |
| Vault | Leave as the server's default unless your administrator says otherwise |
| Workspace | The folder files are put in, e.g. `C:\Openhold\Workspace`. Use the same path on every PC so references between files keep working. |
| Sign-in | **Windows** on a Windows server in your domain; **Openhold account** otherwise |

## Set up many PCs at once

The settings live in `%APPDATA%\Openhold\client.json`. Set one PC up, then copy that file to each user's
`%APPDATA%\Openhold\` folder (for example with Group Policy or Intune):

```json
{
  "ServerUrl": "https://openhold.example.com/",
  "WorkspaceRoot": "C:\\Openhold\\Workspace",
  "Vault": null,
  "SignIn": "Windows"
}
```

## The Inventor and AutoCAD add-ins

The add-ins are built on a PC that has the CAD program installed, because they compile against its programming
interface. Close Inventor or AutoCAD first, then from the `openhold` folder:

```powershell
.\tools\install-addin.ps1 -App Inventor      # Inventor 2025
.\tools\install-addin.ps1 -App AutoCAD       # AutoCAD 2025/2026, AutoCAD Mechanical, AutoCAD Electrical
```

Add `-AllUsers` (from an elevated PowerShell) to install for everyone on the PC. Start the CAD program: an
**Openhold** tab appears on the ribbon. The first time, AutoCAD may ask whether to load the add-in, because it isn't
signed yet; choose **Always load**.

To remove an add-in: `.\tools\install-addin.ps1 -App Inventor -Uninstall`.

## Next

- New to Openhold? The [user guide](../user-guide.md) covers everyday work.
- Administrators: [set up your vault](../howto/set-up-your-vault.md).
