# Import existing files

This brings existing project folders, for example the working folder of another PDM such as Autodesk Vault, into
Openhold with their references, properties and previews, using `OpenholdImport`. Always do a dry run first.

`OpenholdImport` brings each file's references (Inventor assemblies and drawings, AutoCAD xrefs), iProperties,
title-block values and preview. **The source folder is only read**: files are copied into your workspace first, and
Inventor and AutoCAD only ever open the copies.

## Before you start

- **A Windows PC with Inventor and AutoCAD**, with the [Openhold AutoCAD add-in](../install/clients.md#the-inventor-and-autocad-add-ins)
  installed. Without Inventor, Inventor references and iProperties aren't read; without AutoCAD and the add-in, xrefs
  and title blocks aren't read. The import says so when it starts.
- **Openhold Explorer set up and signed in on that PC.** The import uses its server, vault and workspace, and runs as
  that person, who needs *Change* access to the destination folder.
- **The source folder outside your Openhold workspace.**
- **An up-to-date source.** If it's another PDM's working folder, get the latest versions there first.
- **Naming rules not required for the destination.** A category whose naming rule is **Required** refuses new files
  whose names don't follow its scheme. Untick **Required** on **Administration › Naming Rules** for the import, or
  import into a folder whose category doesn't require it.

## 1. Build OpenholdImport

On a PC with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), from the `openhold` folder:

```powershell
dotnet publish src\Openhold.Import -c Release -o C:\Openhold\Import
```

`OpenholdImport` runs on .NET 8. If Windows says .NET 8 is missing, install the .NET 8 Desktop Runtime from
[dot.net](https://dotnet.microsoft.com/download/dotnet/8.0).

## 2. Dry run

```powershell
cd C:\Openhold\Import
.\OpenholdImport.exe "D:\Projects\PRJ01" '$/Legacy/PRJ01' --dry-run --report prj01.csv
```

Put the vault folder in single quotes, so PowerShell doesn't treat `$` as a variable. The dry run reads everything,
writes the report and changes nothing in Openhold.

## 3. Read the report

Open `prj01.csv` in Excel. One row per file:

| Column | Tells you |
|---|---|
| **Vault path** | Where the file will go |
| **Action** | *Import*, *Skipped: already in Openhold*, or *Skipped: a different file is already in the workspace there* |
| **Part Number**, **Description**, **Revision** | What was read from the file |
| **Uses** | How many files it references |
| **Not found** | References to files that aren't being imported and aren't in Openhold |
| **Notes** | Anything else worth checking |

Deal with **Not found** before importing. Usually the missing files are in another folder (a library, or another
project): import that folder first, or choose a source folder that includes it.

## 4. Import

```powershell
.\OpenholdImport.exe "D:\Projects\PRJ01" '$/Legacy/PRJ01' --read-only
```

Options:

| Option | Does |
|---|---|
| `--read-only` | Makes the vault folder read-only for everyone except administrators, e.g. for an archive |
| `--state Released` | Moves the imported files to that lifecycle state afterwards |
| `--comment "text"` | The check-in comment (default: *Imported from* the source folder) |
| `--batch 100` | How many files go in each check-in |
| `--report file.csv` | Where to write the report |

Files are checked in in dependency order (parts before the assemblies that use them), and each records where it came
from in the *Imported From* property.

## 5. Check the result

1. In Openhold Explorer, open the vault folder and spot-check a few assemblies: **Uses** and **Where Used** should
   list their parts and drawings.
2. Open an assembly and a drawing from the workspace to confirm references resolve.
3. Tell people to work from Openhold from now on, and stop changing the old copy.

## If something goes wrong

- **The import stops part-way.** It says why and how many files went in. Fix the problem and run the same command
  again: files already imported are skipped.
- **"The source folder and the destination in the workspace overlap."** Choose a source folder outside the
  workspace.
- **A check-in is refused because a name is taken.** CAD file names (`.ipt`, `.iam`, `.idw`, `.dwg` and so on) must be
  unique across the vault. Find both copies in the report and decide which one to keep; then import from a copy of
  the source folder without the other.
- **A check-in is refused because of a naming rule.** See *Naming rules* under [Before you start](#before-you-start).
- **References come out missing.** Check the **Not found** column of the report, import the missing folder first, then
  run the import again.

More detail: [administrator guide](../administration.md#bringing-existing-files-in).
