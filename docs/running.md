# Running Nexus

[Documentation](README.md) › Running Nexus

## Starting the service and the client

The service runs on `http://localhost:6163`, and on `https://127.0.0.1:6164` for sign-in callbacks. Its client runs
on `http://localhost:6165`. Start Mailpit, which keeps the mail the service sends and shows it at
`http://localhost:8025`. Then start both, and open `http://localhost:6165/install`.

```bash
mailpit
```

```bash
dotnet run --project Server/src/Nexus.Service
```

```bash
npm install --prefix Client
```

```bash
npm run dev --prefix Client
```

The client takes `@pluperfect/jwt` from a sibling checkout of `Pluperfect.Jwt` beside `Pluperfect.Data`, and
the service takes `Pluperfect.Jwt` and `Pluperfect.Mail` the same way. Build `Pluperfect.Jwt/client` once with
`npm run build` before installing the client.

## In Docker, with the -dev containers

Instead of installing .NET, MySQL, and Mailpit, the code can run in containers that reload as it does from the code.
With Docker running, double-click `Nexus Dev.cmd` on Windows, or run `sh nexus-dev.sh` on macOS or Linux. The
launcher:

1. Fetches Pluperfect.Jwt and Pluperfect.Mail beside `Pluperfect.Data` with Git, when they aren't there.
2. Asks whether to start Scry's -dev containers too, when Scry sits at `Pluperfect.Data/Scry`.
3. Makes the database's password the first time, in `.env.dev` beside `compose.dev.yaml`, which git ignores.
4. Builds the images from `Dockerfile.dev` and starts the containers. The first build takes several minutes.
5. Asks the computer to trust the authority behind the callback address, the first time, as the
   [Docker launcher](docker.md#certificates) does.
6. Opens `http://localhost:6165`, then watches the folders.

| Container | What |
|---|---|
| `nexus-api-dev` | The service, under `dotnet watch`, in Development, at `http://localhost:6163` |
| `nexus-web-dev` | The pages, under Vite's dev server, at `http://localhost:6165` |
| `nexus-callback-dev` | Caddy, serving `https://127.0.0.1:6164`, where providers such as Schwab send people back, to the service |
| `nexus-db-dev` | MySQL, at 127.0.0.1, port 6167, for MySQL Workbench or the `mysql` client, as `nexus`, with the `NEXUS_DEV_DB_PASSWORD` in `.env.dev` |
| `nexus-mail-dev` | Mailpit, at `http://localhost:6168` |

A file saved under `Server/src`, `Client/src`, or `Client/public`, or under Pluperfect.Jwt's or Pluperfect.Mail's
`src`, is copied into its container: the service takes the change, restarting when it must, and the page reloads. A
change to `package.json`, `package-lock.json`, `Directory.Packages.props`, or Pluperfect.Jwt's client rebuilds its
image. The window shows the containers' lines, including the installer's address with its install code on a new
database, since requests reach the service through Docker. **Ctrl+C** stops them.

Your own builds don't reach the containers; they build their own. They take development's ports, so run the code, the
-dev containers, or [Docker](docker.md) one at a time. Their data is in the volumes `nexus-dev-database`,
`nexus-dev-data`, `nexus-dev-mail`, and `nexus-dev-caddy`. Removing them in Docker Desktop starts the -dev containers
over.

## The installer

An instance starts with nothing set. Until it has a database, a client address, and an admin, every route but
`/install` and `/health` answers 503, and the client sends its visitors to `/install`. The installer asks, in turn:

1. **The database:** a MySQL 8.4 server's host, port, database, user, and password. Nexus makes the database if it
   is missing and the user may, applies its migrations, and keeps the settings only once that works. A database
   that already has an admin skips step 4, which is how an existing instance is adopted.
2. **The client's address:** where people open Nexus. CORS and the origin check allow it alone, and mailed links
   and connect pages point there.
3. **Mail:** through Postmark, with its server token and sender, or to Mailpit, which delivers nothing and keeps each
   message for reading on its own page. A test mail goes to an address you name, and mail may stay off. Development
   names a Mailpit at `http://localhost:8025`, so the step starts there.
4. **The first admin:** an email, signed in at once. Once mail works the password is optional, and an admin without
   one signs in with mailed links. With mail off, a password is required.
5. **The callback address:** the https address providers send people back to after signing in, at `/oauth/callback`.

Admins change all of it later on the Settings page. A new database is opened and brought up to date before
Nexus moves to it, and a database without an admin sends the instance back to step 3.

A page on the machine the service runs on uses the installer as it is. Any other page needs the install code,
which the service writes to its log, with a link to `/install` that carries it, at each start while it is not set
up. The code is sent as `Authorization: Bearer nxi_...`, and five wrong codes lock the installer for 15 minutes. A
page counts as on the machine when it reaches the service over loopback with no proxy's forwarding headers, at a
loopback name, from a page at a loopback name. With [`Nexus:Proxied`](configuration.md) set, as behind Caddy, the
address the proxy forwards is the one checked, so a page on any other machine still needs the code.

Someone locked out uses the installer's Make an admin, which makes an address an admin with a new password and
signs it out everywhere else. On another machine it asks the service to write a code to its log, good for an hour.
A database that stops opening at a start sends the instance back to step 1, with the stored settings filled in.

The settings are kept in `data/instance.json`, with the database password, the Postmark token, and the session
signing key encrypted by the Data Protection keys in `data/keys`. The service makes the signing key on its first
start. On a box, `data/` must persist across releases.

A Debug build has one more command, `reset`, which sets `data/instance.json` aside under a dated name, so the next
start opens the installer at its first step. The database, `data/keys`, and `data/files` stay: entering the same
database in the installer adopts it again, and a new name starts empty. It refuses outside Development, and a
Release build has no such command. Stop the service before running it.

```bash
dotnet run --project Server/src/Nexus.Service -- reset
```
