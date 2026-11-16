using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Schwab.Tests;

[TestFixture]
public sealed class SchwabOrdersTests
{
    private const string Orders = "/trader/v1/accounts/HASH1/orders";

    [Test]
    public async Task Places_an_equity_order_and_returns_its_id()
    {
        var fake = Linked().On(HttpMethod.Post, Orders, _ => Created(1234567890));

        var result = await Trader(fake).PlaceAsync(
            Schwab.Config(allowOrders: true),
            "11111111",
            EquityOrder.Limit(EquityInstruction.Buy, " aapl ", 10, 150.25m));

        result.Value.ShouldBe(1234567890L);
        result.RefreshedCredentials.ShouldBeNull();
        ShouldMatch(fake.Requests[^1].Body, """
            {
              "session": "NORMAL", "duration": "DAY", "orderType": "LIMIT", "price": "150.25", "orderStrategyType": "SINGLE",
              "orderLegCollection": [{ "instruction": "BUY", "quantity": 10, "instrument": { "symbol": "AAPL", "assetType": "EQUITY" } }]
            }
            """);
    }

    [Test]
    public async Task A_bracket_nests_a_one_cancels_other_pair_under_a_trigger()
    {
        var fake = Linked().On(HttpMethod.Post, Orders, _ => Created(1));
        var bracket = new OneTriggersOther(
            EquityOrder.Limit(EquityInstruction.Buy, "AAPL", 10, 150m),
            new OneCancelsOther(
                EquityOrder.Limit(EquityInstruction.Sell, "AAPL", 10, 170m, duration: OrderDuration.GoodTillCancel),
                EquityOrder.Stop(EquityInstruction.Sell, "AAPL", 10, 140m, duration: OrderDuration.GoodTillCancel)));

        await Trader(fake).PlaceAsync(Schwab.Config(allowOrders: true), "11111111", bracket);

        ShouldMatch(fake.Requests[^1].Body, """
            {
              "session": "NORMAL", "duration": "DAY", "orderType": "LIMIT", "price": "150", "orderStrategyType": "TRIGGER",
              "orderLegCollection": [{ "instruction": "BUY", "quantity": 10, "instrument": { "symbol": "AAPL", "assetType": "EQUITY" } }],
              "childOrderStrategies": [{
                "orderStrategyType": "OCO",
                "childOrderStrategies": [
                  {
                    "session": "NORMAL", "duration": "GOOD_TILL_CANCEL", "orderType": "LIMIT", "price": "170", "orderStrategyType": "SINGLE",
                    "orderLegCollection": [{ "instruction": "SELL", "quantity": 10, "instrument": { "symbol": "AAPL", "assetType": "EQUITY" } }]
                  },
                  {
                    "session": "NORMAL", "duration": "GOOD_TILL_CANCEL", "orderType": "STOP", "stopPrice": "140", "orderStrategyType": "SINGLE",
                    "orderLegCollection": [{ "instruction": "SELL", "quantity": 10, "instrument": { "symbol": "AAPL", "assetType": "EQUITY" } }]
                  }
                ]
              }]
            }
            """);
    }

    [Test]
    public async Task Replace_and_cancel_address_the_order_by_id()
    {
        var fake = Linked()
            .On(HttpMethod.Put, $"{Orders}/1234", _ => Created(5678))
            .On(HttpMethod.Delete, $"{Orders}/5678", _ => new HttpResponseMessage(HttpStatusCode.OK));
        var trader = Trader(fake);
        var config = Schwab.Config(allowOrders: true);

        var replaced = await trader.ReplaceAsync(config, "11111111", 1234, EquityOrder.Limit(EquityInstruction.Buy, "AAPL", 10, 149m));
        await trader.CancelAsync(config, "11111111", 5678);

        replaced.Value.ShouldBe(5678L);
        fake.Requests.Where(r => r.Path != FakeSchwab.AccountNumbers).Select(r => (r.Method, r.Path))
            .ShouldBe([(HttpMethod.Put, $"{Orders}/1234"), (HttpMethod.Delete, $"{Orders}/5678")]);
    }

    [Test]
    public async Task Orders_are_refused_while_Allow_orders_is_off()
    {
        var fake = new FakeSchwab();

        var ex = await Should.ThrowAsync<ConnectorException>(() => Trader(fake).CancelAsync(Schwab.Config(), "11111111", 1));

        ex.Kind.ShouldBe(ConnectorErrorKind.ConfigInvalid);
        fake.Requests.ShouldBeEmpty();
    }

    [Test]
    public async Task A_5xx_after_sending_is_an_unknown_outcome_and_is_not_resent()
    {
        var fake = Linked().On(HttpMethod.Post, Orders, _ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var ex = await Should.ThrowAsync<ConnectorException>(() => Trader(fake).PlaceAsync(
            Schwab.Config(allowOrders: true),
            "11111111",
            EquityOrder.Market(EquityInstruction.Sell, "AAPL", 10)));

        ex.Kind.ShouldBe(ConnectorErrorKind.OutcomeUnknown);
        fake.Requests.Count(r => r.Method == HttpMethod.Post).ShouldBe(1);
    }

    [Test]
    public async Task A_rejected_order_carries_Schwabs_message()
    {
        var fake = Linked().On(HttpMethod.Post, Orders, _ => FakeSchwab.Json(
            """{"message":"Order rejected","errors":["Insufficient buying power."]}""",
            HttpStatusCode.BadRequest));

        var ex = await Should.ThrowAsync<ConnectorException>(() => Trader(fake).PlaceAsync(
            Schwab.Config(allowOrders: true),
            "11111111",
            EquityOrder.Market(EquityInstruction.Buy, "AAPL", 10)));

        ex.Kind.ShouldBe(ConnectorErrorKind.SourceError);
        ex.Message.ShouldBe("Schwab rejected the request (400). Order rejected Insufficient buying power.");
    }

    [Test]
    public async Task An_account_not_linked_to_the_sign_in_is_ConfigInvalid()
    {
        var ex = await Should.ThrowAsync<ConnectorException>(() => Trader(Linked()).CancelAsync(
            Schwab.Config(allowOrders: true), "99999999", 1));

        ex.Kind.ShouldBe(ConnectorErrorKind.ConfigInvalid);
    }

    private static FakeSchwab Linked() => new FakeSchwab().OnJson(HttpMethod.Get, FakeSchwab.AccountNumbers, Schwab.OneAccount);

    private static SchwabOrders Trader(FakeSchwab fake) => new(new HttpClient(fake), new FixedTime(Schwab.Now));

    private static HttpResponseMessage Created(long orderId) => new(HttpStatusCode.Created)
    {
        Headers =
        {
            Location = new Uri(string.Create(
                CultureInfo.InvariantCulture,
                $"https://api.schwabapi.com/trader/v1/accounts/HASH1/orders/{orderId}")),
        },
    };

    private static void ShouldMatch(string? actual, string expected) =>
        JsonNode.DeepEquals(JsonNode.Parse(actual!), JsonNode.Parse(expected)).ShouldBeTrue(actual);
}
