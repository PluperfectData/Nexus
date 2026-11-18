# MySQL, PostgreSQL, and SQL Server

[Documentation](../README.md) › [Connectors](README.md) › MySQL, PostgreSQL, and SQL Server

| Class | Id | Driver |
|---|---|---|
| `MySqlSource` | `mysql` | MySqlConnector |
| `PostgresSource` | `postgres` | Npgsql |
| `SqlServerSource` | `sqlserver` | Microsoft.Data.SqlClient |

Settings, keyed by `SqlSettings`:

| Key | Kind | Notes |
|---|---|---|
| `host` | Text | Required. |
| `port` | Number | Blank uses the driver's default. |
| `database` | Text | Required. |
| `user` | Text | Required. |
| `password` | Secret | |
| `tls` | Choice | MySQL: `None`, `Preferred`, `Required`, `VerifyCA`, `VerifyFull`. PostgreSQL: `Disable`, `Allow`, `Prefer`, `Require`, `VerifyCA`, `VerifyFull`. SQL Server: `Optional`, `Mandatory`, `Strict`. Blank uses the driver's default. |
| `trustServerCertificate` | Boolean | SQL Server only. |

- Streams: every table and view. MySQL names each for its table, from the configured database. PostgreSQL
  and SQL Server name each `schema.table`, from every schema except `information_schema`, `pg_*`, and
  `sys`.
- Primary key: the table's primary key constraint. Views have none.
- Cursor fields: `Int64`, `Decimal`, `Date`, `Timestamp`, and `DateTime` columns, except `Decimal(38, 18)`.
- Full refresh: one query, yielded in batches of 10,000 rows, then a `StateCheckpoint` of `{}`.
- Incremental: `WHERE cursor >= @value ORDER BY cursor`, with a checkpoint of
  `{"cursor": "...", "value": "..."}` after each batch. `>=` re-reads the rows at the saved value; write
  with `Upsert` to collapse them. Rows with a null cursor are never read. Saved state naming a different
  cursor is ignored, with a `LogMessage`.

| `ColumnType` | MySQL | PostgreSQL | SQL Server |
|---|---|---|---|
| `Int64` | integer types, `bit(n)` below 64, `year` | `smallint`, `integer`, `bigint` | `tinyint`, `smallint`, `int`, `bigint` |
| `Decimal` | `decimal(p, s)`; `bigint unsigned` and `bit(64)` as `(20, 0)` | `numeric(p, s)`; `money` as `(19, 2)` | `decimal`, `numeric`; `money` as `(19, 4)`; `smallmoney` as `(10, 4)` |
| `Decimal(38, 18)` | `decimal` above 38 digits | `numeric` with no precision or above 38 digits | |
| `Float64` | `float`, `double` | `real`, `double precision` | `float`, `real` |
| `Boolean` | `tinyint(1)`, `bit(1)` | `boolean` | `bit` |
| `Date` | `date` | `date` | `date` |
| `DateTime` | `datetime` | `timestamp` | `datetime`, `datetime2`, `smalldatetime` |
| `Timestamp` | `timestamp`, read at UTC | `timestamptz` | `datetimeoffset` |
| `Time` | `time` | `time` | `time` |
| `Json` | `json` | `json`, `jsonb`, arrays | `json` |
| `Binary` | `binary`, `varbinary`, blob types | `bytea` | `binary`, `varbinary`, `image`, `rowversion` |
| `String` | text types, `enum`, `set`; spatial as WKT; others through `CAST(... AS CHAR)` | text types, `name`; PostGIS as WKT; others through `::text` | text types; `geometry` and `geography` as WKT; others through `CONVERT(nvarchar(max), ...)` |

- `Decimal(38, 18)` rounds to 18 places.
- MySQL zero dates and PostgreSQL `infinity` and `-infinity` read as null.
- A value the contract cannot carry is `SourceError` naming its column: MySQL `TIME` outside 00:00 to 24:00,
  PostgreSQL `24:00:00`, and a decimal beyond `decimal`'s 28 to 29 significant digits.
- SQL Server takes SQL logins only. A server in Windows-authentication mode needs mixed mode.

| Failure | `ConnectorErrorKind` |
|---|---|
| A missing or invalid setting, a refused login, an unknown database, a denied permission | `ConfigInvalid` |
| An unreachable server, a broken connection, a deadlock, a lock or serialization timeout | `Transient` |
| A value the contract cannot carry; PostgreSQL class 22; SQL Server overflow and conversion errors | `SourceError` |
| Anything else | Not classified; the driver's `DbException` propagates |
