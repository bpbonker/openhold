# Release and revise

This takes files through their lifecycle (e.g. Work in Progress to Released), through approvals where your
administrator has set them, and on to a new revision.

**Revisions never change on check-in or check-out.** Those only add versions (v1, v2, v3…). A file's revision
(A, B, C…) changes only when someone uses **Revise**.

## Release files

1. In Openhold Explorer, select the files. For an assembly, select the assembly.
2. Right-click › **Change State…**.
3. Under **Change to**, choose the change, e.g. *Release*. The list shows only the changes your lifecycle allows you.
4. Tick **Include the files these use, where they can make the same change** to release an assembly together with
   its parts.
5. Read the **After** column. For each file it says what will happen, or why it can't. Files change together or not
   at all.
6. Add a **Comment** if the change needs one, then click the button: its name is the change (e.g. **Release**), or
   **Send for approval** if the change needs approval.

The **State** column then shows the new state. Released files are locked: they can't be checked out, renamed, moved
or edited until they're reopened.

What your administrator may have set for a change:

- **Everything the file uses must be released first.** The **After** column lists anything that isn't.
- **A comment is required.**
- **Properties must be filled in**, e.g. Description.
- **Approval** from one or more groups.
- **A stamped PDF is published** for drawings (see below).

The built-in lifecycles are described in [Release workflows](../release-workflows.md#what-a-new-vault-starts-with).

## Approve a change

When a change needs approval, the files stay as they are until it's approved.

1. The **Approvals** button on the toolbar shows how many changes are waiting for you. Click it (or
   **Actions › Approvals…**).
2. Select a change to see its files and approvals.
3. Type **Your comment** if you want to, and click **Approve**, or **Reject** with a reason.
4. When the last approval is in, the files change state together.

Rules:

- Nobody can approve their own request, unless the administrator ticked *The person who asked may approve it*.
- Each person counts towards one approval only: a change needing an Engineering and a Quality approval needs two
  different people.
- The person who asked can **Withdraw Request**.
- If a file was changed or deleted after the request was sent, the request closes as *blocked*, with the reason.
  Send it again.

There are no email or Teams notifications for approvals yet; the **Approvals** count is the signal.

## Revise a released file

1. Select the released files and choose right-click › **Revise…**.
2. Openhold suggests each file's next revision from its revision scheme (A → B). Type over it if you need to. Tick
   **Major revision** for a major step (e.g. 1C → 2A), if your scheme has one.
3. Type the **Reason for the revision** and click **Revise**.

Openhold reopens the files (e.g. *Reopen for a new revision*, back to *Work in Progress*), writes the new revision into
each file (its Revision iProperty or title block) and checks it in as a new version. Then check out, make the change,
check in, and release again as above.

Revise needs the CAD program that writes the property on your PC: Inventor for Inventor files, AutoCAD for drawings.

## Released PDFs and the QR code

If the change publishes a PDF (the built-in *Release* does), the job worker makes a PDF of each released drawing
shortly after the release:

- every layout in one PDF;
- each page stamped with a QR code and *Released rev B · 28 Oct 2026 · v2 / Scan to check it's current*.

Right-click a released drawing › **Open Published PDF** to open it.

Scanning the QR code on a printout opens a page, with no sign-in, that says:

| Result | Meaning |
|---|---|
| **Current** | Still the current release |
| **Superseded** | A newer release exists; the page names its revision |
| **Obsolete** or **Withdrawn** | Don't use the printout |

PDFs are made for DWG and PDF files. Inventor drawings (`.idw`) aren't published yet. Setting up the job worker:
[Publish PDFs on release](publish-pdfs.md).

## Make a file obsolete

Select it › **Change State…** › *Make obsolete*, with a comment. *Reinstate* (administrators only) brings it back.

## If something goes wrong

- **The change you want isn't offered.** Your lifecycle doesn't allow it from the file's current state, or you're
  not in a group allowed to make it. Ask your administrator.
- **"Everything the file uses must be released."** Release the parts first, or tick **Include the files these use**.
- **Revise says the file is checked out to you.** Check it in (or undo the check-out) first.
- **Revise says reopening needs approval.** The way back to an editable state needs approval. Revise once it's
  approved.
- **No published PDF.** Openhold says *No PDF has been published … yet*. The job worker may not be running: ask your
  administrator to check **Administration › Jobs**.
- **Restoring an older version** (History tab › right-click › **Restore This Version…**) never makes a file released
  again.
