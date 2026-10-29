# Openhold administrator guide

This guide covers installing, configuring, running and recovering Openhold.
- For security controls and procedures, see [SECURITY.md](SECURITY.md).
- For everyday use, see the [user guide](user-guide.md).

## What runs where

| Piece | Where | What it does |
|---|---|---|
| **Openhold Server** (Windows service) | One per site | Serves its office. The **authority** holds the main database: locks, numbers and every change. |
| **PostgreSQL 17** | One per site | The authority's database is the primary. Each other site has a streaming replica for fast local reads. |
| **File store** | With each server | File contents, split into chunks named by their hash. Each site keeps a full copy. |
| **Openhold Explorer** | Every PC | The desktop app: browse, check out, check in, lifecycles, catalogue, administration. |
| **CAD add-ins** | PCs with AutoCAD / Inventor | Check-out on edit, check-in with references and properties, from inside the CAD program. |
| **Job worker** (Windows service) | A PC or VM with AutoCAD | Publishes stamped PDFs when drawings are released. |
| **openhold-admin** | The server | Encrypted backup, restore and verify. |
| **OpenholdImport** | A PC with Inventor and AutoCAD | Brings existing project folders in, with their references. |

Sites keep working when the link between them is down:
- **Browsing and Get** keep working.
- **Check-out, check-in and other changes** wait until the link to the authority is back, and say so.

## Installing the authority (first server)

1. **Install PostgreSQL 17.**
   - Create a database and a login for Openhold.
   - Keep the password for step 4.
2. **Get a certificate** for the server's DNS name (e.g. from your internal CA) into *Local Computer › Personal*.
3. **Create the service account.**
   - A group managed service account is recommended (e.g. `EXAMPLE\openhold$`).
   - For Windows sign-in over HTTPS, register its service principal name once. This needs a domain admin:
     `setspn -S HTTP/openhold.example.com EXAMPLE\openhold$`
4. **Install the server.** From an elevated PowerShell in the Openhold folder:
   ```powershell
   .\tools\install-server.ps1 -DataFolder D:\OpenholdData -ServiceAccount 'EXAMPLE\openhold$' -DatabasePassword (Read-Host -AsSecureString)
   ```
   The first run creates `appsettings.Production.json` from the example and stops. Edit that file:
   - the certificate subject;
   - `SiteId` / `SiteName` (e.g. `AU` / `Australia`);
   - `PublicUrl` (the address PCs and other sites use);
   - `Administrators` (the first administrator, who can then grant everything else);
   - `HomeDomains` (your Windows domain names, so people are known by their plain username);
   - the vaults' database connection and file-store paths.

   Run the script again. It installs the **Openhold Server** service, which restarts after failures, then starts it and waits for it to answer.
5. **Schedule backups** (see [Backups](#backups)) before anyone uses it.

To upgrade: take a backup, then run the same `install-server.ps1` command with the new version. The server upgrades its own databases on start.

### More than one vault

A server can host several vaults, e.g. *Production* and a *Sandbox* for training. Each vault has its own database, file store, configuration and history. Add vaults under `Openhold:Vaults` in the settings. PCs choose a vault in Settings.

## Adding a site (another office)

Each office has its own server, so everyone opens files from their own office's copy.

1. **Set up a streaming replica** of the authority's database at the site.
   - This uses PostgreSQL's standard `pg_basebackup` plus `primary_conninfo`, over a VPN or TLS.
   - The replica is read-only, and the site's server reads from it.
2. **Install the site's server** with `install-server.ps1`. In its settings:
   - `Role`: `Site`
   - a unique `SiteId` (e.g. `IN`) and its own `PublicUrl`
   - for each vault:
     - `Primary`: the authority's database (writes go there);
     - `Local`: the site's replica (reads come from here);
     - `ChunkStorePath`: the site's own file store.
3. **Create the replication account** the servers use to copy files between sites:
   - In Administration › Users, create an Openhold account (e.g. `openhold-replication`).
   - Sign in as it once to set its password. Leave two-factor off: a server can't type codes.
   - Add it to the **Site servers** group, which has the *Copy file contents between sites* privilege.
   - Put its name in each server's settings (`Openhold:Replication:Username`) and its password in the service environment (`Openhold__Replication__Password`), never in a file.
   - Alternatively, leave the username empty and the servers sign in as their Windows service accounts. Those accounts must then be in the Site servers group.
4. **Start the service.** The server registers itself with the authority. The other sites then find it and copy files to and from it.
   - The first copy of a large vault can take a while.
   - Administration › **Sites** shows each site's backlog ("Still to arrive"), which drops to zero when the site is up to date.
   - To seed a new site faster, copy the file store from another site onto a disk and ship it. The site then fetches only what's newer.

**A backup server** (e.g. an offsite copy in the same country) is installed the same way:
- `Role` is `Backup`, and it needs its own `SiteId` (e.g. `AU-OFF`).
- It keeps a full copy of the files and refuses all changes.
- If the authority is lost, it can be promoted (see [Recovering from a lost server](#recovering-from-a-lost-server)).

### How replication behaves

- **New versions arrive first.** A check-in is copied to the other sites straight away, newest versions first.
- **Where content comes from.** Each server fetches what it lacks from whichever site has it, and checks every chunk against its hash.
- **While a file is on its way:** people at other sites see it as *syncing* (a ↻ in its status bubble). Get Latest still works: their server fetches it from the site that has it first.
- **Check-out always confirms the latest version with the authority**, so nobody ever edits an old version.
- **When the link to the authority is down,** a site keeps browsing and getting files. Check-out and check-in fail with a message saying so until the link is back.

## Sign-in and accounts

People sign in one of three ways, set in `Openhold:Authentication`:

| Method | Who | Notes |
|---|---|---|
| **Windows** (Kerberos) | People on the company domain | Nothing to type. Use for staff. |
| **Openhold accounts** | Partners, contractors, sites on another domain | Password plus optional two-factor (authenticator app). Created in Administration › Users. |
| **Organisation accounts** (OpenID Connect) | e.g. Microsoft Entra ID | Uses the organisation's own sign-in and MFA. |

Administration › **Users** (needs *Manage Users*):
- **Create, disable and enable accounts.** Disabling stops every sign-in method on every server within 30 seconds.
- **Require, reset or turn off two-factor sign-in.**
- **Reset passwords.** The person sets their own password at their next sign-in.
- **Unlock accounts and sign people out everywhere.**

Windows and organisation accounts appear the first time they sign in.

## Groups, privileges and permissions

**Groups** (Administration › Groups) are lists of people. Groups are used in three places:
- **Lifecycles:** who may make a change, and who signs off approvals.
- **Privileges:** rights beyond everyday work. Nobody has them unless a group gives them; administrators have them all.

  | Privilege | Allows |
  |---|---|
  | Manage Configuration | Edit everything in Administration except accounts |
  | Manage Users | Create, disable and reset accounts |
  | Change Category | Change a file's category |
  | Delete Released Files | Delete files in a released state |
  | Part Librarian | Release, obsolete and reinstate catalogue items |
  | Run Jobs | The job worker's account |
  | View Audit | Read the audit log and access reviews (auditors) |
  | Copy file contents between sites | The site servers' replication account |

- **Folder permissions** (Administration › **Permissions**). A rule gives a group (or everyone) one of three levels on a folder and everything below it:
  - *Hidden*: the folder and its files can't be seen anywhere, not in search, Where Used or the trash;
  - *Read only*: see and get files;
  - *Change*: check out, check in, rename, delete, change state.

  How rules are decided:
  - The deepest folder with a rule for someone's groups decides.
  - If several of their groups have rules there, the most generous wins.
  - "Folders no rule covers" sets the default.
  - **Explain why** shows, for any person and folder, what they may do and which rule decides it, including changes not saved yet.

Everything in Administration except Users is one configuration, saved as a new version with a note of what changed. **History** shows every version, who saved it and why, and loads any of them back.

## Files: categories, lifecycles, revisions, numbering

- **Categories** decide each kind of file's lifecycle, revision scheme and numbering. New files get the first category whose extensions and folders match.
- **Lifecycles** are states (e.g. Work in Progress, Released, Obsolete) and the changes between them. For each change you can set:
  - who may make it;
  - required approvals ("2 of the Checkers group"), never by the person who asked;
  - a required comment;
  - "everything it uses must be released first";
  - publishing a stamped PDF.
- **Revisions** never change on their own. Check-in and check-out only add versions; a revision changes only when someone uses **Revise**. Revision schemes are built from parts, e.g. letters skipping I, O, Q, S, X and Z, or numbers.
- **Numbering** schemes are yours to build. A new vault has none: Openhold doesn't impose prefixes, part types or number formats. Numbers are reserved while someone saves and released if they cancel, and are never reused unless you allow it.

### Building a numbering scheme

**Administration › Numbering › Add Scheme** (or **From a Template…**). A scheme is a list of **parts**, in order; **Numbers look like** shows the result as you build it.

| Part | What it gives | Its settings |
|---|---|---|
| Fixed text | The same text in every number: a prefix (`DOC`) or a separator (`-`) | The text |
| Pick list | One of a list of codes, e.g. `05` = Hydraulics | The codes and their meanings; a default |
| Typed value | Text people type | Letters, digits or both; shortest and longest; a default |
| Code of a folder above | The code of a folder named `CODE - Name` above where the file is saved | Optionally the codes allowed; a default |
| Parent assembly's number | The number of the assembly the file is made in | — |
| Counter | 001, 002, 003… (one per scheme) | Digits |
| Check digit | One digit that catches typing mistakes | — |
| Year | 26 or 2026 | Two or four digits |
| Site code | The office issuing the number, e.g. AU | — |

Each value part has a **name** (e.g. Area) and the question people see (e.g. *Area of the machine*). **The counter counts separately for each different text before it**: Project, then Area, then Counter numbers every area of every project from the first number. Once a scheme has issued numbers its parts are fixed (codes can still be added); build a new scheme for a different structure. **Template text (advanced)** shows the scheme as one line, for copying it between vaults.

### Naming rules

New files are named by rules you set in Administration, not in code. **Administration › Naming Rules** shows them all on one page, with a **Try a name** box that shows what a file saved in a given folder (inside an assembly or not) would be called.

1. **Where a file is saved decides its category.** This is the first category, top to bottom, whose file types and folders match (Categories tab).
2. **The category decides the numbering scheme.** A category can have a second scheme for files **made inside an assembly**, e.g. components numbered after their assembly (Parent assembly's number, `-`, Counter gives `PRJ01-02-001-001`).
3. **The scheme's values fill themselves in:**
   - **A folder above:** the code of a folder named `CODE - Name`. Folder parts are matched top-down in order, so a project folder fills the project and an area folder below it fills the area. Folder parts side by side (e.g. Application, Type, Equipment with nothing between) share one folder code such as `MR03`.
   - **The parent assembly:** its number.
   - **A default**, when nothing else gives a value.

   People can still change anything that was filled in, and pick or type the rest.
4. **Requiring the rule** (the *Required* tick box) refuses a new file at check-in if its name doesn't follow the category's scheme(s). Files already in the vault are never affected. Without it, the rule is a suggestion.

**Templates.** **From a Template…** on the Numbering tab adds an editable copy of a ready-made scheme:
- part numbers with a check digit;
- project → area → count;
- parts of an assembly;
- family prefix;
- documents by year;
- numbers per site.

**Saving from CAD.** In Inventor, **Save to Openhold** (Openhold tab) applies these rules on a new document's first save. It takes the number only when Save is clicked, sets Part Number, and saves into the workspace. A drawing is saved beside its model, under the model's name.

## The catalogue (item master)

The catalogue gives every part one **part number**, used by every site and every connected ERP. **Administration › Item Classes › Catalogue items are numbered by** picks the scheme (any scheme you built; e.g. a prefix, six digits from 100000 and a check digit gives `P-104527-3`). A class can use its own scheme instead. Until a scheme is chosen, new items can't be added.

- **Who creates items.** Anyone can create a **Draft** item, which gets its number straight away. A **part librarian** (the *Part Librarian* privilege) checks it and **releases** it. Only released items go to the ERPs. Items are never deleted; they're made **Obsolete**, with a reason.
- **Item classes** (Administration › Item Classes) sort parts into a tree, e.g. *Electrical / Circuit breakers*. Each class has **attributes** (e.g. *Rated current (A)*); an item in a class has its own class's attributes and those of every class above it.
  - Required attributes must be filled in before release.
  - Allowed values make an attribute a drop-down.
- **Duplicates.** A manufacturer part number can belong to one item only (capitals, spaces and dashes are ignored), so a bought part can't get two numbers. New items are also checked for the same part number under a different manufacturer.
- **Other numbers** (legacy and ERP cross-references) are kept and searchable, but are never the key.

## ERP connections

ERP connections are optional. With none, Openhold sends nothing anywhere. With one or more switched on, released catalogue items (and later changes to them) are sent to each.

**Setting one up:** Administration › **ERP** › New Connection.
1. Choose the kind:
   - **simPRO** creates catalogue items in simPRO;
   - **CSV file** keeps a file of released items for any other system to import.
2. Fill in its fields. For simPRO:
   - the address (e.g. `https://yourcompany.simprosuite.com`);
   - the company ID (`0` on a single-company build);
   - the **catalogue group** Openhold's items go into;
   - an **API key** made in simPRO, or a client ID and secret.
3. Save, then **Test**. Test signs in and reads from the ERP without changing anything.
4. **Send All Released Items** queues what's already released.

Secrets such as API keys are stored encrypted and never shown again; type a new one to replace it.

**What the simPRO connector does to simPRO:**
- It creates items only in **its own top-level catalogue group** (e.g. *Openhold Parts*). Create that group in simPRO first.
- It **never changes items it didn't create.** If simPRO already has an item with the same part number, Openhold reports it and leaves it alone. Before every update, Openhold checks the item is still in its group.
- On updates it changes only the **name**, **manufacturer** and **archived** flag. Notes, prices and suppliers kept in simPRO are left as they are. Making an item obsolete in Openhold archives it in simPRO.

**Sending and retries:**
- Changes are queued in the same step as the item change itself, and sent within a minute.
- If the ERP is down, sending is retried with growing gaps, up to six hours apart.
- Where each item stands in each ERP is shown at the bottom of the item in the Catalogue; failures are also in the server log.

**Connections in the server's settings file.** Connections can also be put in the server's settings file (`Openhold:ErpConnections`), with secrets in the service environment (e.g. `Openhold__ErpConnections__0__Settings__ApiKey`). These show in Administration but can only be changed in the file.

Other ERPs plug in through the same interface (`IErpConnector`).

## Job worker

The job worker publishes stamped PDFs when drawings are released. Each PDF carries a QR code that shows whether that printout is still current. Install it on a machine with AutoCAD:

```powershell
.\tools\install-job-worker.ps1 -Server https://openhold.example.com:5443 -Vault Production -ServiceAccount 'EXAMPLE\openhold-jobs$'
```

- **The service account** must be in the vault's **Job workers** group and licensed for AutoCAD.
- **Logs** go to `Logs` beside `OpenholdJobs.exe`.
- **Job status:** Administration › Jobs shows waiting, running and failed jobs.

## Bringing existing files in

`OpenholdImport` copies a project folder (for example the working folder of another PDM, such as Autodesk Vault) into Openhold. It brings each file's references (Inventor assemblies and drawings, AutoCAD xrefs), iProperties, title-block values and preview. The source folder is only read: AutoCAD and Inventor only ever open the copies.

```powershell
OpenholdImport "D:\Projects\PRJ01" '$/Legacy/PRJ01' --dry-run --report prj01.csv   # always first
OpenholdImport "D:\Projects\PRJ01" '$/Legacy/PRJ01' --read-only
```

- **The dry run** writes a report and changes nothing. The report shows each file's part number, description and revision, and references that point outside the folder.
- **The import** checks files in children first, and records where each came from ("Imported From").
- **Options:**
  - `--read-only` makes the folder read-only for everyone except administrators.
  - `--state Released` sets a state after the import.
- **If an import stops,** run the same command again; files already imported are skipped.

## Backups

`tools/schedule-backup.ps1` schedules a nightly encrypted backup with `openhold-admin`. It covers:
- each vault's database (pg_dump);
- the file store, copied incrementally (only new chunks);
- the server settings, the token-signing key and the logs.

Everything is encrypted with AES-256-GCM under a passphrase. The scheduled task reads it from a file only administrators can read; keep a copy off the server too.

```powershell
.\tools\schedule-backup.ps1 -Destination \\nas\openhold-backups -Account 'EXAMPLE\openhold$' -Passphrase (Read-Host -AsSecureString)
```

- Keep the passphrase in the organisation's password manager: **without it the backups can't be restored**.
- Check the backups weekly:
  ```
  $env:OPENHOLD_BACKUP_PASSPHRASE = Read-Host 'Backup passphrase'
  openhold-admin verify --from \\nas\openhold-backups
  ```
  Add `--all-chunks` once a month to read every chunk.
- **Rehearse a restore at least twice a year**, into a spare database, and record the result:
  ```
  openhold-admin restore --from \\nas\openhold-backups --vault Production --connection "Host=…;Database=openhold_restore;Username=…" --chunks E:\restore\chunks --settings-to E:\restore\settings
  ```
  Restore refuses a database that already has data, so a live vault can't be overwritten by mistake.

## Recovering from a lost server

| Lost | Do this |
|---|---|
| **A site server** | Reinstall it with the same `SiteId`. It copies its files back from the other sites. Its replica is rebuilt from the authority. |
| **The authority, with a standby** | Promote the standby database (`pg_ctl promote`). Change the new authority server's `Role` to `Authority`, and point the other sites' `Primary` at it. Then update `sites.is_authority` in the database. |
| **The authority, from backup** | Restore the newest backup with `openhold-admin restore` onto a new server. Then install the server with the restored settings and start it. Check-ins made since that backup at the authority itself are lost; versions checked in at other sites still have their contents there. |

## Monitoring and logs

- **Server logs:** `LogFolder` (daily files, kept 90 days by default). Warnings and errors also go to the Windows event log.
- **Health:** `https://<server>/health` answers without sign-in, for uptime monitors.
- **Sites:** Administration › Sites shows each site's backlog and when it last reported.
- **ERP:** a failed send appears on the item in the Catalogue and in the server log.
- **Audit:** Administration › **Audit** (needs *View Audit*) lists sign-ins, failed sign-ins, lockouts, check-ins, state changes, approvals, deletions, configuration saves and catalogue changes. **Export Access Review** saves the quarterly access review as a CSV.

## Before each release of Openhold (maintainers)

Run `tools/security-check.ps1` (vulnerable dependencies, a software bill of materials, secrets) and the full test suite. Both must pass.
