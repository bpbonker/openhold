# Add an office

This adds a server at another office, so people there open files at local network speed, and optionally a backup
server that keeps a full offsite copy.

**Site servers are set up by hand today.** These steps are for Windows site servers, as in the
[administrator guide](../administration.md#adding-a-site-another-office). A site server in Docker needs the same
settings and a replica database set up by hand; a guided Docker setup for site servers is on the
[roadmap](../../ROADMAP.md).

## How it fits together

- The first server, the **authority**, holds the main database: locks, part numbers and every change.
- Each **site** server has a read-only **streaming replica** of that database for fast local reads, and its own full
  copy of the files. Writes (check-out, check-in, state changes) go to the authority.
- A **backup** server is installed the same way, keeps a full copy and refuses all changes. It can be promoted if
  the authority is lost.
- Servers copy file contents between themselves, newest versions first, signing in with a **replication account**.

You need: a Windows Server at the office with PostgreSQL 17, a certificate for its DNS name, a VPN (or TLS) link to
the authority, and administrator access to both servers.

## 1. Let the office reach the authority's database

On the **authority's** PostgreSQL:

1. Create a login for replication (in **SQL Shell (psql)**):

   ```sql
   CREATE ROLE replicator WITH REPLICATION LOGIN PASSWORD 'choose-a-long-password';
   SELECT pg_create_physical_replication_slot('site_in');
   ```

   The slot (one per office) keeps the changes the office hasn't received yet, even while the link is down.
2. Allow the office's server in `pg_hba.conf` (in PostgreSQL's data folder), for replication and for Openhold's own
   writes. Replace `10.20.0.5` with the office server's address:

   ```
   host  replication  replicator  10.20.0.5/32  scram-sha-256
   host  openhold     openhold    10.20.0.5/32  scram-sha-256
   ```

   If the link between offices isn't a VPN, turn on SSL in PostgreSQL (`ssl = on`, with a certificate), use
   `hostssl` instead of `host`, and add `;SSL Mode=Require` to the `Primary` connection string in step 3.
3. Make sure `listen_addresses` in `postgresql.conf` includes the address the office connects to. Restart
   PostgreSQL.

The PostgreSQL documentation covers these settings in detail:
[streaming replication](https://www.postgresql.org/docs/17/warm-standby.html#STREAMING-REPLICATION).

## 2. Create the replica at the office

On the **office server**, with PostgreSQL 17 installed:

1. Stop the PostgreSQL service and empty its data folder (by default
   `C:\Program Files\PostgreSQL\17\data`).
2. Copy the authority's database into it, from an elevated PowerShell:

   ```powershell
   & 'C:\Program Files\PostgreSQL\17\bin\pg_basebackup.exe' -h openhold.example.com -U replicator `
     -D 'C:\Program Files\PostgreSQL\17\data' -R -X stream -S site_in -P
   ```

   `-R` writes the connection to the authority (`primary_conninfo`) and marks the database as a standby.
3. Give the PostgreSQL service's account (by default *Network Service*) full control of the data folder, then start
   the service.

The replica is read-only and follows the authority's changes as they happen.

## 3. Install the office's server

1. Install the server with `install-server.ps1`, as in [Install on Windows Server](../install/windows-server.md),
   including the certificate and, for Windows sign-in, `setspn` for this server's DNS name. Give it the
   `openhold` database password with `-DatabasePassword`: the replica has the same logins as the authority.
2. In `C:\Program Files\Openhold\Server\appsettings.Production.json`, set:

   | Setting | Set it to |
   |---|---|
   | `Role` | `Site` (or `Backup` for a backup server) |
   | `SiteId`, `SiteName` | A unique short code and a name, e.g. `IN` / `India`, or `HQ-OFF` / `Offsite backup` |
   | `PublicUrl` | This server's own address, e.g. `https://openhold-in.example.com:5443` |
   | `Vaults` › `Primary` | The authority's database: `Host=openhold.example.com;Port=5432;Database=openhold;Username=openhold` |
   | `Vaults` › `Local` | This office's replica: `Host=localhost;Port=5432;Database=openhold;Username=openhold` |
   | `Vaults` › `ChunkStorePath` | This office's file store, e.g. `D:/OpenholdData/chunks/production` |
   | `Replication` › `Username` | `openhold-replication` (step 4) |

   Use the same vault names as on the authority.

## 4. Create the replication account

Once, for all offices. In Openhold Explorer, signed in as an administrator:

1. **Tools › Administration… › Users › New Openhold Account…**. Username `openhold-replication`; untick
   **Require two-factor sign-in** (a server can't type codes); **Create**. Note the temporary password.
2. Sign in as `openhold-replication` once (e.g. on a spare PC, with **Settings › Sign-in** set to *Openhold
   account*) and choose its password. Store it in your password manager.
3. **Administration › Groups › Site servers**, add `openhold-replication` to **Members**, **Save Changes**. The group
   has the *Copy file contents between sites* privilege.
4. On **every** server (authority, sites and backup), set `Openhold:Replication:Username` to `openhold-replication`
   and put the password in the service's environment, never in a file. From an elevated PowerShell:

   ```powershell
   $key = 'HKLM:\SYSTEM\CurrentControlSet\Services\Openhold Server'
   $password = Read-Host 'Replication password' -AsSecureString
   $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($password))
   $environment = @((Get-ItemProperty $key -Name Environment).Environment | Where-Object { $_ -notlike 'Openhold__Replication__Password=*' })
   Set-ItemProperty $key -Name Environment -Type MultiString -Value ($environment + "Openhold__Replication__Password=$plain")
   Restart-Service 'Openhold Server'
   ```

Alternatively, leave `Replication:Username` empty: the servers then sign in as their Windows service accounts, which
must be members of *Site servers*.

## 5. Start it and watch it fill

1. Start (or restart) the office's **Openhold Server** service. It registers with the authority; the other servers
   find it and start copying files to it.
2. In Openhold Explorer, open **Administration › Sites**. Each server is listed with its **Role**, **Still to Arrive**
   (file versions it hasn't received yet) and **Last Reported**. **Still to Arrive** drops to zero when the office is
   up to date.
3. Point the office's PCs at the new server: **Settings › Server**, e.g. `https://openhold-in.example.com:5443`
   ([Client PCs](../install/clients.md)).

The first copy of a large vault takes time: roughly an hour per 40 GB at 100 Mbps. To seed an office faster, copy the
file store from another server onto a disk, ship it, and put it in the office's `ChunkStorePath` before starting; the
office then fetches only what's newer.

## A backup server

Follow the same steps with `Role` set to `Backup` and its own `SiteId` (e.g. `HQ-OFF`). It keeps a full copy of the
files and a standby database, allows browsing and getting files, and refuses all changes. If the authority is lost,
it can be promoted: see [Recovering from a lost server](../administration.md#recovering-from-a-lost-server).

A backup server is not a substitute for backups: a mistake replicates. Keep the encrypted backups too
([Back up and restore](back-up-and-restore.md)).

## If something goes wrong

- **Still to Arrive doesn't go down.** Check the office server's log (`LogFolder`, e.g. `D:\OpenholdData\logs`). A
  sign-in failure means the replication account's password or group membership is wrong. Click
  **Fetch Missing Files Now** on the **Sites** tab to make the server you're connected to fetch what it lacks straight
  away.
- **Last Reported is old.** The server is stopped, or can't reach the authority. Check the service and the link.
- **People at the office can browse but not check out or check in.** The link to the authority is down. Browsing and
  Get keep working; check-out and check-in say so and work again when the link is back.
- **Files show ↻.** They were checked in at another office and are still arriving. Get Latest still works: the
  server fetches them from the office that has them.
- **After upgrading a server, replication stopped.** Running `install-server.ps1` again rewrites the service's
  environment, which removes `Openhold__Replication__Password`. Add it again (step 4). See [Upgrade](upgrade.md).
