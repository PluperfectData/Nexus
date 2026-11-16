using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Schwab.Tests;

[TestFixture]
public sealed class SchwabMarketDataTests
{
    private const string Accounts = "/trader/v1/accounts";
    private const string PriceHistory = "/marketdata/v1/pricehistory";
    private const string Quotes = "/marketdata/v1/quotes";

    // One account holding a stock and an option.
    private const string Holdings = """
        [{
          "securitiesAccount": {
            "type": "MARGIN", "accountNumber": "11111111",
            "positions": [
              { "longQuantity": 10, "instrument": { "assetType": "EQUITY", "symbol": "AAPL" } },
              { "longQuantity": 1, "instrument": { "assetType": "OPTION", "symbol": "AAPL  261218C00150000" } }
            ],
            "currentBalances": {}
          }
        }]
        """;

    private const string Candles = """
        {
          "symbol": "AAPL", "empty": false,
          "candles": [
            { "open": 150.1, "high": 152.5, "low": 149.75, "close": 151.2, "volume": 1000000, "datetime": 1762927200000 },
            { "open": 151.2, "high": 153, "low": 150.5, "close": 152.8, "volume": 900000, "datetime": 1763013600000 }
          ]
        }
        """;

    private static readonly DateTimeOffset Now = Schwab.Now;

    private static readonly DateTimeOffset FirstCandle = DateTimeOffset.FromUnixTimeMilliseconds(1762927200000);

    private static readonly DateTimeOffset SecondCandle = DateTimeOffset.FromUnixTimeMilliseconds(1763013600000);

    [Test]
    public async Task Price_history_reads_each_held_and_listed_symbol_from_the_start_date()
    {
        var fake = new FakeSchwab()
            .OnJson(HttpMethod.Get, Accounts, Holdings)
            .On(HttpMethod.Get, PriceHistory, request => FakeSchwab.Json(
                Query(request, "symbol") == "AAPL" ? Candles : """{"symbol":"SPY","empty":true,"candles":[]}"""));

        var table = (await Source(fake).ReadToMemoryAsync(
            Schwab.Config(symbols: "spy, AAPL"),
            Incremental(SchwabSource.PriceHistoryStream))).Tables[SchwabSource.PriceHistoryStream];

        var history = fake.Requests.Skip(1).ToList();
        history.Select(r => r.Query["symbol"]).ShouldBe(["AAPL", "SPY"]);
        var query = history[0].Query;
        (query["periodType"], query["frequencyType"], query["frequency"]).ShouldBe(("year", "daily", "1"));
        query["startDate"].ShouldBe(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        query["endDate"].ShouldBe(Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        query["needExtendedHoursData"].ShouldBe("false");

        table.Stream.PrimaryKey.ShouldBe(["symbol", "candle", "time"]);
        table.Rows.ShouldBe(
        [
            ["AAPL", "Daily", FirstCandle, 150.1m, 152.5m, 149.75m, 151.2m, 1000000L],
            ["AAPL", "Daily", SecondCandle, 151.2m, 153m, 150.5m, 152.8m, 900000L],
        ]);
        var state = table.State!.Value;
        state.GetProperty("candles").GetString().ShouldBe("Daily");
        state.GetProperty("symbols").GetProperty("AAPL").GetString().ShouldBe(SecondCandle.ToString("O", CultureInfo.InvariantCulture));
        state.GetProperty("symbols").TryGetProperty("SPY", out _).ShouldBeFalse();
    }

    [Test]
    public async Task Price_history_resumes_each_symbol_from_its_newest_candle()
    {
        var fake = new FakeSchwab()
            .OnJson(HttpMethod.Get, Accounts, "[]")
            .OnJson(HttpMethod.Get, PriceHistory, """{"candles":[]}""");
        var saved = State(new { cursor = "time", candles = "Daily", symbols = new { AAPL = SecondCandle.ToString("O", CultureInfo.InvariantCulture) } });

        await Source(fake).ReadToMemoryAsync(
            Schwab.Config(symbols: "AAPL,MSFT"),
            new ReadRequest(
                [new StreamSelection(SchwabSource.PriceHistoryStream, SyncMode.Incremental)],
                new Dictionary<string, JsonElement> { [SchwabSource.PriceHistoryStream] = saved }));

        fake.Requests.Skip(1).Select(r => (r.Query["symbol"], r.Query["startDate"])).ShouldBe(
        [
            ("AAPL", SecondCandle.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
            ("MSFT", new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
        ]);
    }

    [Test]
    public async Task A_new_candle_size_starts_every_symbol_over_at_the_start_date()
    {
        var fake = new FakeSchwab()
            .OnJson(HttpMethod.Get, Accounts, "[]")
            .OnJson(HttpMethod.Get, PriceHistory, """{"candles":[]}""");
        var saved = State(new { cursor = "time", candles = "Daily", symbols = new { AAPL = SecondCandle.ToString("O", CultureInfo.InvariantCulture) } });

        await Source(fake).ReadToMemoryAsync(
            Schwab.Config(symbols: "AAPL", candles: "5 minutes"),
            new ReadRequest(
                [new StreamSelection(SchwabSource.PriceHistoryStream, SyncMode.Incremental)],
                new Dictionary<string, JsonElement> { [SchwabSource.PriceHistoryStream] = saved }));

        var query = fake.Requests[1].Query;
        (query["periodType"], query["frequencyType"], query["frequency"]).ShouldBe(("day", "minute", "5"));
        query["startDate"].ShouldBe(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task Quotes_cover_held_options_too_and_skip_symbols_Schwab_does_not_know()
    {
        const string reply = """
            {
              "AAPL": {
                "assetMainType": "EQUITY", "symbol": "AAPL",
                "quote": {
                  "52WeekHigh": 260.1, "52WeekLow": 164.08, "askPrice": 151.3, "askSize": 2, "bidPrice": 151.2, "bidSize": 3,
                  "closePrice": 150, "highPrice": 152, "lastPrice": 151.25, "lowPrice": 149.5, "mark": 151.25,
                  "netChange": 1.25, "netPercentChange": 0.8333, "openPrice": 150.5, "quoteTime": 1762959600000,
                  "totalVolume": 1234567, "tradeTime": 1762959599000
                },
                "reference": { "description": "Apple Inc", "exchangeName": "NASDAQ" }
              },
              "errors": { "invalidSymbols": ["NOPE"] }
            }
            """;
        var fake = new FakeSchwab()
            .OnJson(HttpMethod.Get, Accounts, Holdings)
            .OnJson(HttpMethod.Get, Quotes, reply);

        var messages = await Messages(Source(fake), Schwab.Config(symbols: "NOPE"), SchwabSource.QuotesStream);

        var query = fake.Requests[1].Query;
        query["symbols"].ShouldBe("AAPL,AAPL  261218C00150000,NOPE");
        query["fields"].ShouldBe("quote,reference");

        var quoteTime = DateTimeOffset.FromUnixTimeMilliseconds(1762959600000);
        var tradeTime = DateTimeOffset.FromUnixTimeMilliseconds(1762959599000);
        messages.OfType<RecordBatch>().ShouldHaveSingleItem().Rows.Select(row => row[..^1]).ShouldBe(
        [
            [
                Now, "AAPL", "EQUITY", "Apple Inc", "NASDAQ", 151.25m, 151.2m, 151.3m, 3L, 2L, 150.5m, 152m, 149.5m, 150m,
                1.25m, 0.8333m, 151.25m, 1234567L, 260.1m, 164.08m, quoteTime, tradeTime,
            ],
        ]);
        messages.OfType<LogMessage>().ShouldHaveSingleItem().ShouldBe(
            new LogMessage(LogLevel.Warning, "Schwab has no quote for NOPE, so it was skipped."));
    }

    [Test]
    public async Task Movers_read_each_index_by_every_sort_ranked_as_Schwab_lists_them()
    {
        var fake = new FakeSchwab()
            .On(HttpMethod.Get, "/marketdata/v1/movers/$SPX", request => FakeSchwab.Json(Query(request, "sort") == "VOLUME"
                ? """
                  {"screeners":[
                    {"symbol":"NVDA","description":"NVIDIA CORP","lastPrice":140.5,"netChange":2.5,"netPercentChange":0.018,"volume":5000,"totalVolume":90000,"trades":1200,"marketShare":5.5},
                    {"symbol":"TSLA","description":"TESLA INC","last":250,"change":-4,"direction":"down","totalVolume":80000},
                    {"symbol":"NVDA","description":"NVIDIA CORP","lastPrice":140.5}
                  ]}
                  """
                : """{"screeners":[]}"""));

        var table = (await Source(fake).ReadToMemoryAsync(Schwab.Config(movers: "$spx"), Incremental(SchwabSource.MoversStream)))
            .Tables[SchwabSource.MoversStream];

        fake.Requests.Select(r => (r.Path, r.Query["sort"])).ShouldBe(
        [
            ("/marketdata/v1/movers/$SPX", "VOLUME"),
            ("/marketdata/v1/movers/$SPX", "TRADES"),
            ("/marketdata/v1/movers/$SPX", "PERCENT_CHANGE_UP"),
            ("/marketdata/v1/movers/$SPX", "PERCENT_CHANGE_DOWN"),
        ]);
        table.Rows.ShouldBe(
        [
            [Now, "$SPX", "VOLUME", 1L, "NVDA", "NVIDIA CORP", 140.5m, 2.5m, 0.018m, 5000L, 90000L, 1200L, 5.5m, null],
            [Now, "$SPX", "VOLUME", 2L, "TSLA", "TESLA INC", 250m, -4m, null, null, 80000L, null, null, "down"],
        ]);
    }

    [TestCase("$SPX", "Hourly")]
    [TestCase("$SPX, NOT AN INDEX", "Daily")]
    public async Task CheckAsync_refuses_a_candle_size_or_index_it_cannot_read(string movers, string candles)
    {
        var result = await Source(new FakeSchwab()).CheckAsync(Schwab.Config(movers: movers, candles: candles), default);

        result.ErrorKind.ShouldBe(ConnectorErrorKind.ConfigInvalid);
    }

    private static SchwabSource Source(FakeSchwab fake) => new(new HttpClient(fake), new FixedTime(Now));

    private static ReadRequest Incremental(string stream) => new([new StreamSelection(stream, SyncMode.Incremental)]);

    private static JsonElement State(object state) => JsonSerializer.SerializeToElement(state);

    private static string? Query(HttpRequestMessage request, string key) =>
        System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)[key];

    private static async Task<List<SyncMessage>> Messages(SchwabSource source, ConnectorConfig config, string stream)
    {
        var messages = new List<SyncMessage>();
        await foreach (var message in source.ReadAsync(config, Incremental(stream), default))
        {
            messages.Add(message);
        }

        return messages;
    }
}
