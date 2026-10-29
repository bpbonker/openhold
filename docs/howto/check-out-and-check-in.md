# Check out and check in

This covers everyday work: getting the latest files, changing them, and saving your changes to the vault, from
Openhold Explorer, Inventor or AutoCAD.

Files in your workspace are read-only until you check them out, so nothing changes by accident. Check-out and
check-in only add versions; they never change a file's revision (see [Release and revise](release-and-revise.md)).

## Before you start

- Openhold Explorer is set up with your server, vault and workspace ([Client PCs](../install/clients.md)).
- Your CAD program has the Openhold add-in, shown as an **Openhold** tab on the ribbon.
- Open files from your workspace (e.g. `C:\Openhold\Workspace`). Openhold ignores files outside it.

## Get the latest files

1. In Openhold Explorer, select files or a folder.
2. Click **Get Latest** (Ctrl+L). **Actions › Get Latest With Everything It Uses** also brings an assembly's parts
   and a drawing's xrefs. Right-click a folder › **Get Latest for This Folder** gets every file in that folder.

Files checked out to you are left as they are. If you changed a file without checking it out, your copy is kept in
`.openhold\backup` in your workspace before the vault's version replaces it.

## Change a file in Openhold Explorer

1. Select the file and click **Check Out** (Ctrl+E). For an assembly, **Actions › Check Out With Everything It Uses**
   checks out its parts too. Check-out always gets the latest version first.
2. Open it (double-click or **Open**), change it in its program and save.
3. Back in Openhold Explorer, select it and click **Check In** (Ctrl+I).
4. Type **What changed?**, and click **Check In**. Tick **Keep checked out so I can carry on editing** to keep working
   on it.

To add new files, save them in your workspace (or drag them onto the file list), select them and click **Check In**.

## Change a file in Inventor

Use the **Openhold** tab:

| Button | Does |
|---|---|
| **Check Out** | Gets the latest version and makes the document editable |
| **Check In** | Checks in the active document with every file it uses that's checked out to you or new, e.g. parts added to an assembly, with iProperties and references |
| **Undo Check Out** | Releases the document and restores the vault's copy |
| **Get Latest** | Updates the document and everything it uses (save your changes first) |
| **Status** | Says where the document and its parts stand |
| **Save to Openhold** | Saves a new document into a vault folder, named by the vault's rules ([numbering](numbering-scheme.md#get-a-number)) |

If you start editing a document that isn't checked out, Inventor asks whether to check it out. Say yes, then repeat
your last change.

## Change a drawing in AutoCAD

Use the **Openhold** tab, or type the commands:

| Button | Command | Does |
|---|---|---|
| **Check Out** | `OPENHOLDCHECKOUT` | Gets the latest version and reopens the drawing ready to edit |
| **Check In** | `OPENHOLDCHECKIN` | Asks what changed, saves, reads the title block into properties and checks in; the drawing reopens read-only |
| **Undo Check Out** | `OPENHOLDUNDO` | Releases the drawing and restores the vault's copy |
| **Get Latest** | `OPENHOLDGET` | Updates the drawing to the newest version |
| **Status** | `OPENHOLDSTATUS` | Says in one sentence where the drawing stands |

If you edit a drawing that isn't checked out, Openhold tells you at once and stops the save, but your changes stay
open: run **Check Out** and they're carried over. A new drawing saved in the workspace goes into the vault with
**Check In**. If title blocks on different sheets disagree (e.g. revision), check-in warns you first.

For AutoCAD Electrical projects, see [AutoCAD Electrical projects](autocad-electrical-projects.md).

## Change your mind

Select the file and click **Undo Check Out** (in Explorer or on the CAD ribbon), then confirm. The vault's copy is
restored and your changed copy is kept in `.openhold\backup` in your workspace.

## What the marks mean

The bubble beside each file is the state of the copy on your PC (green: latest; red ↓: older; amber !: changed
without check-out; hollow: not on your PC; dashed: not in the vault yet). A blue ✕ means checked out to you, a red ✕
to someone else (hover to see who). The full list is in the
[user guide](../user-guide.md#reading-the-file-list), and in Openhold under
**Help › What the Status Marks Mean**.

## If something goes wrong

- **"Checked out to someone else."** Hover over the red ✕ to see who. Only one person can change a file at a time.
- **A file can't be checked out because it's released.** Move it back to an editable state first: see
  [Release and revise](release-and-revise.md).
- **"Couldn't reach the Openhold server."** Nothing was changed. At an office with its own server you can still
  browse and get files; check-out and check-in work again once the link to the main server is back.
- **Your changes seem to be gone after Undo Check Out or Get Latest.** Look in `.openhold\backup` in your workspace.
- **Check-in refuses a new file's name.** The folder's naming rule is required; see
  [Build a numbering scheme](numbering-scheme.md#if-something-goes-wrong). CAD file names (e.g. `.ipt`, `.iam`,
  `.dwg`) must also be unique across the whole vault.
