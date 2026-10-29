# Install the server on Windows Server

This installs Openhold as a Windows service. Choose it when you want people on your Windows domain signed in
automatically (Kerberos); otherwise [Docker](docker.md) is quicker.

You need Windows Server 2019 or later, an administrator account on it and, for Windows sign-in, a domain
administrator for one command.

## 1. Install the prerequisites

1. **PostgreSQL 17.** Download the Windows installer from [postgresql.org](https://www.postgresql.org/download/windows/)
   and install it with the defaults. Then, in **SQL Shell (psql)**, create Openhold's database and login:

   ```sql
   CREATE ROLE openhold LOGIN PASSWORD 'choose-a-long-password';
   CREATE DATABASE openhold OWNER openhold;
   ```

2. **The .NET 10 SDK** from [dot.net](https://dotnet.microsoft.com/download/dotnet/10.0), used to build the server.
3. **Git** from [git-scm.com](https://git-scm.com/download/win), then get Openhold:

   ```powershell
   git clone https://github.com/bpbonker/openhold.git C:\Openhold\Source
   ```

4. **A certificate** for the server's DNS name (e.g. `openhold.example.com`) from your organisation's certificate
   authority, imported into *Local Computer › Personal* (`certlm.msc`).

## 2. Create the service account

A group managed service account is best, e.g. `EXAMPLE\openhold$`. For Windows sign-in over HTTPS, a domain
administrator registers its service principal name once:

```powershell
setspn -S HTTP/openhold.example.com EXAMPLE\openhold$
```

## 3. Install

From an elevated PowerShell (*Run as administrator*):

```powershell
cd C:\Openhold\Source
.\tools\install-server.ps1 -DataFolder D:\OpenholdData -ServiceAccount 'EXAMPLE\openhold$' -DatabasePassword (Read-Host -AsSecureString)
```

The first run creates `C:\Program Files\Openhold\Server\appsettings.Production.json` and stops. Open it and set:

| Setting | Set it to |
|---|---|
| `Kestrel` › `Certificate` › `Subject` | The certificate's DNS name |
| `SiteId`, `SiteName` | This office's short code and name, e.g. `HQ` / `Head office` |
| `PublicUrl` | The address PCs use, e.g. `https://openhold.example.com:5443` |
| `Administrators` | The first administrator's Windows account, e.g. `EXAMPLE\jsmith` |
| `HomeDomains` | Your domain names, so people are known by their plain username |
| `Vaults` › `Primary` | `Host=localhost;Port=5432;Database=openhold;Username=openhold` (the password stays out of the file) |
| `Vaults` › `ChunkStorePath` | Where file contents go, e.g. `D:/OpenholdData/chunks/production` |

Run the same command again. It installs and starts the **Openhold Server** service, opens the port in Windows
Firewall and waits until the server answers:

```
Openhold Server is running: site HQ (Head office), https://localhost:5443
```

## 4. First sign-in

Set up a PC ([Client PCs](clients.md)) with the server's `PublicUrl` and **Windows** sign-in. The administrator you
named signs in without a password and can then give others access: see [Add people](../howto/add-people.md).

To use Openhold accounts instead (no domain), name a plain username such as `admin` in `Administrators` and set the
`OPENHOLD_INITIAL_ADMIN_PASSWORD` environment variable on the service before its first start, or read the generated
password from `keys\initial-admin-password.txt` in the data folder.

## 5. Back up

Schedule encrypted backups before anyone uses it: see [Back up and restore](../howto/back-up-and-restore.md).

## Upgrade

Take a backup, update the source (`git pull` in `C:\Openhold\Source`), and run the same `install-server.ps1`
command. It stops the service, replaces the program, keeps your settings and data, and starts it again; the server
upgrades its database itself.
