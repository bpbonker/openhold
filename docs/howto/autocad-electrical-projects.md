# Work with AutoCAD Electrical projects

This covers keeping an AutoCAD Electrical project (`.wdp`) and its drawings in Openhold as one set, so project-wide
changes such as wire numbering, tag updates and title-block updates are always checked out and in together.

## How Openhold treats a project

- **The project set** is the `.wdp`, the project files beside it with the same name (`.wdt`, `.wdl`,
  `_wdtitle.wdl`, `_wdpinlist.wdl`, `.aepx`, `.wdi`) and every drawing the `.wdp` lists.
- **Check out one drawing and you check out the whole set.** The same goes for check-in and Undo Check Out, from
  Openhold Explorer or AutoCAD Electrical's **Openhold** tab.
- Openhold finds a drawing's project by looking for a `.wdp` that lists it, in the drawing's own folder or the
  folder above. Keep drawings in the project's folder or one folder below it.
- `.wdp` files are in the *Engineering* category of a new vault, so they follow the same lifecycle as the drawings.

## Put a project in the vault

1. Copy the project folder into your workspace, e.g. `C:\Openhold\Workspace\Projects\PRJ01 - Pump skid\Electrical`.
   For many projects, or projects with references, use [OpenholdImport](import-existing-files.md) instead.
2. Open the project in AutoCAD Electrical from that folder.
3. In Openhold Explorer, select the `.wdp` and check it in. Its drawings and project files go in with it, as one
   changeset.

Use the same workspace path on every PC (e.g. `C:\Openhold\Workspace`), so the drawing paths in the `.wdp` work
everywhere.

## Daily work

1. **Get the latest set.** In Openhold Explorer, open the project's folder, select the `.wdp` and its drawings (Ctrl+A
   selects everything in the folder) and click **Get Latest**.
2. **Check out.** Open any drawing of the project in AutoCAD Electrical and click **Openhold tab › Check Out**
   (`OPENHOLDCHECKOUT`). The whole set is checked out, each file at its latest version.
3. **Work as normal**, including project-wide commands.
4. **Check in.** Click **Openhold tab › Check In** (`OPENHOLDCHECKIN`) and say what changed. The set is checked in
   together: every drawing and project file you changed gets a new version, in one changeset. The title block of the drawing you're in is read into its
   properties; the other drawings keep the properties they had.

Nobody else can check out any part of the set while you have it.

## Release a project

1. In Openhold Explorer, select the `.wdp` and all its drawings.
2. Right-click › **Change State…** and choose e.g. *Release*.

**Change State** changes the files you select, so select the whole set. See [Release and revise](release-and-revise.md).

## Rename or move a project

Openhold Explorer won't rename or move a `.wdp`, because the project lists its drawings relative to itself. Rename
or move it from AutoCAD Electrical's Project Manager.

Renaming a single drawing in Openhold Explorer (right-click › **Rename…**) is fine: the `.wdp` is updated to the new
name and checked in with it.

## If something goes wrong

- **Renaming a drawing or editing its properties says a file in the project is checked out to you.** Both involve
  the whole set. Check the set in (or undo the check-out) first.
- **Check-out says another drawing is checked out to someone else.** The set is all-or-nothing: wait until they've
  checked in.
- **A drawing isn't checked out with the rest.** It's probably not listed in the `.wdp`, or it's more than one folder
  below the project. Add it to the project in AutoCAD Electrical, or move it closer.
