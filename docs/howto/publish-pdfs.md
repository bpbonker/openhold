# Publish PDFs on release

This sets up the job worker, which makes a stamped PDF of every drawing when it's released, with a QR code that
shows whether a printout is still current.

## How it works

When a lifecycle change that publishes a PDF (the built-in *Release* does) is applied to a DWG or PDF, the server
queues a job. The release itself doesn't wait. The job worker (`OpenholdJobs.exe`), on a Windows machine with
AutoCAD:

1. downloads the released version and everything it references;
2. plots every layout into one PDF;
3. stamps every page with a QR code and *Released rev B · 28 Oct 2026 · v2 / Scan to check it's current*;
4. stores the PDF with the released version.

People open it with right-click › **Open Published PDF**. What the QR code shows is described in
[Release and revise](release-and-revise.md#released-pdfs-and-the-qr-code).

Not built yet: PDFs of Inventor drawings (`.idw`).

## What you need

- A Windows PC or VM that stays on, with **AutoCAD 2025 or 2026**, licensed for the account the worker runs as.
- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and Git, to build it from the Openhold source.
- An account for the worker in the vault's **Job workers** group.

## 1. Prepare the machine

1. Get the Openhold source (`git clone https://github.com/bpbonker/openhold.git C:\Openhold\Source`).
2. Install the Openhold AutoCAD add-in for all users, from an elevated PowerShell with AutoCAD closed:

   ```powershell
   cd C:\Openhold\Source
   .\tools\install-addin.ps1 -App AutoCAD -AllUsers
   ```

3. Set up Openhold Explorer on the machine as on any PC (**Settings**: server and vault). See
   [Client PCs](../install/clients.md).

## 2. Set the address for QR codes

The QR code links to the server's `…/verify/…` page, which needs no sign-in, so phones on the shop floor can use it.
Set the address phones reach the server at, e.g. `https://openhold.example.com`:

- Windows server: `PublicUrl` in `appsettings.Production.json`, then restart the **Openhold Server** service.
- Docker server: `OPENHOLD_PUBLIC_URL` in `.env`, then `docker compose up -d`.

Without it, QR codes use the address the job worker used.

## 3. Install the worker

### With Windows sign-in (Windows Server installs)

The worker runs as a Windows service and signs in as its service account.

1. Create a domain account for it, e.g. a group managed service account `EXAMPLE\openhold-jobs$`.
2. Add it to the **Job workers** group: **Tools › Administration… › Groups › Job workers**, type it under
   **Members**, **Save Changes**.
3. From an elevated PowerShell:

   ```powershell
   cd C:\Openhold\Source
   .\tools\install-job-worker.ps1 -Server https://openhold.example.com:5443 -Vault Production -ServiceAccount 'EXAMPLE\openhold-jobs$'
   ```

   Add `-DefaultPaper A3` to set the paper for layouts with no printer set up (without it, such a layout gets the
   paper closest to what's drawn on it).

The script installs the **Openhold Job Worker** service (starts with Windows, restarts after failures), starts it and
shows the first lines of its log, in `C:\Program Files\Openhold\Job Worker\Logs`.

### With an Openhold account (Docker installs)

The service needs Windows sign-in, which Docker servers don't offer, so run the worker in a signed-in Windows session
instead:

1. Create an Openhold account for the worker, e.g. `openhold-jobs`, with two-factor not required
   ([Add people](add-people.md#3-add-someone-with-an-openhold-account)), and add it to **Job workers**.
2. On the job-worker machine, sign in to Windows, open Openhold Explorer, set **Settings › Sign-in** to *Openhold
   account* and sign in as `openhold-jobs` once.
3. Build and run the worker:

   ```powershell
   cd C:\Openhold\Source
   dotnet publish src\Openhold.Jobs -c Release -o C:\Openhold\Jobs
   C:\Openhold\Jobs\OpenholdJobs.exe --default-paper A3
   ```

   It waits for jobs until you press Ctrl+C. It needs that Windows session to stay signed in. If its Openhold sign-in
   runs out, it waits and says so; sign in once more in Openhold Explorer.

Worker options:

| Option | Does |
|---|---|
| `--once` | Does the waiting jobs, then stops |
| `--default-paper A3` | Paper for layouts with no printer set up |
| `--corner TopRight` | Where the stamp goes: `TopLeft` (default), `TopRight`, `BottomLeft`, `BottomRight` |
| `--server URL --vault NAME` | Use this server and vault instead of the PC's settings (signs in with Windows) |

## 4. Test it

1. Release a test drawing (**Change State… › Release**).
2. Open **Tools › Administration… › Jobs** and click **Refresh**. The job's **Status** goes from queued to running
   to done; the **Show** list filters by status.
3. Right-click the drawing › **Open Published PDF**, and scan the QR code with a phone.

## If something goes wrong

- **Jobs stay queued.** The worker isn't running or can't reach the server. Check the service (or the console
  window) and its log.
- **"Ask an Openhold administrator to add this account to the job-workers group."** Add the worker's account to
  **Job workers** and save. The service checks again every ten minutes.
- **A job failed.** A failing job is retried up to three times. **Administration › Jobs** shows the reason in **Problem**. Common
  causes: AutoCAD isn't licensed for the worker's account, or the add-in isn't installed for all users.
- **`install-job-worker.ps1` refuses the address.** Give the server's `https://` address, e.g.
  `https://openhold.example.com:5443`.
- **The QR code opens the wrong address.** Set the public address (step 2).
- **"No PDF has been published … yet."** The release happened before the worker was running, or the job failed.
  Check **Administration › Jobs**.
