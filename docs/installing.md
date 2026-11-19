# Installing on Windows

[Documentation](README.md) › Installing on Windows

The installer puts Nexus on one Windows computer, with nothing to type in a terminal. To work on Nexus's code
instead, see [Running Nexus](running.md). To run it in containers, see [Running Nexus in Docker](docker.md).

## Running the installer

Put the code at `Pluperfect.Data\Nexus`, as the README's [quick start](../README.md#quick-start) says. Then, on
Windows 10 or 11, right-click `install.ps1` in that folder and choose **Run with PowerShell**. It:

1. Installs whatever is missing through winget: the .NET 10 SDK, Node.js, MySQL, Git, and Mailpit. A MySQL 8 or
   later that is already installed is used as it is.
2. Fetches Pluperfect.Jwt and Pluperfect.Mail beside `Pluperfect.Data`, when they aren't there.
3. Asks whether to install Scry too, when Scry sits at `Pluperfect.Data\Scry`. Yes runs Scry's installer as well,
   and starts both.
4. Builds the service into `data\app\api`, and the pages into `data\app\site`.
5. Trusts the .NET development certificate, which serves every address below over HTTPS. Windows asks once. Then it
   copies the certificate into `data`, for the shortcut to serve with.
6. Puts a **Nexus** shortcut on the desktop, starts Nexus, and opens it.

Running it again rebuilds Nexus from the code in the folder, and skips the programs already installed. Close
Nexus's window first; the installer stops while anything uses port 6163, including a development copy or Nexus in
Docker.

The development certificate lasts a year. When it runs out, the shortcut says so, and running the installer again
renews it.

⚠ **MySQL's installer adds MySQL to the computer, but Nexus doesn't use that server.** It runs one of its own, in its
data folder, so it never touches another database on the computer.

## The first run

The browser opens `https://localhost:6165`, and the installer page starts at Mail, then the first admin. Nexus already
knows its database and its addresses, and a page on this computer needs no install code. Then make a workspace.
[The installer](running.md#the-installer) covers each step.

The first start brings Nexus's new database up to date, which takes a minute or two.

## Starting and stopping

The desktop shortcut starts Nexus. Its window shows what the service is doing; keep it open while you use Nexus.
**Ctrl+C**, or closing the window, stops it.

| Address | What |
|---|---|
| `https://localhost:6165` | Nexus |
| `https://localhost:6163` | Its service |
| `https://127.0.0.1:6164` | Where providers such as Schwab send people back after they sign in |
| `http://localhost:6168` | Mailpit, where Nexus's mail waits to be read until Postmark is set up |

A development copy uses the same ports, so run one or the other, not both. Nexus's database server listens on
`127.0.0.1:6167`, for Nexus alone.

## Where everything is

Everything an install keeps is in `data` in the Nexus folder, which git ignores:

| Path | What |
|---|---|
| `data\app` | The built service, in `api`, and pages, in `site` |
| `data\mysql` | Nexus's own MySQL server, holding the `nexus` database |
| `data\secrets.json` | That server's passwords, made on the first start |
| `data\mysql-client.cnf` | Its root password again, for the MySQL tools the shortcut runs |
| `data\api` | What the service keeps: its settings in `instance.json`, its keys, uploaded files, and its logs |
| `data\install.json` | Where the installer found .NET, MySQL, and Mailpit |
| `data\mailpit.db` | The mail Mailpit keeps |
| `data\certificate.pem`, `data\certificate.key` | The development certificate and its key, which the pages and the service serve with |
| `data\logs\mysql.log` | The database server's log |
| `data\logs\mailpit.log` | Mailpit's log |

## Removing it

Close Nexus, then delete the Nexus folder and the desktop shortcut. ⚠ Deleting `data` deletes everything Nexus holds,
its connections and sign-ins included. The programs the installer added stay installed.
