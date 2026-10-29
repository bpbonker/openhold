# Openhold user guide

Openhold keeps the company's drawings, models and documents: every version, who changed what, and which version is released. Where a company has several offices, each office has its own Openhold server, so files open at local network speed.

## Getting started

1. **Open Openhold Explorer.** The first time, Settings asks for:
   - the server (your administrator gives you the address);
   - the vault;
   - your **workspace**, the folder on your PC where files are put (e.g. `C:\Openhold\Workspace`).
2. **Sign in.** On the company network you're signed in with Windows automatically. With an Openhold account, type your username and password (and the six-digit code from your authenticator app if two-factor sign-in is on).
3. **Browse.** The vault's folders are on the left, the files in the selected folder in the middle, and details on the right.

Files in your workspace are **read-only until you check them out**, so nothing gets changed by accident.

## Reading the file list

The bubble beside each file tells you about the copy on **your PC**:

| Mark | Means |
|---|---|
| ● green | You have the latest version |
| ● red with ↓ | You have an older version; Get Latest |
| ● amber with ! | Changed on your PC without being checked out |
| ○ hollow | Not on your PC |
| ◌ dashed | Only on your PC, not in the vault yet |
| ↻ in the bubble | Someone at another office checked in a newer version that's still arriving at your office. Get Latest still works; it fetches it from their office. |

The **✕** shows a check-out:
- **blue** means it's checked out to you;
- **red** means it's checked out to someone else (hover to see who).

The **State** column shows where the file is in its lifecycle, e.g. *Work in Progress*, *In Review* or *Released*.

Help › *What the Status Marks Mean* shows this list in Openhold.

## Daily work

| To | Do |
|---|---|
| **Get files** | Select files or a folder and click **Get Latest**. *Get Latest With Everything It Uses* also brings an assembly's parts and a drawing's xrefs. |
| **Change a file** | **Check Out**, change it in AutoCAD or Inventor and save, then **Check In** with a comment. In AutoCAD and Inventor, the Openhold ribbon does the same, and Inventor offers to check a file out as soon as you start editing. |
| **Change your mind** | **Undo Check Out** puts the file back as it was (your changed copy is kept in `.openhold\backup`). |
| **Add new files** | Save them in your workspace (or drag them onto Explorer), then **Check In**. In Inventor, **Save to Openhold** names a new file by the vault's rules for the folder you pick (and the assembly it's made in). |
| **See history** | The **History** tab lists every version with who, when and the comment. Right-click a version to open or restore it. |
| **See what uses what** | **Uses** lists what a file uses (parts, xrefs); **Where Used** lists what uses it. |
| **Find files** | Type in the search box (Ctrl+F). Search covers names, properties and comments. `Part Number:PRJ01*` searches one property. Untick *This folder only* to search the whole vault. |
| **Rename, move, delete** | Right-click. References in assemblies and drawings are updated. Deleted files go to the **Trash** and can be restored with their history. |
| **Copy a design** | Right-click › **Copy Design** copies an assembly with its parts and drawings under new names, with references repointed. |
| **Edit iProperties / title blocks** | Right-click › **Edit iProperties** (Inventor files), **Edit Title Block Properties** (drawings) or **Edit Properties** (other files). Every standard field is listed, filled in or not. Openhold checks the file out, writes the values into it and checks it in. |
| **Pack and Go** | Right-click › **Pack and Go** zips a file with everything it uses, e.g. to send to a supplier. |

Check-in and check-out never change a file's **revision**. To start a new revision, use **Revise**.

## Releasing

Right-click › **Change State** moves files along their lifecycle, e.g. *Release*. Depending on how your administrator set it up:
- **Everything a file uses may have to be released first.** Openhold lists anything that isn't.
- **Some changes need approval.** The change waits in **Approvals** until enough of the named group have approved it. Nobody can approve their own request.
- **Released drawings get a stamped PDF** with a QR code. Scanning the QR code on a printout shows whether it is still the current release. Right-click › **Open Published PDF** opens it.

Released files can't be checked out. To change one, move it back to an editable state (e.g. *Reopen for a new revision*), then **Revise**.

## The catalogue (part numbers)

Every catalogue part has one part number, e.g. `P-104527-3`, issued from the numbering scheme your administrator chose for the catalogue. Every site and every connected ERP uses that same number.

- **Tools › Catalogue** finds parts by number, description, manufacturer part number, attribute (e.g. *16 A*) or an old number.
- **Before adding a part, search for it.** A part that's already there keeps its number.
- **New Item** gives a part its number straight away. It starts as *Draft*; a part librarian checks and releases it, and only then is it sent to the ERP.
- **For a bought part,** fill in the manufacturer and their part number. Openhold won't give the same manufacturer part two numbers.
- **Right-click a file › Add to Catalogue** creates the item from the file:
  - the description is taken from the file;
  - its current part number is kept as an "other number";
  - the file is linked to the item;
  - Openhold then offers to write the new number into the file's Part Number.

## Working across offices

- **Opening files.** Files are opened from your own office's server, so they open at office speed.
- **A ↻ on a file** means someone at another office checked in a newer version that's still arriving. You can still Get it.
- **Check-out always checks with the lock server,** so two people can never change the same file and you never edit an old version.
- **If the link between offices is down,** you can still browse and get files. Check-out and check-in say the lock server can't be reached; try again later.

## Your account

**Tools › My Account**:
- change your password (Openhold accounts);
- set up two-factor sign-in with an authenticator app.

**File › Sign Out** signs you out on this PC.

## Getting help

- **Problems:** messages in Openhold say what went wrong and what to do. If you're stuck, tell your Openhold administrator what the message said.
- **Locked out or no access:** your administrator can unlock your account, reset your password or two-factor sign-in, and explain who can do what in a folder.
