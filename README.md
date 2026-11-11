# Nexus

[![Build and test](https://github.com/PluperfectData/Nexus/actions/workflows/ci.yml/badge.svg)](https://github.com/PluperfectData/Nexus/actions/workflows/ci.yml)

Source and destination connectors for .NET 10. A source reads streams of records; a destination writes
them. A source runs standalone with records in memory, or piped into a destination.

| Path | Contents |
|---|---|
| `src/Nexus.Abstractions` | Contracts, messages, errors, `HttpRetry`, `ReadToMemoryAsync` |
| `tests/Nexus.Abstractions.Tests` | NUnit and Shouldly |

Status: contracts only. Planned: MySQL, Postgres, SQL Server, CSV, Google Sheets, Stripe, and Schwab
sources; a DuckDB destination.

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

Source to destination, where `stateStore` is the host's:

```csharp
var messages = source.ReadAsync(sourceConfig, readRequest, ct);

await foreach (var checkpoint in destination.WriteAsync(destinationConfig, writeRequest, messages, ct))
{
    await stateStore.SaveAsync(checkpoint, ct);
}
```

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
