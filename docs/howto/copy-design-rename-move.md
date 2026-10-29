# Copy a design, rename and move

This copies an assembly or drawing set under new names, and renames or moves files, with the references in
assemblies, drawings and AutoCAD Electrical projects kept pointing at the right files.

All three work from Openhold Explorer and are all-or-nothing: if anything can't be done, nothing is changed.

## Copy a design

1. Select the top assembly (or drawing) and choose right-click › **Copy Design…**. Openhold lists it with everything
   it uses.
2. Tick the files to copy. Library and bought-out parts start unticked, so the copies keep using the same ones.
3. Give the copies new names:
   - type in the **New Name** column; or
   - use **Replace** … **with** (e.g. `PRJ01` with `PRJ02`), **Prefix** or **Suffix**; or
   - choose **Number the copies from** to take the next numbers from a numbering scheme. A drawing gets its model's
     number.
4. **Put the copies in** shows where they go: *the same folder as each original*, or click **Change…** to pick a
   folder.
5. Click **Copy**.

The copies are new files in the vault, checked in as version 1, with their properties, vault-only properties and
previews. Each copied assembly and drawing points at the copied parts; parts you didn't tick stay shared. The
originals aren't changed.

## Rename a file

1. Select the file and choose right-click › **Rename…** (F2).
2. Type the new name and click **OK**.

Every file that uses it, such as Inventor assemblies and drawings, AutoCAD drawings with xrefs to it and AutoCAD
Electrical projects, is checked out, repaired to use the new name, and checked in with it as one change.

## Move files

1. Select the files and choose right-click › **Move to Folder…**.
2. Pick the vault folder and click **Choose**.

References are repaired in the same way as for a rename.

## What can't be renamed or moved (yet)

| Not possible | Instead |
|---|---|
| Released files | Reopen them first ([Release and revise](release-and-revise.md)) |
| A file that is checked out to you, or is used by one that is | Check it in (or undo the check-out) first, because it would be updated too |
| An AutoCAD Electrical project (`.wdp`) | Rename or move it from AutoCAD Electrical's Project Manager |
| A drawing with its own xrefs or images, to another folder | Move it together with them in AutoCAD; renaming it in place is fine |
| A folder | Folder rename isn't built yet. Make the new folder and move the files into it. |
| A file used by a type Openhold can't repair | Rename it from within the CAD program that uses it |

CAD file names (`.ipt`, `.iam`, `.idw`, `.dwg`, `.ipn`, `.ide`, `.wdp`, `.dwt`) must be unique across the whole
vault, so Inventor and AutoCAD never open the wrong file. A rename or copy to a name that's already used is refused.

## Delete and restore

Right-click › **Delete** (Del) sends files to the **Trash** (**View › Trash…**), where they keep their full history.
Select them there and click **Restore** to bring them back to the same folder. Deleting a released file needs the
*Delete Released Files* privilege.

## If something goes wrong

- **"Nothing was changed: … already exists."** Choose another name, or move the other file first.
- **"Nothing was changed: check these files in (or undo their check-out) first."** The listed files would be updated
  by the rename, and you have them checked out.
- **"Couldn't reach the Openhold server."** Everything was put back as it was. If the file later shows the new name
  anyway, use **Get Latest** on its folder.
- **Someone else has a file that uses it checked out.** The rename needs to check that file out too. Wait until
  they've checked in.
