# Release workflows

How categories, lifecycles, revisions, approvals and published PDFs work in Openhold, and how to set them up.

## The pieces

- **Category.** Every file has one, e.g. Engineering or Document. The category decides:
  - the file's **lifecycle**;
  - its **revision scheme**;
  - its **numbering scheme**.

  A new file gets the first category whose rules match it (file extensions and vault folders). Categories
  marked *manual* are only ever chosen by hand: right-click a file and choose **Change Category…**.
  Changing a file's category needs the *Change Category* privilege (administrators have it), because the
  category decides how the file is released.
- **Lifecycle.** A set of states (e.g. Work in Progress → Released → Obsolete) and the transitions allowed
  between them.
  - Each state can be **locked**: nobody can check out a file in a locked state.
  - A state can also be **released** or **obsolete**.

  A transition can:
  - be limited to certain groups;
  - require a comment;
  - require properties to be filled in;
  - require every file it uses to be released;
  - need approval from one or more groups;
  - publish a PDF.
- **Revision scheme.** How revisions count, e.g. letters (A, B, C), numbers (01, 02), or number and letter
  (1A, 1B … 2A). Each part sets its start and the letters it skips.
  - **Revise** suggests the next revision from the scheme; you can type over it. It offers a minor step
    (1A → 1B) or a major one (1A → 2A).
  - **Openhold never revises on its own.** Check-in, check-out and state changes only add versions.
- **Groups.** Lists of people, used for transition permissions and approvals. Administrators, and groups with
  the *Manage Configuration* privilege, can change the configuration.

## What a new vault starts with

| Category | Files | Lifecycle | Revisions | Numbering |
|---|---|---|---|---|
| Engineering | .ipt .iam .idw .ipn .ide .dwg .dxf .wdp | Release | Letters, skipping I O Q S X Z | none |
| Document | everything else | Release | Letters, skipping I O Q S X Z | none |

A new vault has no numbering schemes: administrators build their own in Administration › Numbering and
point categories at them. Add categories of your own (e.g. one for bought-out parts on the *Library*
lifecycle) the same way.

**Lifecycles:**

| Lifecycle | Flow | Notes |
|---|---|---|
| **Release** (self-release) | Work in Progress → **Released** → Obsolete | Releasing needs every used file released and publishes a PDF. *Reopen for a new revision* takes a released file back to Work in Progress. |
| **Reviewed release** | Work in Progress → In Review → **Released** | Release needs approval from one member of *Checkers*, who can't be the person who asked. *Return for changes* (with a comment) sends it back. |
| **Library** (bought-out and catalogue parts) | Work in Progress → **Active** → Obsolete | |

To use reviewed release for engineering files, an administrator points the Engineering category at the
*reviewed-release* lifecycle and adds people to *Checkers*.

## Using it in Openhold Explorer

- **State column.** Each file shows its state with a coloured dot:
  - blue: editable
  - amber: locked (e.g. In Review)
  - green: released
  - grey: obsolete
- **Change State… (right-click).**
  - It offers the moves your lifecycle allows you, and checks them with the server before anything changes.
    For each file it shows what will happen, or why it can't.
  - Tick *Include the files these use* to release an assembly together with its parts.
  - If the move needs approval, the button says **Send for approval**.
- **Approvals (toolbar).** The count shows how many changes are waiting for you.
  - Approve, or Reject with a reason.
  - Withdraw a request you made.
  - When the last approval is in, the files change state together.
  - Each person counts towards one approval rule only: a change needing an Engineering and a Quality approval
    needs two different people.
  - If the change can't be made any more by then (a file was changed or deleted since it was submitted), the
    request is closed as *blocked* with the reason. The approvals are kept on record, and the files can be
    submitted again.
- **Revise…** Released files are reopened first, as part of the same action. The new revision is written
  into the file (iProperty or title block) and checked in.
- **Open Published PDF (right-click).** Opens the stamped PDF made when the file was last released.
- **Copy Design…** can number the copies from a numbering scheme. A drawing gets its model's number.
  Library and bought-out parts start unticked, so they stay shared.

**Rules Openhold enforces:**
- A released file can't be checked out, renamed, moved or edited until it is reopened.
- Deleting it needs the *Delete Released Files* privilege; changing its category needs *Change Category*.
- Restoring an older version never makes a file released again.

## Properties a file can't hold

A DWG can only hold properties that map to a title-block attribute. Edit Properties writes those into the
drawing and keeps the rest (e.g. Vendor) in the vault only, as Autodesk Vault does. Vault-only properties:
- show and search like any other property;
- survive check-ins from AutoCAD;
- go with copies made by Copy Design.

## Published PDFs and the QR code

When a transition that publishes a PDF (e.g. *Release*) is applied to a DWG or PDF, a job is queued. The
release itself doesn't wait for it.

The **job worker** (`OpenholdJobs.exe`) runs on a Windows machine with AutoCAD and the Openhold AutoCAD plugin.
For each job it:
1. downloads the released version and everything it references;
2. plots every layout into one PDF;
3. stamps every page with a QR code and *"Released rev 1B · 28 Oct 2026 · v2 / Scan to check it's current"*;
4. stores the PDF with the released version.

Scanning the QR code opens `…/verify/<file>/<version>`. The page says whether that printout is:

| Result | Meaning |
|---|---|
| **Current** | It's still the current release, even if a new revision is being worked on. |
| **Superseded** | A newer release exists; the page names its revision. Compared by content, so a change released without revising is still caught. |
| **Obsolete** or **Withdrawn** | Don't use the printout. |

The page needs no sign-in, so phones on the shop floor work. It shows only what the printout already shows (the
file name and revision). The link contains the file's and version's ids, which can't be guessed.

### Setting up a job worker

1. Install AutoCAD 2025 or 2026 and the Openhold AutoCAD plugin (`tools\install-addin.ps1 -App AutoCAD`).
2. Set up Openhold on that machine as on any PC (Openhold Explorer's **Settings**: server and vault).
3. Add the Windows account it runs as to the vault's **Job workers** group (or make it an administrator).
4. Install it as a Windows service with `tools\install-job-worker.ps1` (see the
   [administrator guide](administration.md#job-worker)), or run `OpenholdJobs.exe` directly. Options:
   - `--once`: do the waiting jobs, then stop.
   - `--default-paper A3`: the paper for layouts with no printer set up. Without it, such a layout gets the paper
     closest to what's drawn on it.
   - `--corner TopRight`: where the stamp goes (`TopLeft`, `TopRight`, `BottomLeft`, `BottomRight`).
5. On the server, set `Openhold:PublicUrl` to the address phones reach the server at, e.g.
   `https://openhold.example.com`. Without it, QR codes use the address the job worker used.

A job that fails is retried up to three times. After that it stays *failed* with its reason, shown in
Administration › Jobs.

## Not built yet

- PDFs of Inventor drawings (.idw). These need Inventor on the job worker.
- Email or Teams notifications for approvals. The Approvals count in Explorer is the signal.
- Signing approvals by entering a password again.
