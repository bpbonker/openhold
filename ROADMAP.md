# Openhold roadmap

The aim is to match Autodesk Vault Basic for daily use, then Autodesk Vault Workgroup and Professional. Features
are configured, not hard-coded, so any organisation can adapt them.

**Parity goal:** anything Autodesk Vault does, Openhold should do too. Openhold may recommend a different default,
but doesn't drop a capability.

Items marked *(done)* are in the current beta. What runs where today is listed in
[Installing Openhold](docs/install/README.md#whats-supported).

## Platforms and installation

- **Server**
  - Docker on Linux x64 and Docker Desktop (done); Windows Server as a Windows service (done).
  - A ready-made server image on GitHub Container Registry, so Docker installs don't build from source.
  - Native Ubuntu and Debian packages (`.deb` with a systemd service), without Docker.
  - ARM64 server images.
  - A Helm chart for Kubernetes.
  - A guided Docker setup for site and backup servers, including the database replica.
  - Windows (Kerberos) sign-in on Linux servers.
  - Organisation sign-in (OpenID Connect) from Openhold Explorer and the add-ins.
  - The job worker as a Windows service signing in with an Openhold account, for Docker servers.
  - Encrypted backups (`openhold-admin`) inside the Docker image.
- **PCs**
  - Prebuilt Openhold Explorer downloads and a signed installer (MSI), deployable by Intune or Group Policy.
  - Building Openhold Explorer, the job worker and the import tool without Inventor installed (today the build
    PC needs Inventor 2025).
  - Prebuilt, signed Inventor and AutoCAD add-in installers (today they're built on a PC with the CAD program).
  - Inventor 2026 testing, and an Inventor 2027 build (.NET 10).
  - A web client for viewing, approvals and non-CAD staff; macOS access through it.
- **Database:** testing with PostgreSQL versions other than 17.

## Administration

- **Multiple vaults per install,** e.g. Production plus Sandbox/Training. Each vault has its own database
  and file store; users choose a vault when connecting (done).
- **Administration** in Openhold Explorer:
  - sites and servers (Authority / Site / Backup) (done); site ownership
  - users, groups and permissions (done)
  - numbering schemes, naming rules, categories, lifecycles and revision schemes (done)
  - item classes and ERP connections (done)
  - title-block and iProperty mappings
- **Client connection setup** (done): server and workspace per PC, a Test connection button, shown on first
  run and shared by Explorer and the CAD add-ins.

## Done

- Content-addressed storage: dedup, verified, batched transfer.
- Server:
  - all-or-nothing batched check-out
  - atomic, idempotent check-in
  - reservation-based numbering
  - append-only audit trail
  - Kerberos sign-in
- Workspace engine:
  - read-only until checked out
  - always edit the latest version
  - local edits backed up, never silently lost
  - safe for several Openhold programs on one PC
- AutoCAD / AutoCAD Electrical plugin:
  - ribbon and check-in dialog
  - title blocks read into properties, with a warning when sheets disagree
  - whole `.wdp` project sets move together
  - headless DWG indexing
- Inventor add-in:
  - ribbon
  - check-out offered as soon as an edit starts
  - check-in of the whole file graph with iProperties and references
- Openhold Explorer desktop app:
  - browse, status, details, history, uses / where used
  - drag-in to add
- CODESYS: handled through Openhold Explorer; build output and user files are never versioned.

## 1. Find & view

- Search by name, any property, check-in comment and who changed it, scoped to a folder or the whole vault
  (done). Saved searches.
- Property columns in Explorer, chosen per user (done).
- Thumbnails and preview for DWG, IPT, IAM, IDW, PDF and Office files (thumbnails done).
- Open or get any earlier version. Restore an earlier version as a new version, so history is never rewritten
  (done).

## 2. Manage files

- Rename, move and delete (to a trash bin), repairing references in parent files: Inventor through
  Apprentice, DWG xrefs through the plugin (done except folder rename and moving .wdp projects).
- Copy Design: copy an assembly or project tree with new numbers, including drawings (done, including the
  next free numbers from a scheme; library and buyout parts stay shared).
- Pack and Go: export a file and everything it uses (done).
- Check out or get a whole assembly, folder or project in one action (done).
- Attachments, e.g. a PDF or spec attached to a drawing.

## 3. Edit, number & release

- Edit iProperties and title-block attributes in Explorer, written back into the files, for many files at once
  (done).
- Numbering on first save from configured schemes (done in Inventor with **Save to Openhold**; AutoCAD to come).
- **Configurable workflows:**
  - categories (e.g. Electrical drawing, Mechanical part, CODESYS project, document) (done)
  - each with its own lifecycle states, transitions, who may perform them, approval steps (self-release,
    checker, checker + approver, …), conditions and actions (done)
  - defined as data and edited in Administration (done)
- **Revise** is an explicit action using configurable revision schemes. Openhold never revises automatically
  (done: schemes per category, minor and major revisions; released files are reopened first).
- Properties a file can't hold (e.g. a DWG property with no title-block attribute) kept in the vault (done).
- PDF generated on release (done for DWG and PDF, by the job worker; Inventor drawings to come).
- **"Is this current?" QR code** stamped on released PDFs. Scanning a printed copy shows whether it's still
  the latest released revision (done).
- Library and bought-out parts: categories with a library lifecycle and numbering of the organisation's own
  design (done; Openhold ships no numbering, see "Numbering you build" below); an item master with manufacturer
  part numbers and duplicate checks (done, see section 5).
- **Numbering you build** (done): schemes are built part by part in Administration (fixed text, pick lists, typed
  values with rules, folder codes, the parent assembly's number, counter, check digit, year, site), optionally from
  a template. A new vault has no schemes; the catalogue's scheme is a setting.

## 4. Multi-site + security

Topology is configuration, not code: any number of sites, each **Authority**, **Site** or **Backup**.
The lock and metadata authority can run on-prem or in the cloud, using the same software.

- **Site ownership:** each folder or project is owned by one site, whose server grants check-outs for it.
  - Offices keep working fully, including new check-outs, while links are down.
  - Only files owned by another site need the link.
  - Ownership is handed over deliberately, e.g. when a project moves to another office.
- **Peer-to-peer chunk replication:** every server fetches missing chunks from whichever peers have them
  (a mesh, not a hub), verified by hash, newest versions first (done). Choosing the fastest peer and adaptive
  link tuning to come.
- **Offline outbox:** check-ins made during an outage are queued at the site and committed exactly once
  when the link returns.
- **Backup servers:** full chunk copy plus a standby database, read-only, ready to be promoted
  (role and refusal of changes done).
- **Cloud authority option:** only site servers talk to it, over mutual TLS. File contents never leave the sites.
- Site-leased number blocks, so numbering works offline.
- Windows sign-in in production (done).
- Users, groups, folder permissions and lifecycle permissions, with an "Explain why" view (done).
- TLS between clients and servers (done); mutual TLS between sites; encrypted backups (done).
- **"Syncing" status per site** (done). When someone at another site checks in, the file shows as *syncing*
  at your site until its new content has arrived there. Get Latest still works: your server fetches the
  content from the site that has it. Check-out always confirms the latest version with the authority first.

## 5. Items, BOMs, change orders, ERP

- Item master with classes, attributes and approved manufacturers (done).
- BOMs from Inventor assemblies and AutoCAD Electrical projects.
- Change orders: affected files and items, markups, approvals, release together.
- ERP connectors behind one interface (done: CSV and simPRO; others plug in through the same interface).

## Later

- Ask someone to release a file; check-out hand-over.
- Visual and CODESYS-code compare between versions.
- Transmittals.
- Plain-English search.
- Web viewer for non-CAD staff.
