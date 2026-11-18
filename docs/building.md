# Building and testing

[Documentation](README.md) › Building and testing

.NET SDK 10.0.302 or a later feature band (`Server/global.json`).

```bash
dotnet build Server/Nexus.slnx
```

```bash
dotnet test Server/Nexus.slnx
```

Restores are locked to `packages.lock.json`. After a dependency change, run
`dotnet restore --force-evaluate` and commit the lock files.

`Server/tests/Nexus.Schwab.Tests` answers every request from canned responses and needs no Schwab account.

`Server/tests/Nexus.Sql.Tests` needs a MySQL, a PostgreSQL, and a SQL Server server. Each fixture creates a
database named `nexus_test_<id>` and drops it afterwards, so each login needs permission to create and drop
databases. Give one connection string per server, as a user secret or an environment variable:

| User secret | Environment variable |
|---|---|
| `ConnectionStrings:MySql` | `ConnectionStrings__MySql` |
| `ConnectionStrings:Postgres` | `ConnectionStrings__Postgres` |
| `ConnectionStrings:SqlServer` | `ConnectionStrings__SqlServer` |

```bash
dotnet user-secrets set ConnectionStrings:MySql "Server=localhost;User ID=...;Password=..." --project Server/tests/Nexus.Sql.Tests
```

CI starts all three as service containers. Their password comes from the repository secret
`CI_DATABASE_PASSWORD`: letters and digits, at least 8 characters, with upper case, lower case, and a digit.
SQL Server rejects a weaker one.
