using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Schwab.Tests;

[TestFixture]
public sealed class SchwabSourceTests
{
    private const string Accounts = """
        [
          {
            "securitiesAccount": {
              "type": "MARGIN",
              "accountNumber": "11111111",
              "positions": [
                {
                  "longQuantity": 10, "shortQuantity": 0, "averagePrice": 150.123456789, "marketValue": 1600.5,
                  "currentDayProfitLoss": -2.25, "longOpenProfitLoss": 99.27,
                  "instrument": { "assetType": "EQUITY", "cusip": "037833100", "symbol": "AAPL", "description": "APPLE INC" }
                }
              ],
              "currentBalances": {
                "cashBalance": 0, "equity": 1600.5, "buyingPower": 3201, "availableFunds": 1600.5,
                "marginBalance": 0, "maintenanceRequirement": 480.15
              }
            },
            "aggregatedBalance": { "currentLiquidationValue": 1600.5, "liquidationValue": 1600.5 }
          },
          {
            "securitiesAccount": {
              "type": "CASH",
              "accountNumber": "22222222",
              "currentBalances": {
                "cashBalance": 250.75, "totalCash": 250.75, "cashAvailableForTrading": 200,
                "cashAvailableForWithdrawal": 200, "unsettledCash": 50.75, "liquidationValue": 250.75
              }
            }
          }
        ]
        """;

    private const string Trade = """
        {
          "activityId": 1001, "time": "2025-03-04T14:30:00+0000", "tradeDate": "2025-03-04T14:30:00+0000",
          "settlementDate": "2025-03-05T00:00:00+0000", "accountNumber": "11111111", "type": "TRADE", "status": "VALID",
          "subAccount": "MARGIN", "activityType": "EXECUTION", "description": "", "orderId": 5001, "positionId": 77,
          "netAmount": -1500.0,
          "transferItems": [
            { "instrument": { "assetType": "CURRENCY", "symbol": "CURRENCY_USD" }, "amount": 0, "cost": -0.01, "feeType": "SEC_FEE" },
            { "instrument": { "assetType": "EQUITY", "symbol": "AAPL" }, "amount": 10, "cost": -1500, "price": 150, "positionEffect": "OPENING" }
          ]
        }
        """;

    private const string Dividend = """
        {
          "activityId": 1002, "time": "2026-02-10T12:00:00.000+0000", "type": "DIVIDEND_OR_INTEREST", "netAmount": 3.21,
          "transferItems": [ { "instrument": { "assetType": "CURRENCY", "symbol": "CURRENCY_USD" }, "amount": 3.21 } ]
        }
        """;

    private const string TokenReply = """
        { "expires_in": 1800, "token_type": "Bearer", "scope": "api", "refresh_token": "refresh", "access_token": "fresh", "id_token": "id" }
        """;

    private const string Transactions = "/trader/v1/accounts/HASH1/transactions";

    private static readonly DateTimeOffset Now = Schwab.Now;

    [Test]
    public async Task Balances_and_positions_are_one_snapshot_stamped_with_the_read_time()
    {
        var fake = new FakeSchwab().OnJson(HttpMethod.Get, "/trader/v1/accounts", Accounts);

        var result = await Source(fake).ReadToMemoryAsync(
            Schwab.Config(),
            new ReadRequest(
            [
                new StreamSelection(SchwabSource.BalancesStream, SyncMode.Incremental),
                new StreamSelection(SchwabSource.PositionsStream, SyncMode.Incremental),
            ]));

        var balances = result.Tables[SchwabSource.BalancesStream];
        balances.Rows.Select(row => row[..^1]).ShouldBe(
        [
            [Now, "11111111", "MARGIN", 1600.5m, 0m, null, null, null, null, 1600.5m, 3201m, 1600.5m, 0m, 480.15m],
            [Now, "22222222", "CASH", 250.75m, 250.75m, 250.75m, 200m, 200m, 50.75m, null, null, null, null, null],
        ]);
        balances.Rows[1][^1].ShouldBeOfType<string>().ShouldContain("\"totalCash\"");
        balances.State!.Value.GetProperty("value").GetString().ShouldBe(Now.ToString("O", CultureInfo.InvariantCulture));

        result.Tables[SchwabSource.PositionsStream].Rows.ShouldBe(
        [
            [Now, "11111111", "AAPL", "037833100", "EQUITY", "APPLE INC", 10m, 0m, 150.12345679m, 1600.5m, -2.25m, 99.27m, null],
        ]);

        fake.Requests.ShouldHaveSingleItem().Authorization.ShouldBe("Bearer access");
        fake.Requests[0].Query["fields"].ShouldBe("positions");
    }

    [Test]
    public async Task Transactions_walk_forward_from_the_start_date_a_year_at_a_time()
    {
        var fake = new FakeSchwab()
            .OnJson(HttpMethod.Get, FakeSchwab.AccountNumbers, Schwab.TwoAccounts)
            .On(HttpMethod.Get, Transactions, request => Reply(request, "2025-01-01T00:00:00.000Z", Trade))
            .On(HttpMethod.Get, "/trader/v1/accounts/HASH2/transactions", request => Reply(request, "2026-01-01T00:00:00.000Z", Dividend));

        var table = (await Source(fake).ReadToMemoryAsync(Schwab.Config(), Incremental(SchwabSource.TransactionsStream)))
            .Tables[SchwabSource.TransactionsStream];

        fake.Requests.Skip(1).Select(r => (r.Path, r.Query["startDate"], r.Query["endDate"])).ShouldBe(
        [
            (Transactions, "2025-01-01T00:00:00.000Z", "2026-01-01T00:00:00.000Z"),
            ("/trader/v1/accounts/HASH2/transactions", "2025-01-01T00:00:00.000Z", "2026-01-01T00:00:00.000Z"),
            (Transactions, "2026-01-01T00:00:00.000Z", "2026-11-12T15:00:00.000Z"),
            ("/trader/v1/accounts/HASH2/transactions", "2026-01-01T00:00:00.000Z", "2026-11-12T15:00:00.000Z"),
        ]);
        fake.Requests[1].Query["types"]!.Split(',').ShouldContain("DIVIDEND_OR_INTEREST");

        table.Rows.Select(row => row[..^1]).ShouldBe(
        [
            [
                1001L, "11111111", At(2025, 3, 4, 14, 30), At(2025, 3, 4, 14, 30), At(2025, 3, 5), "TRADE", "VALID",
                "MARGIN", "EXECUTION", string.Empty, 5001L, 77L, -1500m, "AAPL", "EQUITY", 10m, 150m,
            ],
            [
                1002L, "22222222", At(2026, 2, 10, 12), null, null, "DIVIDEND_OR_INTEREST", null, null, null, null, null,
                null, 3.21m, null, null, null, null,
            ],
        ]);
        table.State!.Value.GetProperty("value").GetString().ShouldBe(At(2026, 2, 10, 12).ToString("O", CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task Transactions_resume_from_the_saved_cursor()
    {
        var fake = new FakeSchwab()
            .OnJson(HttpMethod.Get, FakeSchwab.AccountNumbers, Schwab.OneAccount)
            .OnJson(HttpMethod.Get, Transactions, "[]");
        var state = JsonSerializer.SerializeToElement(new { cursor = "time", value = "2026-10-01T00:00:00.0000000+00:00" });

        var table = (await Source(fake).ReadToMemoryAsync(
            Schwab.Config(),
            new ReadRequest(
                [new StreamSelection(SchwabSource.TransactionsStream, SyncMode.Incremental)],
                new Dictionary<string, JsonElement> { [SchwabSource.TransactionsStream] = state })))
            .Tables[SchwabSource.TransactionsStream];

        fake.Requests.Skip(1).Select(r => (r.Query["startDate"], r.Query["endDate"]))
            .ShouldBe([("2026-10-01T00:00:00.000Z", "2026-11-12T15:00:00.000Z")]);
        table.Rows.ShouldBeEmpty();
        table.State!.Value.GetProperty("value").GetString().ShouldBe("2026-10-01T00:00:00.0000000+00:00");
    }

    [Test]
    public async Task A_window_at_the_result_limit_is_split_in_half()
    {
        static string Item(int id, string time) =>
            string.Create(CultureInfo.InvariantCulture, $$"""{"activityId":{{id}},"time":"{{time}}","type":"JOURNAL","netAmount":1}""");

        var full = "[" + string.Join(",", Enumerable.Range(1, 3_000).Select(id => Item(id, "2026-11-02T00:00:00+0000"))) + "]";
        var fake = new FakeSchwab()
            .OnJson(HttpMethod.Get, FakeSchwab.AccountNumbers, Schwab.OneAccount)
            .On(HttpMethod.Get, Transactions, request => FakeSchwab.Json(
                (Query(request, "startDate"), Query(request, "endDate")) switch
                {
                    ("2026-11-01T00:00:00.000Z", "2026-11-12T15:00:00.000Z") => full,
                    ("2026-11-01T00:00:00.000Z", _) =>
                        $"[{Item(1, "2026-11-02T00:00:00+0000")},{Item(2, "2026-11-05T00:00:00+0000")}]",
                    _ => $"[{Item(2, "2026-11-05T00:00:00+0000")},{Item(3, "2026-11-08T00:00:00+0000")}]",
                }));

        var table = (await Source(fake).ReadToMemoryAsync(
            Schwab.Config(startDate: "2026-11-01"),
            Incremental(SchwabSource.TransactionsStream))).Tables[SchwabSource.TransactionsStream];

        fake.Requests.Skip(1).Select(r => (r.Query["startDate"], r.Query["endDate"])).ShouldBe(
        [
            ("2026-11-01T00:00:00.000Z", "2026-11-12T15:00:00.000Z"),
            ("2026-11-01T00:00:00.000Z", "2026-11-06T19:30:00.000Z"),
            ("2026-11-06T19:30:00.000Z", "2026-11-12T15:00:00.000Z"),
        ]);
        table.Rows.Select(row => row[0]).ShouldBe([1L, 2L, 3L]);
    }

    [Test]
    public async Task Orders_reread_the_lookback_window_every_sync()
    {
        const string order = """
            [{
              "session": "NORMAL", "duration": "DAY", "orderType": "LIMIT", "complexOrderStrategyType": "NONE",
              "quantity": 10, "filledQuantity": 10, "remainingQuantity": 0, "price": 150.25, "orderStrategyType": "SINGLE",
              "orderId": 9001, "cancelable": false, "editable": false, "status": "FILLED",
              "enteredTime": "2026-11-02T14:00:00+0000", "closeTime": "2026-11-02T14:00:01+0000", "accountNumber": 11111111,
              "orderLegCollection": [{
                "orderLegType": "EQUITY", "legId": 1, "instruction": "BUY", "positionEffect": "OPENING", "quantity": 10,
                "instrument": { "assetType": "EQUITY", "symbol": "AAPL" }
              }]
            }]
            """;
        var fake = new FakeSchwab()
            .OnJson(HttpMethod.Get, FakeSchwab.AccountNumbers, Schwab.OneAccount)
            .OnJson(HttpMethod.Get, "/trader/v1/accounts/HASH1/orders", order);

        var table = (await Source(fake).ReadToMemoryAsync(Schwab.Config(), Incremental(SchwabSource.OrdersStream)))
            .Tables[SchwabSource.OrdersStream];

        var query = fake.Requests[1].Query;
        (query["fromEnteredTime"], query["toEnteredTime"], query["maxResults"])
            .ShouldBe(("2026-09-13T15:01:00.000Z", "2026-11-12T15:00:00.000Z", "3000"));
        table.Stream.PrimaryKey.ShouldBe(["order_id"]);
        table.Rows.Select(row => row[..^3]).ShouldBe(
        [
            [
                9001L, "11111111", At(2026, 11, 2, 14), At(2026, 11, 2, 14, 0, 1), "FILLED", null, "LIMIT", "SINGLE", "NONE",
                "NORMAL", "DAY", 10m, 10m, 0m, 150.25m, null, "AAPL", "BUY", false, false,
            ],
        ]);
        table.State!.Value.EnumerateObject().ShouldBeEmpty();
    }

    [Test]
    public async Task An_expiring_token_is_refreshed_and_reported()
    {
        var fake = new FakeSchwab()
            .OnJson(HttpMethod.Post, FakeSchwab.Token, TokenReply)
            .OnJson(HttpMethod.Get, "/trader/v1/accounts", Accounts);

        var result = await Source(fake).ReadToMemoryAsync(
            Schwab.Config(expiresAt: Now.AddMinutes(1)),
            Incremental(SchwabSource.BalancesStream));

        var refresh = fake.Requests[0];
        refresh.Path.ShouldBe(FakeSchwab.Token);
        refresh.Authorization.ShouldBe("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("app-key:app-secret")));
        refresh.Body.ShouldBe("grant_type=refresh_token&refresh_token=refresh");
        fake.Requests[1].Authorization.ShouldBe("Bearer fresh");
        result.RefreshedCredentials.ShouldBe(new OAuthTokens("fresh", "refresh", Now.AddMinutes(30)));
    }

    [TestCase("invalid_grant", HttpStatusCode.BadRequest, ConnectorErrorKind.AuthExpired)]
    [TestCase("invalid_client", HttpStatusCode.Unauthorized, ConnectorErrorKind.ConfigInvalid)]
    public async Task A_refused_refresh_says_what_to_fix(string error, HttpStatusCode status, ConnectorErrorKind kind)
    {
        var fake = new FakeSchwab().On(HttpMethod.Post, FakeSchwab.Token, _ => FakeSchwab.Json($$"""{"error":"{{error}}"}""", status));

        var result = await Source(fake).CheckAsync(Schwab.Config(expiresAt: Now), default);

        result.ErrorKind.ShouldBe(kind);
    }

    [Test]
    public async Task A_401_refreshes_the_token_once_and_resends()
    {
        var calls = 0;
        var fake = new FakeSchwab()
            .On(HttpMethod.Get, FakeSchwab.AccountNumbers, _ => ++calls == 1
                ? FakeSchwab.Json("{}", HttpStatusCode.Unauthorized)
                : FakeSchwab.Json(Schwab.OneAccount))
            .OnJson(HttpMethod.Post, FakeSchwab.Token, TokenReply);

        var result = await Source(fake).CheckAsync(Schwab.Config(), default);

        result.Succeeded.ShouldBeTrue();
        fake.Requests.Select(r => r.Authorization?.Split(' ')[^1]).ShouldBe(["access", Convert.ToBase64String(Encoding.UTF8.GetBytes("app-key:app-secret")), "fresh"]);
    }

    [Test]
    public async Task CheckAsync_needs_the_app_and_a_sign_in()
    {
        var source = Source(new FakeSchwab());

        (await source.CheckAsync(Schwab.Config(withClient: false), default)).ErrorKind.ShouldBe(ConnectorErrorKind.ConfigInvalid);
        (await source.CheckAsync(Schwab.Config(withTokens: false), default)).ErrorKind.ShouldBe(ConnectorErrorKind.AuthExpired);
    }

    [Test]
    public async Task A_snapshot_stream_cannot_be_read_by_full_refresh()
    {
        var ex = await Should.ThrowAsync<ConnectorException>(() => Source(new FakeSchwab()).ReadToMemoryAsync(
            Schwab.Config(),
            new ReadRequest([new StreamSelection(SchwabSource.PositionsStream, SyncMode.FullRefresh)])));

        ex.Kind.ShouldBe(ConnectorErrorKind.ConfigInvalid);
    }

    private static SchwabSource Source(FakeSchwab fake) => new(new HttpClient(fake), new FixedTime(Now));

    private static ReadRequest Incremental(string stream) => new([new StreamSelection(stream, SyncMode.Incremental)]);

    private static DateTimeOffset At(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        new(year, month, day, hour, minute, second, TimeSpan.Zero);

    private static string? Query(HttpRequestMessage request, string key) =>
        System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)[key];

    private static HttpResponseMessage Reply(HttpRequestMessage request, string startDate, string transaction) =>
        FakeSchwab.Json(Query(request, "startDate") == startDate ? $"[{transaction}]" : "[]");
}
