# How Nexus is built

[Documentation](README.md) › How Nexus is built

Nexus is a service with pages of its own, built on a library of connectors.

Source and destination connectors for .NET 10. A source reads streams of records; a destination writes
them; an action changes something on a service, such as placing an order. A source runs standalone with
records in memory, or piped into a destination.

## The repository

| Path | Contents |
|---|---|
| `Server/src/Nexus.Abstractions` | Contracts, messages, errors, `HttpRetry`, `ReadToMemoryAsync`, `Tap` |
| `Server/src/Nexus.Csv` | `CsvSource` |
| `Server/src/Nexus.DuckDb` | `DuckDbDestination` |
| `Server/src/Nexus.Push` | `PushDestination`, which writes through an app's push API, such as Scry's |
| `Server/src/Nexus.Sql` | `SqlSettings` and the reader shared by the SQL sources |
| `Server/src/Nexus.MySql` | `MySqlSource` |
| `Server/src/Nexus.Postgres` | `PostgresSource` |
| `Server/src/Nexus.SqlServer` | `SqlServerSource` |
| `Server/src/Nexus.Schwab` | `SchwabSource`, `SchwabOrders`, and the order types |
| `Server/src/Nexus.Service` | The Nexus service, which keeps connections and runs syncs into apps' push APIs |
| `Client` | The service's pages, a React client: the installer, signing in, workspaces, and for admins, users and settings |
| `Server/tests/Nexus.Abstractions.Tests` | NUnit and Shouldly |
| `Server/tests/Nexus.Csv.Tests` | NUnit and Shouldly, against in-memory files |
| `Server/tests/Nexus.DuckDb.Tests` | NUnit and Shouldly, against a temp DuckDB file |
| `Server/tests/Nexus.Push.Tests` | NUnit and Shouldly, against a fake that keeps Scry's push API contract |
| `Server/tests/Nexus.Schwab.Tests` | NUnit and Shouldly, against canned Schwab responses |
| `Server/tests/Nexus.Service.Tests` | NUnit and Shouldly, hosting the service over a SQLite file with a fake source and push API |
| `Server/tests/Nexus.Sql.Tests` | NUnit and Shouldly, against real MySQL, PostgreSQL, and SQL Server servers |

## Sibling checkouts

The service builds against two other repositories, which it takes by relative path, so each must sit where Nexus
expects:

```text
any-folder/
├── Pluperfect.Data/
│   └── Nexus/         this repository
├── Pluperfect.Jwt/    github.com/mnwachukwu/Pluperfect.Jwt: sessions
└── Pluperfect.Mail/   github.com/mnwachukwu/Pluperfect.Mail: mail
```
