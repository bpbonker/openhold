# Install the server with Docker

This runs the Openhold server and its PostgreSQL database as two containers. It works on any Linux x64 machine with
Docker, and on Docker Desktop for Windows or macOS if you just want to try Openhold.

New Ubuntu or Debian server without Docker yet? Do [Ubuntu and Debian](ubuntu-debian.md) first.

## 1. Get Openhold

```sh
git clone https://github.com/bpbonker/openhold.git
cd openhold
```

## 2. Settings

```sh
cp .env.example .env
```

Open `.env` and set `OPENHOLD_DB_PASSWORD` to a long random password. To make one:

```sh
openssl rand -base64 24
```

The other settings are optional:

| Setting | What it is | Default |
|---|---|---|
| `OPENHOLD_PORT` | The port PCs connect to | `8080` |
| `OPENHOLD_SITE_ID`, `OPENHOLD_SITE_NAME` | This office's short code and name | `HQ`, `Head office` |
| `OPENHOLD_PUBLIC_URL` | The address PCs use, e.g. `https://openhold.example.com` | empty |
| `OPENHOLD_ADMIN` | The first administrator's username | `admin` |
| `OPENHOLD_INITIAL_ADMIN_PASSWORD` | Their temporary password. Leave empty to have one generated | empty |

## 3. Start it

```sh
docker compose up -d
```

The first start builds the server, which takes a few minutes. Check it's running:

```sh
curl http://localhost:8080/health/ready
```

`{"status":"ok",...}` means it's ready.

## 4. The first administrator

On its first start the server creates the administrator account (`admin` unless you changed `OPENHOLD_ADMIN`)
with a temporary password. If you didn't set one, read the generated one:

```sh
docker compose exec server cat /data/keys/initial-admin-password.txt
```

Now set up a PC ([Client PCs](clients.md)), choose **Openhold account** as the sign-in method, and sign in as the
administrator. You'll be asked to choose a new password straight away. Then turn on two-factor sign-in for the
account in **Tools › My Account › Set Up Two-Factor Sign-In**, and delete the file:

```sh
docker compose exec server rm /data/keys/initial-admin-password.txt
```

Next: [set up your vault](../howto/set-up-your-vault.md) and [add people](../howto/add-people.md).

## 5. Turn on HTTPS

Do this before real use: without it, passwords cross the network unencrypted. You need a certificate for the
server's DNS name (from your organisation's certificate authority, or a public one) as two PEM files.

1. Put them in a `certs` folder next to `compose.yaml`, named `openhold.crt` and `openhold.key`.
2. Create `compose.override.yaml` next to `compose.yaml` (Docker Compose reads it automatically):

   ```yaml
   services:
     server:
       ports: !override
         - "443:8443"
       environment:
         ASPNETCORE_HTTPS_PORTS: "8443"
         ASPNETCORE_HTTPS_PORT: "443"
         Kestrel__Certificates__Default__Path: /certs/openhold.crt
         Kestrel__Certificates__Default__KeyPath: /certs/openhold.key
       volumes:
         - ./certs:/certs:ro
   ```

3. Restart: `docker compose up -d`. PCs now use `https://<server name>/`.

The private key must be readable by the account the server runs as (user ID 1654 in the container):

```sh
sudo chown 1654 certs/openhold.key && sudo chmod 400 certs/openhold.key
```

## Everyday tasks

| To | Run |
|---|---|
| See the server's log | `docker compose logs -f server` |
| Stop / start | `docker compose stop` / `docker compose start` |
| Upgrade to a new version | Take a backup, then `git pull` and `docker compose up -d --build`. The server upgrades its database itself. |
| Back up | See [Back up and restore](../howto/back-up-and-restore.md) |

## Where the data lives

| Volume | Holds |
|---|---|
| `openhold_db` | The database: folders, versions, locks, part numbers, settings, users, audit log |
| `openhold_data` | The file store (every version's contents), the token signing key and the logs |

Back up both. Removing the containers (`docker compose down`) keeps the volumes; `docker compose down -v` deletes
them and everything in your vault.
