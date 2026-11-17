# Nexus

[![Build and test](https://github.com/PluperfectData/Nexus/actions/workflows/ci.yml/badge.svg)](https://github.com/PluperfectData/Nexus/actions/workflows/ci.yml)
[![Client](https://github.com/PluperfectData/Nexus/actions/workflows/client.yml/badge.svg)](https://github.com/PluperfectData/Nexus/actions/workflows/client.yml)

Source and destination connectors for .NET 10. A source reads streams of records; a destination writes
them; an action changes something on a service, such as placing an order. A source runs standalone with
records in memory, or piped into a destination.

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
| `Server/tools/Nexus.Schwab.SignIn` | A console tool that signs in to Schwab and saves the tokens to user secrets |
| `Server/tests/Nexus.Abstractions.Tests` | NUnit and Shouldly |
| `Server/tests/Nexus.Csv.Tests` | NUnit and Shouldly, against in-memory files |
| `Server/tests/Nexus.DuckDb.Tests` | NUnit and Shouldly, against a temp DuckDB file |
| `Server/tests/Nexus.Push.Tests` | NUnit and Shouldly, against a fake that keeps Scry's push API contract |
| `Server/tests/Nexus.Schwab.Tests` | NUnit and Shouldly, against canned Schwab responses |
| `Server/tests/Nexus.Service.Tests` | NUnit and Shouldly, hosting the service over a SQLite file with a fake source and push API |
| `Server/tests/Nexus.Sql.Tests` | NUnit and Shouldly, against real MySQL, PostgreSQL, and SQL Server servers |

## Connector matrix

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

## Contract

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

### Actions

An action is a typed method on the connector's own class, such as `SchwabOrders.PlaceAsync`. There is no
shared action interface.

- It returns `ActionOutcome`, or `ActionOutcome<T>` with the service's `Value`. Persist
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

## Push destination

`PushDestination`, id `push`, writes each stream to a table of the same name through an app's push API, such
as Scry's. Settings: `url`, the push API's address, such as `http://localhost:6161/ingest`; `key`, a push
API key, which is secret; and `origin`, optional, which says where the rows come from and goes with each
table's columns when it is made or replaced. `IdentifyAsync` calls `GET {url}`, which names the key, and answers
its name and prefix. `CheckAsync` does the same and answers whether it worked.

| Write mode | Requests |
|---|---|
| `Append` | `PUT {url}/sources/{table}` with the columns and key, then batches with `mode: append` |
| `Upsert` | The same `PUT`, then batches with `mode: merge`. Requires `PrimaryKey`. |
| `Overwrite` | `POST {url}/sources/{table}/replace` with the columns, after a `PUT` when the table is new, then batches. The app swaps the replacement in on `POST .../commit` when the write ends, and the stream's checkpoints are yielded after it. |

- Before each other checkpoint, every row waiting is sent and the stream is committed.
- A batch holds at most 5,000 rows and 8 MB. Each carries an id unique to the write, so the app skips one
  sent twice.
- Values go in the forms the push API reads: a decimal as text, a timestamp with its offset, a date-time
  with none, a date as `yyyy-MM-dd`, JSON as itself, and bytes as base64. A double that is not finite, or JSON
  text that does not parse, is a `SourceError`.

| Failure | `ConnectorErrorKind` |
|---|---|
| An address that is not http or https, or no key | `ConfigInvalid` |
| 401, 403, and a 404 outside a replace | `ConfigInvalid` |
| 409, a column that changed or went | `SourceError`, saying a full refresh replaces the table |
| 429, after `HttpRetry`'s tries | `RateLimited` |
| 5xx, a timeout, or no answer, after `HttpRetry`'s tries | `Transient` |
| Any other answer | `SourceError`, carrying the app's detail |

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

`SchwabSource` and `SchwabOrders`, id `schwab`, auth `OAuth`, through the Schwab Trader and Market Data APIs. Each
takes an `HttpClient` and an optional `TimeProvider`. Both read their settings from the same `ConnectorConfig`; the
spec is `SchwabSource.Spec`.

| Key | Kind | Notes |
|---|---|---|
| `startDate` | Text | Required. `yyyy-MM-dd`. Where the first transactions and candles read start. |
| `symbols` | Text | Comma-separated symbols that quotes and price history read beside the account's holdings, such as `AAPL,SPY`. |
| `candles` | Choice | `1 minute`, `5 minutes`, `10 minutes`, `15 minutes`, `30 minutes`, `Daily`, `Weekly`, or `Monthly`. Default `Daily`. |
| `movers` | Text | Comma-separated indexes that movers are read for, such as `$SPX,$DJI`. Letters, digits, `$`, and `_` only. |
| `allowOrders` | Boolean | Default `false`. While off, every `SchwabOrders` call throws `ConfigInvalid`. |

- Sign-in: authorize at `https://api.schwabapi.com/v1/oauth/authorize`, tokens from
  `https://api.schwabapi.com/v1/oauth/token`, no scopes. The host runs the redirect and passes the app key
  and secret as `ConnectorConfig.Client`. `SchwabSignIn.AuthorizeUrl` builds the sign-in page's address for a
  callback and a `state`, and `SchwabSignIn.ExchangeAsync` trades the redirect's code for `OAuthTokens`, sending it
  once. A refused code throws `AuthExpired`, and a refused app key `ConfigInvalid`.
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
| `price_history` | Full refresh, Incremental | `symbol`, `candle`, `time` | `time`, per symbol | `GET /pricehistory`, one per symbol |
| `quotes` | Incremental | `read_at`, `symbol` | `read_at` | `GET /quotes?fields=quote,reference`, 100 symbols a request |
| `movers` | Incremental | `read_at`, `index_symbol`, `sort`, `symbol` | `read_at` | `GET /movers/{index}`, once per sort |

- Trader paths are under `https://api.schwabapi.com/trader/v1`, and market data paths under
  `https://api.schwabapi.com/marketdata/v1`.
- `balances` and `positions` are snapshots. Each read adds one row per account or holding, stamped
  `read_at`. Schwab keeps no history of either.
- `transactions` starts at the saved cursor, or at `startDate`, and walks to now in 365-day windows, with a
  checkpoint after each. A full refresh starts at `startDate` and ends with `{}`.
- `orders` re-reads the last 60 days, less a minute, on every read and ends with `{}`. Write with `Upsert`;
  orders older than the window keep their last values.
- A request returning 3,000 results is split in half and both halves are read. Rows repeated across
  requests are dropped.
- Two positions in one symbol in one account throw `SourceError`.
- `price_history` and `quotes` cover `symbols` and every symbol an account holds, read from the same
  `GET /accounts?fields=positions` call. `price_history` leaves held options out; `quotes` keeps them.
- `price_history` starts each symbol at its newest saved candle, or at `startDate`, and reads to now with regular
  hours only. The newest candle is read again, so write with `Upsert`. A symbol's checkpoint follows its candles.
  The saved state names the candle size, and a different size starts every symbol over. A full refresh starts
  every symbol at `startDate` and ends with `{}`.
- `quotes` and `movers` are snapshots stamped `read_at`, like `balances`. A symbol Schwab returns under
  `errors.invalidSymbols` is skipped with a warning `LogMessage`.
- `movers` reads each index sorted by `VOLUME`, `TRADES`, `PERCENT_CHANGE_UP`, and `PERCENT_CHANGE_DOWN`. `rank` is
  the symbol's place in Schwab's list, from 1. A symbol listed twice keeps its first place.

| Stream | Columns |
|---|---|
| `balances` | `read_at`, `account_number`, `account_type`, `liquidation_value`, `cash_balance`, `total_cash`, `cash_available_for_trading`, `cash_available_for_withdrawal`, `unsettled_cash`, `equity`, `buying_power`, `available_funds`, `margin_balance`, `maintenance_requirement`, `current_balances` |
| `positions` | `read_at`, `account_number`, `symbol`, `cusip`, `asset_type`, `description`, `long_quantity`, `short_quantity`, `average_price`, `market_value`, `current_day_profit_loss`, `long_open_profit_loss`, `short_open_profit_loss` |
| `transactions` | `activity_id`, `account_number`, `time`, `trade_date`, `settlement_date`, `type`, `status`, `sub_account`, `activity_type`, `description`, `order_id`, `position_id`, `net_amount`, `symbol`, `asset_type`, `quantity`, `price`, `transfer_items` |
| `orders` | `order_id`, `account_number`, `entered_time`, `close_time`, `status`, `status_description`, `order_type`, `order_strategy_type`, `complex_order_strategy_type`, `session`, `duration`, `quantity`, `filled_quantity`, `remaining_quantity`, `price`, `stop_price`, `symbol`, `instruction`, `cancelable`, `editable`, `legs`, `child_orders`, `activity` |
| `price_history` | `symbol`, `candle`, `time`, `open`, `high`, `low`, `close`, `volume` |
| `quotes` | `read_at`, `symbol`, `asset_type`, `description`, `exchange`, `last_price`, `bid_price`, `ask_price`, `bid_size`, `ask_size`, `open_price`, `high_price`, `low_price`, `close_price`, `net_change`, `net_percent_change`, `mark`, `total_volume`, `week_52_high`, `week_52_low`, `quote_time`, `trade_time`, `quote` |
| `movers` | `read_at`, `index_symbol`, `sort`, `rank`, `symbol`, `description`, `last_price`, `net_change`, `net_percent_change`, `volume`, `total_volume`, `trades`, `market_share`, `direction` |

- Amounts, prices, and quantities are `Decimal(28, 8)`, rounded to 8 places. Times are `Timestamp`.
- `current_balances`, `transfer_items`, `legs`, `child_orders`, `activity`, and `quote` are Schwab's JSON.
- `candle` is the candle size's label, such as `Daily`. Market data times, sent as epoch milliseconds, are read as
  `Timestamp`.
- A mover's `last_price` is read from `lastPrice` or `last`, and its `net_change` from `netChange` or `change`.
- `liquidation_value` comes from `currentBalances`, or from `aggregatedBalance` when that has none.
- A transaction's `symbol`, `asset_type`, `quantity`, and `price` come from its first transfer item that is
  not currency. An order's `symbol` and `instruction` come from its leg, when it has exactly one.

| `SchwabOrders` method | Request | Returns |
|---|---|---|
| `PlaceAsync(config, accountNumber, order)` | `POST /accounts/{hash}/orders` | `ActionOutcome<long?>`, the new order's id |
| `ReplaceAsync(config, accountNumber, orderId, order)` | `PUT /accounts/{hash}/orders/{orderId}` | `ActionOutcome<long?>`, the replacement's id |
| `CancelAsync(config, accountNumber, orderId)` | `DELETE /accounts/{hash}/orders/{orderId}` | `ActionOutcome` |

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

`Server/tools/Nexus.Schwab.SignIn` signs in to Schwab in the browser and saves the tokens to its user secrets, id
`Nexus.Schwab.SignIn`. It is not packed.

First, register the app on Schwab's developer portal with the callback URL the tool listens on:
`https://127.0.0.1` with a port, such as `https://127.0.0.1:8182`, as its own entry and with no trailing slash.
Any other callback, a placeholder included, makes Schwab's sign-in page fail with "We are unable to complete
your request." Changing the callback sends the app back for approval, which can take days. Sign-in works only
while the app shows **Ready For Use**.

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
dotnet run --project Server/tools/Nexus.Schwab.SignIn
```

- The tool listens on 127.0.0.1 at the callback URL's port, with a self-signed certificate made at run time,
  and opens Schwab's sign-in page. The browser warns about the certificate; continue past it.
- It checks the returned `state`, exchanges the code with HTTP Basic auth, and writes the four token keys.
  Other keys in the file are kept, and the file is replaced in one move.
- It waits 5 minutes for the redirect, and never prints a token.

With `read`, the tool runs the connector against Schwab with the saved sign-in. It reads every stream into a
DuckDB file, prints each stream's row count, and prints any warning the read sends. It only reads; orders stay off.

```bash
dotnet run --project Server/tools/Nexus.Schwab.SignIn -- read --symbols 'AAPL,SPY' --movers '$SPX'
```

- `--from yyyy-MM-dd` sets where transactions and candles start, a year back by default. `--out path.duckdb` sets
  the file, `%LOCALAPPDATA%\Nexus\schwab.duckdb` by default.
- `--symbols`, `--candles`, and `--movers` set the settings of the same names. Without `--symbols`, quotes and
  candles cover only the account's holdings; without `--movers`, no movers are read. In PowerShell, quote an index
  in single quotes, since `"$SPX"` reads a variable.
- Balances, positions, quotes, and movers are appended, a snapshot each run; transactions, orders, and candles are
  upserted by their keys, so a run can be repeated into the same file.
- Tokens Schwab refreshes during the read are saved back to user secrets the same way the sign-in saves them.

## Service

`Server/src/Nexus.Service` keeps users, workspaces, apps, their destinations, connections, and syncs in a MySQL
database of its own, and runs each sync by reading its stream through a source connector and writing it
through `PushDestination`. It reads through the MySQL, PostgreSQL, SQL Server, and CSV sources. `Client/` holds its
pages.

- A connection whose connector signs in, such as Schwab, is made by signing in. The sign-in starts from the
  workspace page (`POST /workspaces/{id}/sign-ins` with `{name, connector, settings}`), from a connect page
  (`POST /connect/sign-in` with `{name, settings}`), or to renew a connection (`POST /connections/{id}/sign-in`),
  and each answers the provider's sign-in page. The provider sends the browser back to `GET /oauth/callback`,
  which keeps the tokens, encrypted, and sends it on: to the connect page to pick streams, to the workspace page
  with `?signedIn=`, or with `?error=` and the reason. Schwab's setting that allows orders is never offered,
  since the service only reads.
- Each workspace signs in through its own app at the provider, such as its Schwab app, which its members set under
  Sign-in apps on the workspace page, beside the callback to register on it. The app's secret is encrypted and
  never shown again. A connector whose app is not set says so wherever it is offered, in place of signing in, and a
  sync of it fails and says so.
- A sign-in under way is kept in the database for 10 minutes, by a hash of the `state` the provider's redirect
  returns, so a restart of the service loses none. Each is used once.
- A connection whose connector reads a file, such as CSV, is made from a `multipart/form-data` upload: `name`,
  `connector`, `settings` as a JSON object of text, and `file`, up to 1 GB. The file is kept in `data/files` as the
  connection's own copy, kept only once the connector reads it, and deleted with the connection.

- A worker runs queued syncs one at a time. A full refresh overwrites its table. An incremental sync upserts
  by the stream's primary key, or appends when there is none, and resumes from the last checkpoint the
  destination committed.
- A `Transient` or `RateLimited` failure runs again after 1, 5, and 15 minutes, or after a longer wait the
  provider asked for, then stops. An `AuthExpired` failure marks the connection as needing a sign-in.
- A sync that was queued or running when the service stopped runs again when it starts.
- Each run tells the push API where the table's rows come from: the workspace, the connection, its connector,
  and the stream.
- Removing a destination, or the app it belongs to, leaves its syncs in place without one. Such a sync does not
  run until someone picks another destination for it, and a run already queued fails and says so.
- Connection settings, tokens, push keys, and mailed links are protected with ASP.NET Data Protection. Its
  keys live in `data/keys`, beside the service, outside the database they protect.

### Users and sign-in

There is no sign-up. An admin invites each user by mail, and makes and removes workspaces, their members,
and apps. A member sees their own workspaces and changes their connections and syncs.

The first admin comes from the installer, below.

- A user signs in with a password, or with a link mailed to them that works once, for 15 minutes. An invite
  link works once, for 7 days. Mail goes through `Pluperfect.Mail`'s Postmark transport, with the token set on the
  Settings page. A Debug build running as Development writes `.eml` files to `data/mail` instead, until a Postmark
  token and sender are saved to test real mail; turning Postmark off returns it to the files. A released service never
  writes mail to files. An instance without mail offers no mailed links, and an invite answers its link for the admin
  to pass on.
- The session is `Pluperfect.Jwt`'s: an access token in an `HttpOnly` cookie, a refresh token that rotates,
  and the origin and antiforgery checks on every unsafe request. Every route needs a session but `/health`
  and the sign-in routes under `/auth`.
- Five wrong passwords for one address lock it for 15 minutes, and one address gets at most three mailed
  links in that time.
- Setting a password ends the user's other sessions. A forgotten password is replaced by signing in with a
  mailed link and setting a new one.

| Route | Does |
|---|---|
| `GET /install`, `POST /install/database`, `POST /install/address`, `PUT` and `DELETE /install/mail`, `POST /install/mail/test`, `POST /install/admin`, `POST /install/code` | The installer, below. Its mail routes answer 409 once the instance is set up, and its test mail goes to `{"to"}`. |
| `/settings`, `/settings/addresses`, `/settings/mail`, `POST /settings/mail/test`, `/settings/database`, `PUT /settings/log` | Admins see and change the instance's addresses, mail, database, and how many days the log keeps an entry, and send a test mail |
| `GET /log?workspace=&connection=&sync=&level=&before=&instance=` | The Log page's entries, below: newest first, 100 at a time, with `next` for the older ones. `level=Warning` keeps warnings and errors, and an admin's `instance=true` keeps the entries about the whole instance. |
| `POST /auth/sign-in`, `POST /auth/link`, `POST /auth/link/redeem` | Signs in with a password, mails a link, or signs in with one |
| `GET /auth/me`, `GET /auth/csrf`, `POST /auth/refresh`, `POST /auth/sign-out`, `PUT /auth/password` | The session and the signed-in user's password |
| `/users` | Admins invite, change, and remove users. An invite that is not mailed answers its link. |
| `/workspaces`, `/workspaces/{id}/members/{userId}` | Workspaces and their members |
| `/workspaces/{id}/apps`, `/apps/{id}`, `/apps/{id}/key`, `/apps/{id}/destinations`, `/destinations/{id}` | Apps, their keys, and their destinations. A key shows once, when it is made, and an app lists when its key was made. A destination is kept only once its key works, and records the push key's name and prefix. |
| `POST /destinations/{id}/check` | Asks the push API whether it still takes the destination's key, and records the key's name and prefix, or why it was refused |
| `GET /connectors` | The connectors and the settings each asks for |
| `GET /workspaces/{id}/sign-in-apps`, `PUT` and `DELETE /workspaces/{id}/sign-in-apps/{connector}` | The workspace's app for each provider it signs in to, from `{clientId, clientSecret}`: whether it is set, the last four characters of its key, the callback to register, and why the workspace cannot sign in yet. A blank key or secret keeps the one stored. |
| `/workspaces/{id}/connections`, `/connections/{id}`, `GET /connections/{id}/streams` | Connections, kept only once their settings connect, and their streams. A connection that reads a file is made from a form with the file. |
| `PUT /connections/{id}/file` | Replaces the file a connection reads, from a form with `file`, once the connector reads it, and queues a run of each of its syncs |
| `/workspaces/{id}/syncs`, `POST /connections/{id}/syncs`, `POST /syncs/{id}/run`, `GET /syncs/{id}/runs`, `DELETE /syncs/{id}` | Syncs, their runs, and running one now |
| `PATCH /syncs/{id}` | Changes a sync's destination, table name, and read mode from `{destinationId, table, mode, cursorField}`, and queues a run that reads from the start. The table it wrote before stays. |
| `GET /pairings/{id}`, `POST /pairings/{id}/approve`, `POST /pairings/{id}/decline` | Admins see a pairing and approve it into a workspace, or decline it |

### Apps and connect pages

An app, such as Scry, links to Nexus by pairing, with no key copied by hand:

1. The app's server calls `POST /pairings` with its name, its push API's address, the callback for the admin's
   return, its connect pages' return address, and the SHA-256 hash of a verifier it keeps. Nexus answers the
   approval page's address, `/pair?id=...` on the client.
2. An admin signs in there, picks a workspace, and approves. Nexus sends them to the callback with `?pairing=<id>`,
   or with `?error=declined`.
3. The app's server calls `POST /pairings/{id}/claim` with the verifier and a push key for its push API. Nexus
   checks the key, makes the app and its destination, and answers the app's key, once.

A pairing lasts 10 minutes and is claimed once. `POST /pairings` and the claim take no session, since only
the app's server calls them. An admin can still make an app by hand on its workspace's page.

The app sends its key as `Authorization: Bearer nexus_...`. A key reaches only the routes below, and only the
syncs into its own app's destinations.

| Route | Does |
|---|---|
| `GET /app` | The app itself: its name, its workspace, its destinations with their ids, and its return addresses |
| `DELETE /app/key` | Revokes the app's own key, as an app does when it unlinks |
| `GET /connectors` | The connectors and the settings each asks for, each with `notReady`: why the app's workspace cannot connect it yet, such as a sign-in app it has not set |
| `POST /connect-sessions` | From `{"connector", "destinationId", "returnUrl"}`, opens a connect page and answers its address. The destination must be the app's, and the return address one an admin added to the app. With `connectionId`, a connection in the app's workspace that signs in, the page signs it in again and returns with `?connection=`. |
| `GET /syncs` | The app's syncs, each with how its last run went and whether its connection needs a new sign-in |
| `POST /syncs/{id}/run`, `DELETE /syncs/{id}` | Runs one of the app's syncs now, or removes it |

A connect page is for one person, who needs no user in the instance. It lasts 10 minutes, makes one connection
in the app's workspace, adds the syncs the person picks into the app's destination, and sends them back to the
return address with `?connection=<id>`, or `?error=cancelled` when they cancel. The page's address carries its
token after the `#`, and the page sends it as `Authorization: Bearer nxc_...` to `GET /connect`,
`POST /connect/connection`, `GET /connect/streams`, `POST /connect/syncs`, and `POST /connect/cancel`.
`POST /connect/connection` takes a form with the file, as above, for a connector that reads one.

A request that carries an `Authorization` header skips the antiforgery check, since another site's page cannot
send one without a CORS preflight, which the service answers for its client's origin alone.

### Logs

The service keeps two logs.

**The Log page**, `/log` on the client, lists what happened, newest first, each in a sentence that names its
workspace, connection, or sync:

- each run, with the rows it sent or its error, and when it tries again
- a connector's own warnings, and a connection that needs a new sign-in
- each change someone made, with who made it: an email, or `app` and the app's name

An admin sees every entry, and a member sees their own workspaces'. Entries about users, settings, and the
installer belong to the whole instance, so only admins see them. Each connection and table on the workspace page
links to its own entries. A removed workspace's entries stay, as entries about the whole instance.

Entries are kept in the database for 30 days, a number an admin changes on the Settings page, from 1 to 365. Signing
in or out, reading anything, and the install code write no entry.

**The developer log** goes to the console and to a file a day in `data/logs`, `nexus-<date>.log`, keeping 14. It
has every Log page entry, under `Nexus.Operator`, one line for each request, and the framework's detail and the
stack traces the Log page leaves out. Its levels come from `Serilog:MinimumLevel`: Information, with
`Microsoft.AspNetCore` and `Microsoft.EntityFrameworkCore` at Warning. For more detail, set
`Serilog__MinimumLevel__Default` to `Debug`.

### Running it

The service runs on `http://localhost:6163`, and on `https://127.0.0.1:6164` for sign-in callbacks. Its client runs
on `http://localhost:6165`. Start both, then open `http://localhost:6165/install`.

```bash
dotnet run --project Server/src/Nexus.Service
```

```bash
npm install --prefix Client
```

```bash
npm run dev --prefix Client
```

The client takes `@pluperfect/jwt` from a sibling checkout of `Pluperfect.Jwt` beside `Pluperfect.Data`, and
the service takes `Pluperfect.Jwt` and `Pluperfect.Mail` the same way. Build `Pluperfect.Jwt/client` once with
`npm run build` before installing the client.

### The installer

An instance starts with nothing set. Until it has a database, a client address, and an admin, every route but
`/install` and `/health` answers 503, and the client sends its visitors to `/install`. The installer asks, in turn:

1. **The database:** a MySQL 8.4 server's host, port, database, user, and password. Nexus makes the database if it
   is missing and the user may, applies its migrations, and keeps the settings only once that works. A database
   that already has an admin skips step 4, which is how an existing instance is adopted.
2. **The client's address:** where people open Nexus. CORS and the origin check allow it alone, and mailed links
   and connect pages point there.
3. **Mail:** Postmark's server token and sender, with a test mail to an address you name. Mail may stay off.
   A Debug build running as Development writes mail to `data/mail`, and the step says so.
4. **The first admin:** an email, signed in at once. Once mail works the password is optional, and an admin without
   one signs in with mailed links. With mail off, a password is required.
5. **The callback address:** the https address providers send people back to after signing in, at `/oauth/callback`.

Admins change all of it later on the Settings page. A new database is opened and brought up to date before
Nexus moves to it, and a database without an admin sends the instance back to step 3.

A page on the machine the service runs on uses the installer as it is. Any other page needs the install code,
which the service writes to its log, with a link to `/install` that carries it, at each start while it is not set
up. The code is sent as `Authorization: Bearer nxi_...`, and five wrong codes lock the installer for 15 minutes. A
page counts as on the machine when it reaches the service over loopback with no proxy's forwarding headers, at a
loopback name, from a page at a loopback name; a box behind Caddy's Unix socket always needs the code.

Someone locked out uses the installer's Make an admin, which makes an address an admin with a new password and
signs it out everywhere else. On another machine it asks the service to write a code to its log, good for an hour.
A database that stops opening at a start sends the instance back to step 1, with the stored settings filled in.

The settings are kept in `data/instance.json`, with the database password, the Postmark token, and the session
signing key encrypted by the Data Protection keys in `data/keys`. The service makes the signing key on its first
start. On a box, `data/` must persist across releases.

A Debug build has one more command, `reset`, which sets `data/instance.json` aside under a dated name, so the next
start opens the installer at its first step. The database, `data/keys`, and `data/files` stay: entering the same
database in the installer adopts it again, and a new name starts empty. It refuses outside Development, and a
Release build has no such command. Stop the service before running it.

```bash
dotnet run --project Server/src/Nexus.Service -- reset
```

### Configuration

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

`add-admin` makes or restores an admin, prints a link that signs them in once, within an hour, and exits.

```bash
dotnet run --project Server/src/Nexus.Service -- add-admin you@example.com
```

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

## License

AGPL-3.0-only. See `LICENSE`.
