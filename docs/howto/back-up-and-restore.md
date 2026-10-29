# Back up and restore

This sets up regular backups of the Openhold server, checks they work, and restores from them. Set it up before
anyone uses the vault, and test a restore before you need one.

A backup must cover two things, taken together:

- **the database**: folders, versions, locks, part numbers, settings, users and the audit log;
- **the file store**: every version's contents, plus the token-signing key and logs.

Follow the section for your install: [Windows](#windows-installs) or [Docker](#docker-installs).

## Windows installs

Windows installs use `openhold-admin`, which encrypts everything (AES-256-GCM) under a passphrase you choose. It
backs up each vault's database, the file store (only new chunks each night), the server settings, the token-signing
key and the logs.

### Schedule the nightly backup

1. Choose a destination off the server, e.g. a NAS share `\\nas\openhold-backups`, and a passphrase of at least 16
   characters. **Store the passphrase in your password manager: without it the backups can't be restored.**
2. From an elevated PowerShell on the server, in the Openhold source folder:

   ```powershell
   cd C:\Openhold\Source
   .\tools\schedule-backup.ps1 -Destination \\nas\openhold-backups -Account 'EXAMPLE\openhold$' -Passphrase (Read-Host -AsSecureString)
   ```

   Use the server's service account for `-Account`: it can read the keys and the file store, and needs write access
   to the destination. Options: `-At 01:00` (the time), `-KeepDays 35`.

   The script installs `openhold-admin` in `C:\Program Files\Openhold\Admin`, keeps the passphrase there in a file
   only the backup account and administrators can read, and registers the task *Openhold nightly backup*.
3. Run it once now and check it finished:

   ```powershell
   Start-ScheduledTask 'Openhold nightly backup'
   ```

   Each run writes a log to `\\nas\openhold-backups\logs` and its outcome to
   `\\nas\openhold-backups\last-backup.json`.

### Check the backups weekly

```powershell
cd 'C:\Program Files\Openhold\Admin'
.\openhold-admin.exe verify --from \\nas\openhold-backups --passphrase-file .\backup-passphrase.txt
```

`Verified: … decrypt and are intact` means the latest run is good. Once a month, add `--all-chunks` to read every
file-store chunk rather than a sample.

### Test a restore (at least twice a year)

Restore into a spare database, never the live one; `openhold-admin` refuses a database that already has tables.

1. Create an empty database, in **SQL Shell (psql)**:

   ```sql
   CREATE DATABASE openhold_restore OWNER openhold;
   ```

2. Restore the latest run into it, with the file store and settings into spare folders:

   ```powershell
   cd 'C:\Program Files\Openhold\Admin'
   .\openhold-admin.exe restore --from \\nas\openhold-backups --vault Production `
     --connection "Host=localhost;Database=openhold_restore;Username=openhold" `
     --chunks E:\restore\chunks --settings-to E:\restore\settings `
     --passphrase-file .\backup-passphrase.txt --db-password-file .\db-password.txt
   ```

   `--run` picks an earlier run instead of the latest.
3. Record the date, how long it took and the result. Then drop the spare database and delete the folders.

### Restore for real

If the server is lost, restore onto a new server as above, into a new empty database. Install the server with the
restored settings (they're in the `--settings-to` folder, with the token-signing key), point `Vaults` › `Primary` and
`ChunkStorePath` at the restored database and file store, and start it. Run the restore with the passphrase from your
password manager (`$env:OPENHOLD_BACKUP_PASSPHRASE`, or a `--passphrase-file`), as the old server's copy is gone.

Check-ins made since that backup are lost at the authority; at a multi-office installation, versions checked in at
other offices still have their contents there. The full procedure, including promoting a standby, is in
[Recovering from a lost server](../administration.md#recovering-from-a-lost-server).

## Docker installs

A Docker install keeps everything in two volumes: `openhold_db` (the database) and `openhold_data` (the file store,
the token-signing key and the logs). `docker volume ls` shows their names; they start with the name of the folder
you cloned into.

These backups are **not encrypted**. Store them somewhere access-controlled, and encrypt them if they leave your
network.

### Back up

From the `openhold` folder on the server:

1. **The database**, as a PostgreSQL dump:

   ```sh
   docker compose exec -T db pg_dump -U openhold -Fc openhold > openhold-$(date +%F).dump
   ```

2. **The data volume**, as an archive. Take it after the dump, so every file the dump refers to is in it:

   ```sh
   docker run --rm -v openhold_data:/data -v "$PWD":/backup alpine tar czf /backup/openhold-data-$(date +%F).tgz -C /data .
   ```

3. **Your settings**: copy `.env`, `compose.override.yaml` and the `certs` folder with the backups.
4. Copy all of it off the server.

To run it nightly, put the two commands in a script and schedule it with `cron`, e.g. `/opt/openhold/backup.sh`:

```sh
#!/bin/sh
set -e
cd /opt/openhold
mkdir -p backups
docker compose exec -T db pg_dump -U openhold -Fc openhold > backups/openhold-$(date +%F).dump
docker run --rm -v openhold_data:/data -v "$PWD/backups":/backup alpine tar czf /backup/openhold-data-$(date +%F).tgz -C /data .
```

```sh
chmod +x /opt/openhold/backup.sh
( crontab -l 2>/dev/null; echo "0 1 * * * /opt/openhold/backup.sh" ) | crontab -
```

Then copy `/opt/openhold/backups` off the server, and delete old files there, on your own schedule.

### Restore

Restore into an empty database with the server stopped. Use the dump and archive from the same night.

1. Stop the server (the database keeps running):

   ```sh
   docker compose stop server
   ```

2. Replace the database with an empty one and restore the dump into it:

   ```sh
   docker compose exec -T db dropdb -U openhold openhold
   docker compose exec -T db createdb -U openhold -O openhold openhold
   docker compose exec -T db pg_restore -U openhold -d openhold --no-owner --exit-on-error < openhold-2026-10-29.dump
   ```

3. Replace the data volume's contents with the archive:

   ```sh
   docker run --rm -v openhold_data:/data -v "$PWD":/backup alpine sh -c "find /data -mindepth 1 -delete && tar xzf /backup/openhold-data-2026-10-29.tgz -C /data && chown -R 1654 /data"
   ```

   The server runs as user ID 1654 in its container, so it must own the files.
4. Start the server and check it:

   ```sh
   docker compose start server
   curl http://localhost:8080/health/ready
   ```

On a new machine, clone Openhold, put back `.env`, `compose.override.yaml` and `certs`, run `docker compose up -d` once
to create the volumes, then follow the steps above.

### Test a restore

Do it at least twice a year, on a spare machine or VM rather than the live server: follow *On a new machine* above,
sign in, and open a few files. Record the date and the result.

## If something goes wrong

- **The scheduled task fails.** Read the newest log in the destination's `logs` folder. Common causes: the account
  can't write to the destination, or can't reach the database.
- **"The target database isn't empty."** `openhold-admin` only restores into a new, empty database. Create one.
- **`verify` or `restore` asks for the passphrase.** Give `--passphrase-file`, or set
  `$env:OPENHOLD_BACKUP_PASSPHRASE` in that PowerShell window.
- **There's no `db-password.txt`.** `schedule-backup.ps1` only writes it when the service has a database password.
  Leave out `--db-password-file` and set `$env:PGPASSWORD` in that PowerShell window instead.
- **`pg_restore` reports errors.** The database wasn't empty. Run the `dropdb` and `createdb` commands again, then
  restore.
- **The Docker server won't start after a restore.** Check `docker compose logs server`. If it can't read its files,
  run the `chown` part of step 3 again.
