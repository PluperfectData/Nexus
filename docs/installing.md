# Installing

[Documentation](README.md) › Installing

The installers put Nexus on one computer: Windows, macOS, or Linux. On Windows there is nothing to type in a terminal.
On macOS and Linux, one command installs Nexus and another starts it. To work on Nexus's code instead, see
[Running Nexus](running.md). To run it in containers, see [Running Nexus in Docker](docker.md).

## Running the installer

Put the code at `Pluperfect.Data/Nexus`, as the README's [quick start](../README.md#quick-start) says. Then:

| On | Run |
|---|---|
| Windows 10 or 11 | Right-click `install.ps1` in that folder and choose **Run with PowerShell** |
| macOS | Open Terminal in that folder and run `bash install.sh` |
| Linux | Open a terminal in that folder and run `bash install.sh` |

Each one:

1. Installs whatever is missing: the .NET 10 SDK, Node.js, MySQL, Git, and Mailpit. A copy that is already installed
   is used as it is, such as a MySQL 8 or later.
   - **Windows** uses winget.
   - **macOS** uses Homebrew, and installs Homebrew first if it's missing, which asks for the Mac's password.
   - **Linux** downloads .NET, Node.js, MySQL 8.4, and Mailpit from their own sites into `data/tools`, with no
     password. Git, and any library MySQL or .NET needs that the computer lacks, come from apt or dnf, which asks for
     the password.
2. Fetches Pluperfect.Jwt and Pluperfect.Mail beside `Pluperfect.Data`, when they aren't there.
3. Asks whether to install Scry too, when Scry sits at `Pluperfect.Data/Scry`. Yes runs Scry's installer as well,
   and starts both.
4. Builds the service into `data/app/api`, and the pages into `data/app/site`.
5. Trusts the .NET development certificate, which serves every address below over HTTPS. Windows asks once, macOS asks
   for the password, and Linux trusts it for this user in Chrome, Edge, and Firefox. Then it copies the certificate
   into `data`, to serve with.
6. Adds a way to start Nexus again: a **Nexus** shortcut on the Windows desktop, or a **Nexus** entry in the Linux app
   menu. On a Mac, `sh start.sh` starts it.
7. Starts Nexus, and opens it.

Running it again rebuilds Nexus from the code in the folder, and skips the programs already installed. Close Nexus's
window first; the installer stops while anything uses port 6163, including a development copy or Nexus in Docker.

The development certificate lasts a year. When it runs out, starting Nexus says so, and running the installer again
renews it.

⚠ **On Windows and macOS, the MySQL that winget or Homebrew installs is a server for the whole computer, but Nexus
doesn't use it.** Nexus runs one of its own, in its data folder, so it never touches another database on the
computer.

## The first run

The browser opens `https://localhost:6165`, and the installer page starts at Mail, then the first admin. Nexus already
knows its database and its addresses, and a page on this computer needs no install code. Then make a workspace.
[The installer](running.md#the-installer) covers each step.

The first start brings Nexus's new database up to date, which takes a minute or two.

## Starting and stopping

| On | Start Nexus with |
|---|---|
| Windows | The **Nexus** shortcut on the desktop |
| macOS | `sh start.sh` in Terminal, in the Nexus folder |
| Linux | **Nexus** in the app menu, or `sh start.sh` in a terminal, in the Nexus folder |

Its window shows what the service is doing; keep it open while you use Nexus. **Ctrl+C**, or closing the window,
stops it.

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
| `data/app` | The built service, in `api`, and pages, in `site` |
| `data/mysql` | Nexus's own MySQL server, holding the `nexus` database |
| `data/secrets.json` | That server's passwords, made on the first start |
| `data/mysql-client.cnf` | Its root password again, for the MySQL tools that starting Nexus runs |
| `data/api` | What the service keeps: its settings in `instance.json`, its keys, uploaded files, and its logs |
| `data/install.json` | Where the installer found .NET, MySQL, and Mailpit |
| `data/tools` | On Linux, the .NET, Node.js, MySQL, and Mailpit the installer downloaded |
| `data/mailpit.db` | The mail Mailpit keeps |
| `data/certificate.pem`, `data/certificate.key` | The development certificate and its key, which the pages and the service serve with |
| `data/logs/mysql.log` | The database server's log |
| `data/logs/mailpit.log` | Mailpit's log |

## Removing it

Close Nexus, then delete the Nexus folder. On Windows, delete the desktop shortcut too; on Linux, delete
`~/.local/share/applications/nexus.desktop`. ⚠ Deleting `data` deletes everything Nexus holds, its connections and
sign-ins included. On Windows and macOS, the programs the installer added stay installed; on Linux, the ones in
`data/tools` go with the folder.
