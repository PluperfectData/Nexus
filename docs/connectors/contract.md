# The connector contract

[Documentation](../README.md) › [Connectors](README.md) › The connector contract

| Interface | Members |
|---|---|
| `ISourceConnector` | `Spec`, `CheckAsync`, `DiscoverAsync`, `ReadAsync` |
| `IDestinationConnector` | `Spec`, `CheckAsync`, `WriteAsync` |

`Spec` is a `ConnectorSpec`: `Id`, `DisplayName`, a one-sentence `Description` a host can show beside the name,
`Version`, `Auth`, the settings `Fields`, and `OAuth` endpoints when it signs in that way.

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

## Actions

An action is a typed method on the connector's own class, such as `SchwabOrders.PlaceAsync`. There is no
shared action interface.

- It returns `ActionOutcome`, or `ActionOutcome<T>` with the service's `Value`. Persist
  `RefreshedCredentials` when it is not null.
- It is sent once and never retried. A failure after sending throws `OutcomeUnknown`.

## Column types

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

## Errors

`ConnectorException.Kind`. `Message` is safe to display.

| Kind | Host action |
|---|---|
| `ConfigInvalid` | User corrects settings |
| `AuthExpired` | User re-authenticates |
| `RateLimited` | Retry after `RetryAfter` |
| `Transient` | Retry later |
| `SourceError` | None; the source returned unusable data |
| `OutcomeUnknown` | An action was sent and may have taken effect. Check before sending it again. |

## Secrets

The library persists nothing. The host passes `ConnectorConfig` (settings, secrets, OAuth tokens, uploaded
files, and its OAuth app) on every call and stores state and tokens itself. `ToString()` on
`ConnectorConfig`, `OAuthTokens`, and `OAuthClient` omits secret values.

An OAuth connector that refreshes its own tokens takes the host's app as `ConnectorConfig.Client`: an
`OAuthClient` of client id and client secret.

## Files

A `SettingKind.File` field takes an upload. The host passes it in `ConnectorConfig.Files`, keyed by the
field's name, as a `ConnectorFile`: the uploaded name, and `OpenAsync`, which opens a new stream at the start
of the file on every call. The caller disposes each stream.

## HTTP

`HttpRetry` retries network failures, 429, and 5xx.

| Option | Default |
|---|---|
| `MaxAttempts` | 5 |
| `BaseDelay` | 1 s, doubling per attempt, 50 to 100 percent jitter |
| `MaxDelay` | 30 s |

`Retry-After` is honored. A value above `MaxDelay` throws at once with `RetryAfter` set. The final failure
throws `RateLimited` for 429 and `Transient` otherwise.
