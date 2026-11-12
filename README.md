# Nexus

[![Build and test](https://github.com/PluperfectData/Nexus/actions/workflows/ci.yml/badge.svg)](https://github.com/PluperfectData/Nexus/actions/workflows/ci.yml)

Source and destination connectors for .NET 10. A source reads streams of records; a destination writes
them. A source runs standalone with records in memory, or piped into a destination.

| Path | Contents |
|---|---|
| `src/Nexus.Abstractions` | Contracts, messages, errors, `HttpRetry`, `ReadToMemoryAsync`, `Tap` |
| `src/Nexus.DuckDb` | `DuckDbDestination` |
| `tests/Nexus.Abstractions.Tests` | NUnit and Shouldly |
| `tests/Nexus.DuckDb.Tests` | NUnit and Shouldly, against a temp DuckDB file |

Status: contracts and the DuckDB destination. Planned sources: MySQL, Postgres, SQL Server, CSV, Google
Sheets, Stripe, and Schwab.

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

The library persists nothing. The host passes `ConnectorConfig` (settings, secrets, and OAuth tokens) on
every call and stores state and tokens itself. `ToString()` on `ConnectorConfig` and `OAuthTokens` omits
secret values.

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
| `Time` | `TIME` |
| `Json` | `JSON` |
| `Binary` | `BLOB` |

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

## Releases

Push a `vMAJOR.MINOR.PATCH` tag. `ci.yml` builds and tests the tag, packs each packable project at that
version, and publishes a GitHub release with the `.nupkg` files and generated notes.

```bash
git tag v0.1.0
```

```bash
git push origin v0.1.0
```

## License

AGPL-3.0-only. See `LICENSE`.
