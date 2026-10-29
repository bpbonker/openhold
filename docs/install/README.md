# Installing Openhold

An Openhold installation has two parts:

- **A server** for each office. The first one is the **authority**: it holds the main database, hands out locks and
  part numbers, and records every change. More offices get their own server later (see
  [Adding an office](../howto/add-an-office.md)).
- **PCs**: Openhold Explorer on every engineer's PC, plus the add-ins for Inventor and AutoCAD.

## Pick a server setup

| You have | Use | Guide | Time |
|---|---|---|---|
| A Linux server or VM (Ubuntu, Debian, or anything that runs Docker) | Docker | [Ubuntu and Debian](ubuntu-debian.md), then [Docker](docker.md) | 15 minutes |
| Docker already | Docker | [Docker](docker.md) | 10 minutes |
| A Windows Server, and you want Windows (Kerberos) sign-in | Windows service | [Windows Server](windows-server.md) | 45 minutes |
| Just want to try it on your own PC | Docker Desktop | [Docker](docker.md) | 10 minutes |

Then set up the PCs: [Client PCs](clients.md).

## What's supported

| | Supported now | On the [roadmap](../../ROADMAP.md) |
|---|---|---|
| **Server** | Docker on any Linux x64 host (the guide covers Ubuntu 22.04 / 24.04 and Debian 12); Docker Desktop for trying out; Windows Server 2019 or later as a Windows service | Native Ubuntu/Debian packages (.deb with systemd), ARM64 images, a ready-made image on GitHub Container Registry, Kubernetes |
| **Database** | PostgreSQL 17 | Other PostgreSQL versions (not tested) |
| **Sign-in** | Openhold accounts with two-factor codes (all servers); Windows sign-in (Windows servers) | Organisation sign-in (OpenID Connect, e.g. Microsoft Entra ID) from Openhold Explorer and the add-ins, which the server already accepts; Windows (Kerberos) sign-in on Linux servers |
| **PCs** | Windows 10 and 11, x64 | Web client, macOS |
| **CAD** | Inventor 2025; AutoCAD 2025 and 2026, including AutoCAD Mechanical and AutoCAD Electrical | Inventor 2026 testing, Inventor 2027, prebuilt and signed add-in installers |
| **More offices** | Site and backup servers, with PostgreSQL streaming replication set up by hand | A guided setup for site servers in Docker |
| **Published PDFs** | Job worker on a Windows PC or VM with AutoCAD: as a Windows service with a Windows server, or in a signed-in session with an Openhold account ([how](../howto/publish-pdfs.md)) | The job worker as a service with an Openhold account |

## Sizing

For 20–60 users:

| | CPU | Memory | Disk |
|---|---|---|---|
| Trying it out | 2 cores | 4 GB | 20 GB |
| An office server | 4–8 cores | 16–32 GB | SSD: about three times the size of your files today, to leave room for versions |

A new version stores only the parts of a file that changed, so the store grows slowly. The first copy to a new
office takes roughly an hour per 40 GB at 100 Mbps; after that only changes travel.
