# Nexus

[![Build and test](https://github.com/PluperfectData/Nexus/actions/workflows/ci.yml/badge.svg)](https://github.com/PluperfectData/Nexus/actions/workflows/ci.yml)

Source and destination connectors for .NET 10. A source reads streams of records; a destination writes
them. A source runs standalone with records in memory, or piped into a destination.

| Path | Contents |
|---|---|
| `src/Nexus.Abstractions` | Contracts, messages, errors, `HttpRetry`, `ReadToMemoryAsync`, `Tap` |
| `src/Nexus.Csv` | `CsvSource` |
| `src/Nexus.DuckDb` | `DuckDbDestination` |
| `src/Nexus.Sql` | `SqlSettings` and the reader shared by the SQL sources |
| `src/Nexus.MySql` | `MySqlSource` |
| `src/Nexus.Postgres` | `PostgresSource` |
| `src/Nexus.SqlServer` | `SqlServerSource` |
| `tests/Nexus.Abstractions.Tests` | NUnit and Shouldly |
| `tests/Nexus.Csv.Tests` | NUnit and Shouldly, against in-memory files |
| `tests/Nexus.DuckDb.Tests` | NUnit and Shouldly, against a temp DuckDB file |
| `tests/Nexus.Sql.Tests` | NUnit and Shouldly, against real MySQL, PostgreSQL, and SQL Server servers |

## Connector matrix

| Connector | Project | Source | Destination |
|---|---|---|---|
| CSV | `Nexus.Csv` | ✓ | |
| DuckDB | `Nexus.DuckDb` | | ✓ |
| MySQL | `Nexus.MySql` | ✓ | |
| PostgreSQL | `Nexus.Postgres` | ✓ | |
| SQL Server | `Nexus.SqlServer` | ✓ | |

Planned sources: Google Sheets, Stripe, and Schwab.

## Contract

| Interface | Members |
|---|---|
| `ISourceConnector` | `Spec`, `CheckAsync`, `DiscoverAsync`, `ReadAsync` |
| `IDestinationConnector` | `Spec`, `CheckAsync`, `WriteAsync` |

`ReadAsync` yields:

| Message | Contents |
|---|---|
| `SchemaMessage` | Columns, primary key, and cursor fields. Precedes the stream's records. |
| `RecordBatch` | Rows as `object?[]` in column order. Arrays are not reused after yield. |
| `StateCheckpoint` | Per-stream JSON. Persist it; pass it back in `ReadRequest.State` to resume. |
| `CredentialsRefreshed` | Rotated OAuth tokens. Persist them. |
| `LogMessage` | Connector log line. |

- Sync modes: `FullRefresh`; `Incremental`, past a cursor field. Incremental reads miss hard deletes.
- Write modes: `Overwrite`, `Append`, `Upsert` by primary key.
- `WriteAsync` yields a `StateCheckpoint` only after every preceding record is committed. Persist only
  those.

### Column types

| `ColumnType` | CLR type |
|---|---|
| `String` | `string` |
| `Int64` | `long` |
| `Decimal` | `decimal`, with `Precision` and `Scale` |
| `Float64` | `double` |
| `Boolean` | `bool` |
| `Date` | `DateOnly` |
| `Timestamp` | `DateTimeOffset`, UTC |
| `DateTime` | `DateTime`, no zone, `DateTimeKind.Unspecified` |
| `Time` | `TimeOnly` |
| `Json` | `string` |
| `Binary` | `byte[]` |

### Errors

`ConnectorException.Kind`. `Message` is safe to display.

| Kind | Host action |
|---|---|
| `ConfigInvalid` | User corrects settings |
| `AuthExpired` | User re-authenticates |
| `RateLimited` | Retry after `RetryAfter` |
| `Transient` | Retry later |
| `SourceError` | None; the source returned unusable data |

### Secrets

The library persists nothing. The host passes `ConnectorConfig` (settings, secrets, OAuth tokens, and
uploaded files) on every call and stores state and tokens itself. `ToString()` on `ConnectorConfig` and
`OAuthTokens` omits secret values.

### Files

A `SettingKind.File` field takes an upload. The host passes it in `ConnectorConfig.Files`, keyed by the
field's name, as a `ConnectorFile`: the uploaded name, and `OpenAsync`, which opens a new stream at the start
of the file on every call. The caller disposes each stream.

### HTTP

`HttpRetry` retries network failures, 429, and 5xx.

| Option | Default |
|---|---|
| `MaxAttempts` | 5 |
| `BaseDelay` | 1 s, doubling per attempt, 50 to 100 percent jitter |
| `MaxDelay` | 30 s |

`Retry-After` is honored. A value above `MaxDelay` throws at once with `RetryAfter` set. The final failure
throws `RateLimited` for 429 and `Transient` otherwise.

## DuckDB destination

`DuckDbDestination`, id `duckdb`. Setting `path`: the database file, created if absent.

| Write mode | Behavior |
|---|---|
| `Append` | Inserts every row. |
| `Upsert` | Deletes target rows whose primary key is staged, then inserts the last staged row per key. Requires `PrimaryKey`. |
| `Overwrite` | Loads `__nexus_overwrite_<stream>`, then replaces `<stream>` in one transaction when the write ends. The stream's checkpoints are yielded after the replace. |

- One table per stream, named for the stream. A column the source adds is added with
  `ALTER TABLE ADD COLUMN`; none are dropped. No `NOT NULL` or key constraints.
- Rows are appended to a temp stage table per stream and merged into the target by column name at each
  checkpoint, in one transaction. A failed write rolls back to the last checkpoint.
- Tables prefixed `__nexus_` are internal.

| Failure | `ConnectorErrorKind` |
|---|---|
| Open: another process holds the file | `Transient` |
| Open: any other failure | `ConfigInvalid` |
| `DuckDBErrorType` `Conversion`, `OutOfRange`, `Decimal`, `MismatchType`, `Constraint`, `InvalidInput`, `ObjectSize`; a value overflowing its column | `SourceError` |
| `DuckDBErrorType` `Transaction`, `OutOfMemory`, `Io`, `Interrupt` | `Transient` |
| `DuckDBErrorType` `Permission` | `ConfigInvalid` |
| Any other `DuckDBErrorType` (`Binder`, `Catalog`, `Parser`, `Internal`, ...) | Not classified; `DuckDBException` propagates |

The original exception is `InnerException`.

| `ColumnType` | DuckDB type |
|---|---|
| `String` | `VARCHAR` |
| `Int64` | `BIGINT` |
| `Decimal` | `DECIMAL(p, s)`; precision 0 becomes 38 |
| `Float64` | `DOUBLE` |
| `Boolean` | `BOOLEAN` |
| `Date` | `DATE` |
| `Timestamp` | `TIMESTAMPTZ` |
| `DateTime` | `TIMESTAMP` |
| `Time` | `TIME` |
| `Json` | `JSON` |
| `Binary` | `BLOB` |

## CSV source

`CsvSource`, id `csv`, auth `FileUpload`, on Sylvan.Data.Csv. One file, one stream.

| Key | Kind | Notes |
|---|---|---|
| `file` | File | Required. Passed in `ConnectorConfig.Files`. |
| `stream` | Text | Required. The stream's name. |
| `header` | Boolean | Default `true`. Without a header, columns are `column1` to `columnN`. |
| `delimiter` | Choice | `Comma` (default), `Semicolon`, `Tab`, `Pipe`. |

- UTF-8, unless a byte order mark names another encoding.
- Discovery reads the file once. A read passes over it twice: once to infer types, once to yield rows.
- Full refresh only, in batches of 10,000 rows, then a `StateCheckpoint` of `{}`. No primary key, so write
  with `Overwrite` or `Append`.
- Empty fields are null in every column. A column with an empty field is nullable; a column of only empty
  fields is a nullable `String`. Blank lines are skipped.

Each column takes the first type every non-empty value fits. Formats are invariant: no thousands separators,
no locale dates.

| `ColumnType` | Accepted text |
|---|---|
| `Boolean` | `true`, `false`, in any case |
| `Int64` | An optional sign and digits |
| `Decimal(p, s)` | An optional sign, digits, and `.`, at most 28 digits per value. `p` and `s` are the widest seen; `p` is at most 38. |
| `Float64` | A number with an exponent, or one too long for `Decimal` |
| `Date` | `yyyy-MM-dd` |
| `Timestamp` | `yyyy-MM-ddTHH:mm:ss[.fffffff]` and `Z` or `±hh:mm`. A space may replace the `T`. |
| `DateTime` | `yyyy-MM-ddTHH:mm[:ss[.fffffff]]`, no zone. A space may replace the `T`. |
| `String` | Anything else |

| Failure | `ConnectorErrorKind` |
|---|---|
| No file, no `stream`, an invalid `header` or `delimiter`, an empty file, a missing file, an incremental read | `ConfigInvalid` |
| Any other `IOException` reading the file | `Transient` |
| A row with the wrong field count, malformed CSV, a repeated column name, a value that changed between passes | `SourceError` |

## SQL sources

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

## Usage

Standalone, in memory:

```csharp
var result = await source.ReadToMemoryAsync(
    config,
    new ReadRequest([new StreamSelection("orders", SyncMode.FullRefresh)]),
    logger);

MemoryTable orders = result.Tables["orders"];
// orders.Rows, orders.State, result.RefreshedCredentials
```

Source to destination, where `tokenStore` and `stateStore` are the host's:

```csharp
var messages = source.ReadAsync(sourceConfig, readRequest, ct)
    .Tap((tokens, cancellation) => tokenStore.SaveAsync(tokens, cancellation), logger, ct);

await foreach (var checkpoint in destination.WriteAsync(destinationConfig, writeRequest, messages, ct))
{
    await stateStore.SaveAsync(checkpoint, ct);
}
```

A destination consumes `CredentialsRefreshed` and `LogMessage` without acting on them. `Tap` hands each to
the host before passing it on; without it, refreshed tokens are lost.

## Build

.NET SDK 10.0.302 or a later feature band (`global.json`).

```bash
dotnet build Nexus.slnx
```

```bash
dotnet test Nexus.slnx
```

Restores are locked to `packages.lock.json`. After a dependency change, run
`dotnet restore --force-evaluate` and commit the lock files.

`tests/Nexus.Sql.Tests` needs a MySQL, a PostgreSQL, and a SQL Server server. Each fixture creates a
database named `nexus_test_<id>` and drops it afterwards, so each login needs permission to create and drop
databases. Give one connection string per server, as a user secret or an environment variable:

| User secret | Environment variable |
|---|---|
| `ConnectionStrings:MySql` | `ConnectionStrings__MySql` |
| `ConnectionStrings:Postgres` | `ConnectionStrings__Postgres` |
| `ConnectionStrings:SqlServer` | `ConnectionStrings__SqlServer` |

```bash
dotnet user-secrets set ConnectionStrings:MySql "Server=localhost;User ID=...;Password=..." --project tests/Nexus.Sql.Tests
```

CI starts all three as service containers. Their password comes from the repository secret
`CI_DATABASE_PASSWORD`: letters and digits, at least 8 characters, with upper case, lower case, and a digit.
SQL Server rejects a weaker one.

## License

AGPL-3.0-only. See `LICENSE`.
