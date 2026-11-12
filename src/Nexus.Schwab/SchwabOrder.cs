using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Nexus.Schwab;

/// <summary>What an equity order does.</summary>
public enum EquityInstruction
{
    /// <summary>Buys shares.</summary>
    Buy,

    /// <summary>Sells shares the account holds.</summary>
    Sell,

    /// <summary>Sells borrowed shares.</summary>
    SellShort,

    /// <summary>Buys shares to close a short position.</summary>
    BuyToCover,
}

/// <summary>How an equity order is priced.</summary>
public enum EquityOrderType
{
    /// <summary>At the market price.</summary>
    Market,

    /// <summary>At the limit price or better.</summary>
    Limit,

    /// <summary>A market order once the stop price trades.</summary>
    Stop,

    /// <summary>A limit order once the stop price trades.</summary>
    StopLimit,
}

/// <summary>How long an order stays working.</summary>
public enum OrderDuration
{
    /// <summary>Until the session ends.</summary>
    Day,

    /// <summary>Until it fills or is canceled.</summary>
    GoodTillCancel,

    /// <summary>Fills in full at once, or is canceled.</summary>
    FillOrKill,
}

/// <summary>The session an order works in.</summary>
public enum OrderSession
{
    /// <summary>Regular market hours.</summary>
    Normal,

    /// <summary>The premarket session.</summary>
    Am,

    /// <summary>The after-hours session.</summary>
    Pm,

    /// <summary>Every session, premarket through after hours.</summary>
    Seamless,
}

/// <summary>
/// An order to send to Schwab: one <see cref="EquityOrder"/>, or a <see cref="OneCancelsOther"/> or
/// <see cref="OneTriggersOther"/> group of them.
/// </summary>
public abstract record SchwabOrder
{
    private protected SchwabOrder()
    {
    }

    internal abstract JsonObject ToJson();
}

/// <summary>
/// One order for a stock or ETF, made by <see cref="Market"/>, <see cref="Limit"/>, <see cref="Stop"/>, or
/// <see cref="StopLimit"/>.
/// </summary>
public sealed record EquityOrder : SchwabOrder
{
    private EquityOrder(
        EquityOrderType type,
        EquityInstruction instruction,
        string symbol,
        decimal quantity,
        decimal? limitPrice,
        decimal? stopPrice,
        OrderSession session,
        OrderDuration duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        if (limitPrice is { } limit)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit, nameof(limitPrice));
        }

        if (stopPrice is { } stop)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stop, nameof(stopPrice));
        }

        if (!Enum.IsDefined(instruction))
        {
            throw new ArgumentOutOfRangeException(nameof(instruction));
        }

        if (!Enum.IsDefined(session))
        {
            throw new ArgumentOutOfRangeException(nameof(session));
        }

        if (!Enum.IsDefined(duration))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        OrderType = type;
        Instruction = instruction;
        Symbol = symbol.Trim().ToUpperInvariant();
        Quantity = quantity;
        LimitPrice = limitPrice;
        StopPrice = stopPrice;
        Session = session;
        Duration = duration;
    }

    /// <summary>How the order is priced.</summary>
    public EquityOrderType OrderType { get; }

    /// <summary>Whether it buys or sells.</summary>
    public EquityInstruction Instruction { get; }

    /// <summary>The stock or ETF symbol, in capitals.</summary>
    public string Symbol { get; }

    /// <summary>The number of shares.</summary>
    public decimal Quantity { get; }

    /// <summary>The limit price, for a limit or stop-limit order.</summary>
    public decimal? LimitPrice { get; }

    /// <summary>The stop price, for a stop or stop-limit order.</summary>
    public decimal? StopPrice { get; }

    /// <summary>The session the order works in.</summary>
    public OrderSession Session { get; }

    /// <summary>How long the order stays working.</summary>
    public OrderDuration Duration { get; }

    /// <summary>An order at the market price.</summary>
    public static EquityOrder Market(
        EquityInstruction instruction,
        string symbol,
        decimal quantity,
        OrderSession session = OrderSession.Normal,
        OrderDuration duration = OrderDuration.Day) =>
        new(EquityOrderType.Market, instruction, symbol, quantity, null, null, session, duration);

    /// <summary>An order at <paramref name="limitPrice"/> or better.</summary>
    public static EquityOrder Limit(
        EquityInstruction instruction,
        string symbol,
        decimal quantity,
        decimal limitPrice,
        OrderSession session = OrderSession.Normal,
        OrderDuration duration = OrderDuration.Day) =>
        new(EquityOrderType.Limit, instruction, symbol, quantity, limitPrice, null, session, duration);

    /// <summary>A market order once <paramref name="stopPrice"/> trades.</summary>
    public static EquityOrder Stop(
        EquityInstruction instruction,
        string symbol,
        decimal quantity,
        decimal stopPrice,
        OrderSession session = OrderSession.Normal,
        OrderDuration duration = OrderDuration.Day) =>
        new(EquityOrderType.Stop, instruction, symbol, quantity, null, stopPrice, session, duration);

    /// <summary>A limit order at <paramref name="limitPrice"/> once <paramref name="stopPrice"/> trades.</summary>
    public static EquityOrder StopLimit(
        EquityInstruction instruction,
        string symbol,
        decimal quantity,
        decimal stopPrice,
        decimal limitPrice,
        OrderSession session = OrderSession.Normal,
        OrderDuration duration = OrderDuration.Day) =>
        new(EquityOrderType.StopLimit, instruction, symbol, quantity, limitPrice, stopPrice, session, duration);

    internal override JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["session"] = Session switch
            {
                OrderSession.Normal => "NORMAL",
                OrderSession.Am => "AM",
                OrderSession.Pm => "PM",
                OrderSession.Seamless => "SEAMLESS",
                _ => throw new UnreachableException(),
            },
            ["duration"] = Duration switch
            {
                OrderDuration.Day => "DAY",
                OrderDuration.GoodTillCancel => "GOOD_TILL_CANCEL",
                OrderDuration.FillOrKill => "FILL_OR_KILL",
                _ => throw new UnreachableException(),
            },
            ["orderType"] = OrderType switch
            {
                EquityOrderType.Market => "MARKET",
                EquityOrderType.Limit => "LIMIT",
                EquityOrderType.Stop => "STOP",
                EquityOrderType.StopLimit => "STOP_LIMIT",
                _ => throw new UnreachableException(),
            },
        };

        // Prices go as strings, the form schwab-py sends.
        if (LimitPrice is { } limit)
        {
            json["price"] = limit.ToString(CultureInfo.InvariantCulture);
        }

        if (StopPrice is { } stop)
        {
            json["stopPrice"] = stop.ToString(CultureInfo.InvariantCulture);
        }

        json["orderStrategyType"] = "SINGLE";
        json["orderLegCollection"] = new JsonArray(new JsonObject
        {
            ["instruction"] = Instruction switch
            {
                EquityInstruction.Buy => "BUY",
                EquityInstruction.Sell => "SELL",
                EquityInstruction.SellShort => "SELL_SHORT",
                EquityInstruction.BuyToCover => "BUY_TO_COVER",
                _ => throw new UnreachableException(),
            },
            ["quantity"] = Quantity,
            ["instrument"] = new JsonObject { ["symbol"] = Symbol, ["assetType"] = "EQUITY" },
        });

        return json;
    }
}

/// <summary>Two orders that work together. When one fills, Schwab cancels the other.</summary>
public sealed record OneCancelsOther : SchwabOrder
{
    /// <summary>Groups two orders.</summary>
    public OneCancelsOther(EquityOrder first, EquityOrder second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        First = first;
        Second = second;
    }

    /// <summary>One order.</summary>
    public EquityOrder First { get; }

    /// <summary>The other order.</summary>
    public EquityOrder Second { get; }

    internal override JsonObject ToJson() => new()
    {
        ["orderStrategyType"] = "OCO",
        ["childOrderStrategies"] = new JsonArray(First.ToJson(), Second.ToJson()),
    };
}

/// <summary>
/// An order that sends a second order or group once it fills, such as a bracket: a buy that triggers a
/// <see cref="OneCancelsOther"/> pair of exits.
/// </summary>
public sealed record OneTriggersOther : SchwabOrder
{
    /// <summary>Sends <paramref name="second"/> once <paramref name="first"/> fills.</summary>
    public OneTriggersOther(EquityOrder first, SchwabOrder second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        First = first;
        Second = second;
    }

    /// <summary>The order that works first.</summary>
    public EquityOrder First { get; }

    /// <summary>The order or group sent once <see cref="First"/> fills.</summary>
    public SchwabOrder Second { get; }

    internal override JsonObject ToJson()
    {
        var json = First.ToJson();
        json["orderStrategyType"] = "TRIGGER";
        json["childOrderStrategies"] = new JsonArray(Second.ToJson());
        return json;
    }
}
