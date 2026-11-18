# Charles Schwab

[Documentation](../README.md) › [Connectors](README.md) › Charles Schwab

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
  key and secret. Schwab ends a sign-in after 7 days; the refresh then throws `AuthExpired`. Its `SignInLife` is a
  fixed clock of 7 days, warned a day ahead.
- `IdentifyAsync` names each account linked to the sign-in, from `GET /accounts/accountNumbers`: by a SHA-256 hash of
  its account number, and labeled with its last four digits, as `…1234`.
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

## Registering the app

Each workspace signs in through its own Schwab app, set under Sign-in apps on the workspace page
([the service](../service.md)). Register the app on Schwab's developer portal with the callback that section shows,
`https://127.0.0.1:6164/oauth/callback` locally, as its own entry and with no trailing slash. Any other callback, a
placeholder included, makes Schwab's sign-in page fail with "We are unable to complete your request." Changing the
callback sends the app back for approval, which can take days. Sign-in works only while the app shows
**Ready For Use**.
