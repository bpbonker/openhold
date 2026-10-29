# Try the Inventor workflow (development build)

Uses the same local development server as [Try the AutoCAD workflow](try-autocad.md). Use **copies** of your
files.

## 1. Use a separate Inventor project

Inventor finds an assembly's parts through the active project's folders. If an Openhold copy is opened
under another project, such as an Autodesk Vault project, Inventor may load the **originals** from that
project's folders. So create a separate project first:

1. In Inventor, close all documents and open **Projects** (Get Started tab).
2. Click **New** → **New Single User Project** → Next.
3. Name it `Openhold` and set the project folder to `C:\Openhold\Workspace`, then click **Finish**.
4. Make sure **Openhold** is the active project (it shows a tick), then click **Done**.

Switch back to your usual project at any time the same way.

## 2. Install the add-in (once)

```powershell
./tools/install-addin.ps1 -App Inventor        # current user, no admin needed
```

Restart Inventor (2025 or later). An **Openhold** tab appears on the ribbon in Part, Assembly and Drawing.

## 3. Try it

1. Copy one of your own assemblies with its parts into the workspace, e.g.
   `C:\Openhold\Workspace\Mechanical\PRJ01 Frame Test\PRJ01-01-103Z-00.iam`, and open it.
2. **Check In.** The assembly and the files it uses are new, so they're all added in one go.
   Part Number, Description, Revision, Material and so on are recorded from the iProperties,
   and the references between files are recorded so Uses / Where Used work.
3. Try to change something. Openhold stops the edit and offers to check the file out.
   Accept, then repeat the change.
4. **Check In** again to make version 2. **Status** shows where the assembly and its parts stand.
5. In **Openhold Explorer**, open `Mechanical › PRJ01 Frame Test`, select the assembly and look at
   **Uses**, then select a part and look at **Where Used**.

| Button | What it does |
|---|---|
| Save to Openhold | Saves a new document into the vault folder you pick, named by the vault's naming rules |
| Check Out | Checks out the active document, getting the latest version first |
| Check In | Checks in the active document and every file it uses that is checked out to you or new, with iProperties and references. Never changes the revision |
| Undo Check Out | Releases without checking in and restores the vault copy. Saved changes are kept in a backup |
| Get Latest | Updates the document and the files it uses to the newest versions, then reopens it |
| Status | Where the document and the files it uses stand |
