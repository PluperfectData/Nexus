# Connectors

[Documentation](../README.md) › Connectors

| Connector | Project | Source | Destination | Actions |
|---|---|---|---|---|
| Charles Schwab | `Nexus.Schwab` | ✓ | | ✓ |
| CSV | `Nexus.Csv` | ✓ | | |
| DuckDB | `Nexus.DuckDb` | | ✓ | |
| MySQL | `Nexus.MySql` | ✓ | | |
| PostgreSQL | `Nexus.Postgres` | ✓ | | |
| Push API | `Nexus.Push` | | ✓ | |
| SQL Server | `Nexus.SqlServer` | ✓ | | |

Planned sources: Google Sheets and Stripe.

Each connector's page:

- [The connector contract](contract.md): the interfaces every connector implements, and the column types, errors,
  secrets, files, and HTTP retries they share
- [CSV](csv.md)
- [MySQL, PostgreSQL, and SQL Server](sql.md)
- [Charles Schwab](schwab.md)
- [DuckDB](duckdb.md)
- [Push API](push.md)
- [Using the connectors in code](usage.md)
