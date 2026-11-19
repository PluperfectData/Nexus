# Configuration

[Documentation](README.md) › Configuration

Every setting the installer and the Settings page take can be set in configuration instead, for an operator or for
debugging. A configured value wins over the stored one, and the pages show it without offering to change it.

| Setting | Environment variable | Takes the place of |
|---|---|---|
| `ConnectionStrings:Nexus` | `ConnectionStrings__Nexus` | The database |
| `Nexus:ClientOrigin` | `Nexus__ClientOrigin` | The client's address |
| `Nexus:CallbackOrigin` | `Nexus__CallbackOrigin` | The address providers send people back to |
| `Mail:ServerToken` | `Mail__ServerToken` | Postmark's server token |
| `Nexus:MailFrom`, `Nexus:MailFromName` | `Nexus__MailFrom`, `Nexus__MailFromName` | The sender |
| `Jwt:SigningKey` | `Jwt__SigningKey` | The session signing key the service makes, at least 32 bytes |
| `Mail:PickupDirectory` | `Mail__PickupDirectory` | Sending mail, in a Debug build running as Development: it is written to files there until a Postmark token and sender are saved. Anywhere else it is ignored, and the log says so. |
| `Nexus:Schwab:AppKey`, `Nexus:Schwab:AppSecret` | `Nexus__Schwab__AppKey`, `Nexus__Schwab__AppSecret` | Each workspace's Schwab app |
| `Nexus:LogDays` | `Nexus__LogDays` | How many days the Log page keeps an entry |

For example, as a user secret:

```bash
dotnet user-secrets set ConnectionStrings:Nexus "Server=localhost;Database=nexus;User ID=...;Password=..." --project Server/src/Nexus.Service
```

`Nexus:Schwab:AppKey` and `Nexus:Schwab:AppSecret` set one Schwab app for every workspace, in place of each
workspace's own. `Nexus:Schwab:CallbackUrl` sets a callback for Schwab alone, in place of the instance's.

```bash
dotnet user-secrets set Nexus:Schwab:AppKey "<app key>" --project Server/src/Nexus.Service
```

```bash
dotnet user-secrets set Nexus:Schwab:AppSecret "<app secret>" --project Server/src/Nexus.Service
```

`Nexus:Proxied` (`Nexus__Proxied`), `false` unless set, says browsers reach the service only through a proxy, such
as Caddy. When `true`, the service believes the proxy's `X-Forwarded-For` and `X-Forwarded-Proto`: who asked, for the
install code's check, and whether it came over HTTPS, which the session and antiforgery cookies need outside
Development. Docker's `compose.yaml` sets it. ⚠ Set it only where the service's own port can't be reached except
through the proxy. Anyone who reaches it directly could otherwise claim any address, including this machine's.

`add-admin` makes or restores an admin, prints a link that signs them in once, within an hour, and exits.

```bash
dotnet run --project Server/src/Nexus.Service -- add-admin you@example.com
```
