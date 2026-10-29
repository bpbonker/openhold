# Prepare an Ubuntu or Debian server

These steps install Docker and Git on a fresh **Ubuntu 22.04 / 24.04** or **Debian 12** server, then hand over to
the [Docker guide](docker.md). Run them as a user with `sudo`.

## 1. Install Docker and Git

The same commands work on Ubuntu and Debian:

```sh
sudo apt-get update
sudo apt-get install -y ca-certificates curl git
sudo install -m 0755 -d /etc/apt/keyrings
. /etc/os-release
sudo curl -fsSL "https://download.docker.com/linux/$ID/gpg" -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/$ID ${UBUNTU_CODENAME:-$VERSION_CODENAME} stable" \
  | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
```

Let your account run Docker without `sudo`, then sign out and back in:

```sh
sudo usermod -aG docker "$USER"
```

Check it works:

```sh
docker run --rm hello-world
```

Docker starts with the server, and the Openhold containers restart on their own after a reboot.

## 2. Install Openhold

Follow the [Docker guide](docker.md) from step 1.

## 3. Firewall

PCs need to reach the server's port: `8080`, or `443` once HTTPS is on. If you use `ufw`, note that ports
published by Docker bypass `ufw` rules. To limit who can connect, publish the port on one address only, e.g.
`"10.0.0.5:443:8443"` in `compose.override.yaml`, or use your network firewall.

## Without Docker

A native install (a `.deb` package with a systemd service) is on the [roadmap](../../ROADMAP.md). Until then, Docker
is the supported way to run Openhold on Linux.
