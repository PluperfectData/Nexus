# Nexus

[![Build and test](https://github.com/PluperfectData/Nexus/actions/workflows/ci.yml/badge.svg)](https://github.com/PluperfectData/Nexus/actions/workflows/ci.yml)

Source and destination connectors for .NET 10. A source reads streams of records; a destination writes
them; an action changes something on a service, such as placing an order. A source runs standalone with
records in memory, or piped into a destination.

| Path | Contents |
|---|---|
| `src/Nexus.Abstractions` | Contracts, messages, errors, `HttpRetry`, `ReadToMemoryAsync`, `Tap` |
| `src/Nexus.Csv` | `CsvSource` |
| `src/Nexus.DuckDb` | `DuckDbDestination` |
| `src/Nexus.Sql` | `SqlSettings` and the reader shared by the SQL sources |
| `src/Nexus.MySql` | `MySqlSource` |
| `src/Nexus.Postgres` | `PostgresSource` |
| `src/Nexus.SqlServer` | `SqlServerSource` |
| `src/Nexus.Schwab` | `SchwabSource`, `SchwabOrders`, and the order types |
| `tools/Nexus.Schwab.SignIn` | A console tool that signs in to Schwab and saves the tokens to user secrets |
| `tests/Nexus.Abstractions.Tests` | NUnit and Shouldly |
| `tests/Nexus.Csv.Tests` | NUnit and Shouldly, against in-memory files |
| `tests/Nexus.DuckDb.Tests` | NUnit and Shouldly, against a temp DuckDB file |
| `tests/Nexus.Schwab.Tests` | NUnit and Shouldly, against canned Schwab responses |
| `tests/Nexus.Sql.Tests` | NUnit and Shouldly, against real MySQL, PostgreSQL, and SQL Server servers |

## Connector matrix

| Connector | Project | Source | Destination | Actions |
|---|---|---|---|---|
| Charles Schwab | `Nexus.Schwab` | ✓ | | ✓ |
| CSV | `Nexus.Csv` | ✓ | | |
| DuckDB | `Nexus.DuckDb` | | ✓ | |
| MySQL | `Nexus.MySql` | ✓ | | |
| PostgreSQL | `Nexus.Postgres` | ✓ | | |
| SQL Server | `Nexus.SqlServer` | ✓ | | |

Planned sources: Google Sheets and Stripe.

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

### Actions

An action is a typed method on the connector's own class, such as `SchwabOrders.PlaceAsync`. There is no
shared action interface.

- It returns `ActionResult`, or `ActionResult<T>` with the service's `Value`. Persist
  `RefreshedCredentials` when it is not null.
- It is sent once and never retried. A failure after sending throws `OutcomeUnknown`.

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
| `OutcomeUnknown` | An action was sent and may have taken effect. Check before sending it again. |

### Secrets

The library persists nothing. The host passes `ConnectorConfig` (settings, secrets, OAuth tokens, uploaded
files, and its OAuth app) on every call and stores state and tokens itself. `ToString()` on
`ConnectorConfig`, `OAuthTokens`, and `OAuthClient` omits secret values.

An OAuth connector that refreshes its own tokens takes the host's app as `ConnectorConfig.Client`: an
`OAuthClient` of client id and client secret.

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

## Charles Schwab

`SchwabSource` and `SchwabOrders`, id `schwab`, auth `OAuth`, through the Schwab Trader API. Each takes an
`HttpClient` and an optional `TimeProvider`. Both read their settings from the same `ConnectorConfig`; the
spec is `SchwabSource.Spec`.

| Key | Kind | Notes |
|---|---|---|
| `startDate` | Text | Required. `yyyy-MM-dd`. Where the first transactions read starts. |
| `allowOrders` | Boolean | Default `false`. While off, every `SchwabOrders` call throws `ConfigInvalid`. |

- Sign-in: authorize at `https://api.schwabapi.com/v1/oauth/authorize`, tokens from
  `https://api.schwabapi.com/v1/oauth/token`, no scopes. The host runs the redirect and passes the app key
  and secret as `ConnectorConfig.Client`.
- The access token is refreshed with 5 minutes or less left, and on a 401, using HTTP Basic auth with the app
  key and secret. Schwab ends a sign-in after 7 days; the refresh then throws `AuthExpired`.
- Every account linked to the sign-in is read. Requests address an account by the hash from
  `GET /accounts/accountNumbers`; rows carry the plain `account_number`.

| Stream | Modes | Primary key | Cursor | Request |
|---|---|---|---|---|
| `balances` | Incremental | `read_at`, `account_number` | `read_at` | `GET /accounts?fields=positions` |
| `positions` | Incremental | `read_at`, `account_number`, `symbol` | `read_at` | The same call |
| `transactions` | Full refresh, Incremental | `activity_id` | `time` | `GET /accounts/{hash}/transactions`, every type |
| `orders` | Incremental | `order_id` | None | `GET /accounts/{hash}/orders`, `maxResults=3000` |

- `balances` and `positions` are snapshots. Each read adds one row per account or holding, stamped
  `read_at`. Schwab keeps no history of either.
- `transactions` starts at the saved cursor, or at `startDate`, and walks to now in 365-day windows, with a
  checkpoint after each. A full refresh starts at `startDate` and ends with `{}`.
- `orders` re-reads the last 60 days, less a minute, on every read and ends with `{}`. Write with `Upsert`;
  orders older than the window keep their last values.
- A request returning 3,000 results is split in half and both halves are read. Rows repeated across
  requests are dropped.
- Two positions in one symbol in one account throw `SourceError`.

| Stream | Columns |
|---|---|
| `balances` | `read_at`, `account_number`, `account_type`, `liquidation_value`, `cash_balance`, `total_cash`, `cash_available_for_trading`, `cash_available_for_withdrawal`, `unsettled_cash`, `equity`, `buying_power`, `available_funds`, `margin_balance`, `maintenance_requirement`, `current_balances` |
| `positions` | `read_at`, `account_number`, `symbol`, `cusip`, `asset_type`, `description`, `long_quantity`, `short_quantity`, `average_price`, `market_value`, `current_day_profit_loss`, `long_open_profit_loss`, `short_open_profit_loss` |
| `transactions` | `activity_id`, `account_number`, `time`, `trade_date`, `settlement_date`, `type`, `status`, `sub_account`, `activity_type`, `description`, `order_id`, `position_id`, `net_amount`, `symbol`, `asset_type`, `quantity`, `price`, `transfer_items` |
| `orders` | `order_id`, `account_number`, `entered_time`, `close_time`, `status`, `status_description`, `order_type`, `order_strategy_type`, `complex_order_strategy_type`, `session`, `duration`, `quantity`, `filled_quantity`, `remaining_quantity`, `price`, `stop_price`, `symbol`, `instruction`, `cancelable`, `editable`, `legs`, `child_orders`, `activity` |

- Amounts, prices, and quantities are `Decimal(28, 8)`, rounded to 8 places. Times are `Timestamp`.
- `current_balances`, `transfer_items`, `legs`, `child_orders`, and `activity` are Schwab's JSON.
- `liquidation_value` comes from `currentBalances`, or from `aggregatedBalance` when that has none.
- A transaction's `symbol`, `asset_type`, `quantity`, and `price` come from its first transfer item that is
  not currency. An order's `symbol` and `instruction` come from its leg, when it has exactly one.

| `SchwabOrders` method | Request | Returns |
|---|---|---|
| `PlaceAsync(config, accountNumber, order)` | `POST /accounts/{hash}/orders` | `ActionResult<long?>`, the new order's id |
| `ReplaceAsync(config, accountNumber, orderId, order)` | `PUT /accounts/{hash}/orders/{orderId}` | `ActionResult<long?>`, the replacement's id |
| `CancelAsync(config, accountNumber, orderId)` | `DELETE /accounts/{hash}/orders/{orderId}` | `ActionResult` |

The id comes from the `Location` header, and is null when Schwab sends none.

| Order | Made by |
|---|---|
| `EquityOrder` | `Market`, `Limit`, `Stop`, or `StopLimit`, with an `EquityInstruction` (`Buy`, `Sell`, `SellShort`, `BuyToCover`), a symbol, a quantity, and prices. Optional `OrderSession` (`Normal` by default, `Am`, `Pm`, `Seamless`) and `OrderDuration` (`Day` by default, `GoodTillCancel`, `FillOrKill`). |
| `OneCancelsOther` | Two `EquityOrder`s. When one fills, Schwab cancels the other. |
| `OneTriggersOther` | An `EquityOrder`, and any `SchwabOrder` sent once it fills. A bracket is a buy that triggers a `OneCancelsOther`. |

- Symbols are sent in capitals, and prices as strings.
- Option orders and trailing stops are not supported.

| Failure | `ConnectorErrorKind` |
|---|---|
| A missing or invalid setting, orders turned off, no `ConnectorConfig.Client`, an account not linked to the sign-in, a refused app key and secret, a 403, an unreadable saved state, a full refresh of a snapshot stream | `ConfigInvalid` |
| No tokens, no refresh token, a refused refresh, a 401 after a refresh | `AuthExpired` |
| A 429 | `RateLimited` |
| A read that runs out of retries, a response cut off, an action that failed DNS or TLS and so was never sent | `Transient` |
| An action sent with no answer: a 5xx, a timeout, a dropped connection | `OutcomeUnknown` |
| Any other 4xx, with Schwab's message; a missing or unreadable value | `SourceError` |

### Sign-in tool

`tools/Nexus.Schwab.SignIn` signs in to Schwab in the browser and saves the tokens to its user secrets, id
`Nexus.Schwab.SignIn`. It is not packed.

| User secret | Set by | Holds |
|---|---|---|
| `Schwab:AppKey` | You | The app key from Schwab's developer portal |
| `Schwab:AppSecret` | You | The app secret |
| `Schwab:CallbackUrl` | You | The app's callback URL, exactly as registered: `https://127.0.0.1`, with an optional port |
| `Schwab:AccessToken` | The tool | |
| `Schwab:RefreshToken` | The tool | |
| `Schwab:ExpiresAt` | The tool | When the access token expires, ISO 8601 |
| `Schwab:SignedInAt` | The tool | When the sign-in happened. Schwab ends it 7 days later. |

```bash
dotnet user-secrets set Schwab:AppKey "<app key>" --id Nexus.Schwab.SignIn
```

```bash
dotnet run --project tools/Nexus.Schwab.SignIn
```

- The tool listens on 127.0.0.1 at the callback URL's port, with a self-signed certificate made at run time,
  and opens Schwab's sign-in page. The browser warns about the certificate; continue past it.
- It checks the returned `state`, exchanges the code with HTTP Basic auth, and writes the four token keys.
  Other keys in the file are kept, and the file is replaced in one move.
- It waits 5 minutes for the redirect, and never prints a token.

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

An action:

```csharp
var placed = await new SchwabOrders(httpClient).PlaceAsync(
    config,
    "12345678",
    EquityOrder.Limit(EquityInstruction.Buy, "AAPL", 10, 150.25m));

// placed.Value is the order id; persist placed.RefreshedCredentials when it is not null.
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

`tests/Nexus.Schwab.Tests` answers every request from canned responses and needs no Schwab account.

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
