# Running Nexus

[Documentation](README.md) › Running Nexus

## Starting the service and the client

The service runs on `http://localhost:6163`, and on `https://127.0.0.1:6164` for sign-in callbacks. Its client runs
on `http://localhost:6165`. Start both, then open `http://localhost:6165/install`.

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

## The installer

An instance starts with nothing set. Until it has a database, a client address, and an admin, every route but
`/install` and `/health` answers 503, and the client sends its visitors to `/install`. The installer asks, in turn:

1. **The database:** a MySQL 8.4 server's host, port, database, user, and password. Nexus makes the database if it
   is missing and the user may, applies its migrations, and keeps the settings only once that works. A database
   that already has an admin skips step 4, which is how an existing instance is adopted.
2. **The client's address:** where people open Nexus. CORS and the origin check allow it alone, and mailed links
   and connect pages point there.
3. **Mail:** Postmark's server token and sender, with a test mail to an address you name. Mail may stay off.
   A Debug build running as Development writes mail to `data/mail`, and the step says so.
4. **The first admin:** an email, signed in at once. Once mail works the password is optional, and an admin without
   one signs in with mailed links. With mail off, a password is required.
5. **The callback address:** the https address providers send people back to after signing in, at `/oauth/callback`.

Admins change all of it later on the Settings page. A new database is opened and brought up to date before
Nexus moves to it, and a database without an admin sends the instance back to step 3.

A page on the machine the service runs on uses the installer as it is. Any other page needs the install code,
which the service writes to its log, with a link to `/install` that carries it, at each start while it is not set
up. The code is sent as `Authorization: Bearer nxi_...`, and five wrong codes lock the installer for 15 minutes. A
page counts as on the machine when it reaches the service over loopback with no proxy's forwarding headers, at a
loopback name, from a page at a loopback name; a box behind Caddy's Unix socket always needs the code.

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
