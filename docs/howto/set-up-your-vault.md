# Set up your vault

This takes a new, empty vault to one your team can start working in: folders, categories, lifecycles, revisions
and who may release.

You need the server installed ([install guides](../install/README.md)) and Openhold Explorer signed in as the first
administrator. Allow about an hour.

## What a new vault already has

| | Out of the box |
|---|---|
| **Categories** | *Engineering* (.ipt .iam .idw .ipn .ide .dwg .dxf .wdp) and *Document* (everything else) |
| **Lifecycles** | *Release* (self-release), *Reviewed release* (needs a checker's approval) and *Library* |
| **Revision schemes** | Letters (A, B, C, skipping I O Q S X Z), number and letter (1A), numbers (01), letter and number (A.01) |
| **Groups** | Administrators, Checkers, Job workers, Part librarians, Site servers |
| **Numbering** | None. You build your own: see [Build a numbering scheme](numbering-scheme.md). |

Both categories use the *Release* lifecycle and letter revisions. The full flows are in
[Release workflows](../release-workflows.md#what-a-new-vault-starts-with).

## A sensible starting set-up

For most small and mid-sized firms this is enough for the first months:

| | Recommendation |
|---|---|
| **Folders** | `$/Projects/PRJ01 - Pump skid/02 - Structure` for project work, `$/Library` for bought-out and standard parts, `$/Documents` for procedures and reports. Name folders `CODE - Name` so numbering schemes can read the code. |
| **Categories** | Keep *Engineering* and *Document*. Add *Library parts* for `$/Library/*` on the *Library* lifecycle. |
| **Lifecycle** | Start with *Release* (self-release). Switch *Engineering* to *Reviewed release* once you have named checkers. |
| **Revisions** | Letters. |
| **Numbering** | Build one scheme for project models and one for catalogue part numbers. |

Keep it small at first. Every configuration save is versioned, so you can change it later or go back.

## Steps

1. **Create the top folders.** In Openhold Explorer, select the vault's top folder and choose
   **File › New Folder…** (Ctrl+Shift+N). Make `Projects`, `Library` and `Documents`, then a first project folder
   inside `Projects`, e.g. `PRJ01 - Pump skid`.
2. **Open Administration.** **Tools › Administration…**. Everything on the tabs below is saved together at the end.
3. **Check the revision scheme.** On the **Revisions** tab, select *Letters (A, B, C…)*. **How it counts** shows what
   **Revise** will suggest. To build your own, click **Add**, then **Add Part** for each part and set its **Kind**
   (Letters or Number), **Start**, **Separator before** and **Skip letters**.
4. **Check the lifecycle.** On the **Lifecycles** tab, select *Release*. Select a change under
   **Changes allowed between states** to see its rules:
   - **Who may do it**: groups, comma separated; empty means anyone.
   - **Needs approval from**: e.g. `Checkers`, or `Checkers, Quality x2`.
   - **Before it can happen**: *Everything the file uses must be released*, *A comment is required*, required
     properties.
   - **Then**: *Publish a stamped PDF (drawings and PDFs)*. This needs a job worker: see
     [Publish PDFs on release](publish-pdfs.md).

   To change a built-in lifecycle without losing the original, select it and click **Copy**.
5. **Point each category at its lifecycle and revision scheme.** On the **Categories** tab, select *Engineering* and
   check **Lifecycle**, **Revision scheme** and, once you have built one, **Numbering scheme**.
6. **Add a category for library parts.** Still on **Categories**:
   1. Click **Add** and name it `Library parts`.
   2. **Lifecycle**: *Library*. **Revision scheme**: *Letters (A, B, C…)*.
   3. **File types**: `.ipt, .iam`. **Folders**: `$/Library/*`.
   4. Tick **Library parts: shared rather than copied by Copy Design**.
   5. Click the up arrow until it sits above *Engineering*. New files get the first category, top to bottom, whose
      file types and folders match.
7. **Put checkers in their group** (only if you use *Reviewed release*). On the **Groups** tab, select *Checkers* and
   add people under **Members**. See [Add people](add-people.md).
8. **Save.** Type a note in **What changed (kept with this version)**, e.g. `Initial set-up`, and click
   **Save Changes**. Openhold says *Saved as version N*. Everyone gets the new configuration the next time Openhold
   refreshes.
9. **Try it.** Drag a test drawing into `$/Projects/PRJ01 - Pump skid`, check it in, and look at its **Category** and
   **State** columns. Then delete it (it goes to the Trash).

Next: [Add people](add-people.md), then [Build a numbering scheme](numbering-scheme.md).

## Want a place to practise?

A server can host more than one vault, e.g. *Production* and a *Sandbox* for training, each with its own database,
files and configuration. On a Windows install, add one under `Openhold:Vaults` in the server settings
([administrator guide](../administration.md#more-than-one-vault)). People choose the vault in Openhold Explorer's
**Settings**.

## If something goes wrong

- **Administration is missing or greyed out.** Your account isn't an administrator and has no *Manage
  Configuration* privilege. The administrator named when the server was installed can give it to you.
- **"Not saved yet: …"** Something on one of the tabs isn't filled in correctly; the message says what. Nothing was
  saved.
- **Files already in the vault have the wrong category.** Category rules only decide the category of new files.
  Right-click the files › **Change Category…** (needs the *Change Category* privilege).
- **You want the previous configuration back.** On the **History** tab, select the version, click
  **Load This Version**, check it, then **Save Changes**. It becomes the newest version; nothing is deleted.
