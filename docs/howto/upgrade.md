# Upgrade Openhold

This moves the server, Openhold Explorer and the CAD add-ins to a new version of Openhold. The server upgrades its own
database when it starts; you don't run any database scripts.

## Before you start

1. **Read the [changelog](../../CHANGELOG.md)** for the new version.
2. **Back up**, and check the backup finished ([Back up and restore](back-up-and-restore.md)). The database upgrade
   can't be undone except by restoring that backup.
3. **Pick a quiet time** and tell people. The server is unavailable for a few minutes. Ask everyone to check in what
   they can first.
4. **More than one office?** Upgrade the authority (the first server) first, since it's the one that upgrades the
   database, then each site and backup server.

## Docker installs

From the `openhold` folder on the server:

```sh
git pull
docker compose up -d --build
```

The new server image is built, the container is replaced, and the server upgrades the database on start. Your
`.env`, `compose.override.yaml`, certificates and both volumes are kept. Check it's back:

```sh
curl http://localhost:8080/health/ready
docker compose logs --tail 50 server
```

(Use your HTTPS address if you've turned HTTPS on.)

## Windows Server installs

1. Note any extra environment variables you've given the service, such as the replication password
   (`Openhold__Replication__Password`) or ERP secrets. `install-server.ps1` rewrites the service's environment and
   keeps only the database password, so you'll add the others back in step 4. To list them:

   ```powershell
   (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\Openhold Server').Environment
   ```

2. Update the source, from an elevated PowerShell:

   ```powershell
   cd C:\Openhold\Source
   git pull
   ```

3. Run the same install command as before. Leave out `-DatabasePassword` to keep the current one:

   ```powershell
   .\tools\install-server.ps1 -DataFolder D:\OpenholdData -ServiceAccount 'EXAMPLE\openhold$'
   ```

   It stops the service, replaces the program, keeps `appsettings.Production.json` and your data, starts the service
   and waits until it answers (*Openhold Server is running: …*).
4. Add back any environment variables from step 1 (see
   [Add an office, step 4](add-an-office.md#4-create-the-replication-account) for the commands), then
   `Restart-Service 'Openhold Server'`.
5. Update `openhold-admin` for the backups:

   ```powershell
   dotnet publish src\Openhold.Admin -c Release -o 'C:\Program Files\Openhold\Admin'
   ```

## Openhold Explorer

On the PC where you build it ([Client PCs](../install/clients.md)):

```powershell
cd openhold
git pull
dotnet publish src\Openhold.Desktop -c Release -o C:\Openhold\Explorer
```

Then copy the `C:\Openhold\Explorer` folder to each PC while Openhold Explorer is closed there. Settings
(`%APPDATA%\Openhold\client.json`), sign-ins and workspaces are kept.

## The Inventor and AutoCAD add-ins

On each PC with the CAD program, from the updated `openhold` folder:

```powershell
.\tools\install-addin.ps1 -App Inventor -WaitForClose
.\tools\install-addin.ps1 -App AutoCAD -WaitForClose
```

`-WaitForClose` builds first and swaps the add-in in the moment the CAD program closes, then says when it's safe to
start it again. Add `-AllUsers` (elevated) if it was installed for all users.

## The job worker and OpenholdImport

- **Job worker (service):** run the same `install-job-worker.ps1` command again; it stops the service, replaces the
  program and starts it. Update its AutoCAD add-in as above, with `-AllUsers`.
- **Job worker (run by hand):** stop it, run the `dotnet publish src\Openhold.Jobs …` command again, and start it.
- **OpenholdImport:** run its `dotnet publish` command again ([Import existing files](import-existing-files.md)).

## If something goes wrong

- **The server doesn't come back.** Read the log: `docker compose logs server`, or `LogFolder` (e.g.
  `D:\OpenholdData\logs`) and the Windows event log. A failed database upgrade is reported there.
- **You need to go back.** Install the previous version (`git checkout` the earlier version, then rebuild) and
  restore the backup you took before the upgrade, into an empty database. Don't point an older server at a database
  a newer one has already upgraded.
- **Replication or ERP sending stopped after a Windows upgrade.** An environment variable was removed: step 1 and 4.
- **A CAD program says the add-in can't be updated.** It's still running. Close it (check Task Manager for
  `Inventor.exe`, `acad.exe` or `accoreconsole.exe`), or use `-WaitForClose`.
