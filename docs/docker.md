# Running Nexus in Docker

[Documentation](README.md) › Running Nexus in Docker

Nexus in three containers, on Windows, macOS, or Linux, instead of the [installer](installing.md)'s programs.
Docker Desktop is a large install, so on Windows the installer is the simpler way in; Docker is for anyone who has it
already. For working on the code in Docker, see
[In Docker, with the -dev containers](running.md#in-docker-with-the--dev-containers).

## Starting it

Put the code at `Pluperfect.Data/Nexus`, as the README's [quick start](../README.md#quick-start) says. Start Docker
Desktop, or Docker on Linux, and wait until it is running. Then, in the Nexus folder:

| On | Run |
|---|---|
| Windows | Double-click `Nexus Docker.cmd` |
| macOS or Linux | `sh nexus-docker.sh` |

The launcher:

1. Fetches Pluperfect.Jwt and Pluperfect.Mail beside `Pluperfect.Data` with Git, when they aren't there.
2. Asks whether to start Scry too, when Scry sits at `Pluperfect.Data/Scry`. See [Running both](#running-both).
3. Makes the database's password the first time, in `.env` beside `compose.yaml`, which git ignores.
4. Builds the images from the folder and starts them. The first build takes several minutes.
5. Asks the computer to trust Nexus's certificates, the first time. See [Certificates](#certificates).
6. Prints the installer's address with its install code while Nexus isn't set up, and opens Nexus.

The installer page asks for that install code, since requests through Docker don't reach Nexus from this computer as
Nexus sees it. Then it asks for Mail and the first admin; Nexus already knows its database and addresses. Mail starts at Mailpit,
so the first admin may skip the password.

| Address | What |
|---|---|
| `https://localhost:6165` | Nexus |
| `https://localhost:6163` | Its service |
| `https://127.0.0.1:6164` | Where providers such as Schwab send people back after they sign in |
| `http://localhost:6168` | Mailpit, where Nexus's mail waits to be read |

Docker Desktop's links to these ports open them at `http://127.0.0.1`, and Caddy sends each one on to its
`https://localhost` address, where Nexus's cookies and its service's allowed origin apply. Its link for `nexus-web`
opens the lowest port, 6163, the service's, so a browser asking for that root goes on to the pages.

A development copy uses the same ports, so stop one before starting Docker; the launcher says so when a port is
taken. Docker keeps the containers running, and starts them again with Docker. Stop them from Docker Desktop, or with
`docker compose stop` in the Nexus folder.

## Certificates

Caddy, in the `nexus-web` container, serves all three addresses over HTTPS, with certificates from an authority it
makes itself and keeps in the `nexus-caddy` volume. The launcher adds that authority to the ones the computer trusts,
the first time it meets it:

| On | What happens |
|---|---|
| Windows | Windows asks whether to trust it. Choose **Yes**. It goes in your account's trusted roots. |
| macOS | macOS asks for your password, and it goes in your login keychain. |
| Linux | `sudo` asks for your password, and it goes in the system's trusted certificates. Chrome's own store gets it too, when `certutil` is installed. |

⚠ Firefox on Linux keeps its own list. Import `root.crt` from the `nexus-web` container in its certificate settings,
or it warns at each address.

Declining leaves the browser warning at each address, and Nexus's pages can't reach the service until you open
`https://localhost:6163` once and continue past the warning. Deleting the `nexus-caddy` volume makes a new authority,
which the launcher asks about on its next run.

## What runs

`compose.yaml` holds four services, published on this computer only:

| Service | Image | What |
|---|---|---|
| `nexus-web` | `ghcr.io/pluperfectdata/nexus-web` | Caddy, serving the pages, the service, and the callback address over HTTPS |
| `nexus-api` | `ghcr.io/pluperfectdata/nexus-api` | The service, which browsers reach only through `nexus-web` |
| `nexus-db` | `mysql:8.4` | The `nexus` database, not published outside Docker |
| `nexus-mail` | `axllent/mailpit:v1.33` | Mailpit, which keeps the mail the service sends until Postmark is set up, at `http://localhost:6168` |

Both of Nexus's images come from the one `Dockerfile`, as its `api` and `web` targets. Pluperfect.Jwt and
Pluperfect.Mail come into the build as its `jwt` and `mail` build contexts.

The data is in four Docker volumes: `nexus-database` holds the database, `nexus-data` what the service keeps (its
settings, keys, files, and logs), `nexus-caddy` Caddy's authority and certificates, and `nexus-mail` the mail Mailpit
keeps. ⚠ `docker compose down --volumes` deletes all four, and everything Nexus holds with them.

Since every browser request reaches the service through Caddy, `compose.yaml` sets `Nexus__Proxied`, and the service reads
who asked and over what from Caddy's forwarding headers. See [configuration](configuration.md).

## Running both

With Scry beside Nexus, yes to the launcher's question starts `compose.both.yaml`: Nexus's and Scry's compose files
together, on one network. Scry's server reaches Nexus at `http://nexus-api:6163`, which Scry's Link Nexus form starts
with, and tells Nexus to push rows to `http://scry-api:6161`. Those two addresses stay inside Docker's network, so
they are plain HTTP. Link them from a Scry workspace's Settings page, as on any other install. Scry's own launcher can start both the same way.

Each product keeps its own volumes, so its data is the same whether it runs alone or with the other.

## Running a published release

The [Docker workflow](../.github/workflows/docker.yml) builds both images for amd64 and arm64 on every `v` tag and
pushes them to GitHub's container registry, tagged with the version: `v0.2.0` becomes `0.2.0` and `0.2`. To run one
instead of building, once the launcher has made `.env`, set the version and pull, in the Nexus folder:

```powershell
$env:NEXUS_VERSION = "0.2.0"; docker compose pull nexus-api nexus-web; docker compose up --detach --no-build
```
