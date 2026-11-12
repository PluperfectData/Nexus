using System.Text.Json;

namespace Nexus.Schwab;

internal sealed record AccountSnapshot(DateTimeOffset ReadAt, string Number, JsonElement Account, JsonElement Balances, JsonElement Aggregated);

internal sealed record PositionSnapshot(DateTimeOffset ReadAt, string Number, JsonElement Position, JsonElement Instrument);

internal sealed record TransactionEntry(string Number, JsonElement Transaction, JsonElement Security, JsonElement Instrument);

internal sealed record OrderEntry(string Number, JsonElement Order, JsonElement Leg, JsonElement Instrument);

/// <summary>A stream's columns, each paired with how it reads its value, so a row always matches its schema.</summary>
internal sealed class StreamDef<T>
{
    private readonly List<Func<T, object?>> _reads;

    public StreamDef(
        string name,
        Columns<T> columns,
        IReadOnlyList<string> primaryKey,
        IReadOnlyList<string> cursorFields,
        IReadOnlyList<SyncMode> modes)
    {
        _reads = columns.Fields.Select(field => field.Read).ToList();
        Spec = new StreamSpec(name, columns.Fields.Select(field => field.Column).ToList(), primaryKey, cursorFields, modes);
    }

    public StreamSpec Spec { get; }

    public object?[] Row(T source)
    {
        var row = new object?[_reads.Count];
        for (var i = 0; i < row.Length; i++)
        {
            var value = _reads[i](source);
            if (value is null && !Spec.Columns[i].Nullable)
            {
                throw new ConnectorException(ConnectorErrorKind.SourceError, $"Schwab sent no value for '{Spec.Columns[i].Name}'.");
            }

            row[i] = value;
        }

        return row;
    }
}

internal sealed class Columns<T>
{
    public List<(ColumnSpec Column, Func<T, object?> Read)> Fields { get; } = [];

    public Columns<T> Text(string name, Func<T, string?> read, bool nullable = true) =>
        Add(new ColumnSpec(name, ColumnType.String, nullable), source => read(source));

    public Columns<T> Int64(string name, Func<T, long?> read, bool nullable = true) =>
        Add(new ColumnSpec(name, ColumnType.Int64, nullable), source => read(source));

    public Columns<T> Decimal(string name, Func<T, decimal?> read) =>
        Add(new ColumnSpec(name, ColumnType.Decimal, true, SchwabJson.Precision, SchwabJson.Scale), source => read(source));

    public Columns<T> Boolean(string name, Func<T, bool?> read) =>
        Add(new ColumnSpec(name, ColumnType.Boolean, true), source => read(source));

    public Columns<T> Timestamp(string name, Func<T, DateTimeOffset?> read, bool nullable = true) =>
        Add(new ColumnSpec(name, ColumnType.Timestamp, nullable), source => read(source));

    public Columns<T> Json(string name, Func<T, string?> read) =>
        Add(new ColumnSpec(name, ColumnType.Json, true), source => read(source));

    private Columns<T> Add(ColumnSpec column, Func<T, object?> read)
    {
        Fields.Add((column, read));
        return this;
    }
}

internal static class SchwabStreams
{
    public static StreamDef<AccountSnapshot> Balances { get; } = new(
        SchwabSource.BalancesStream,
        new Columns<AccountSnapshot>()
            .Timestamp("read_at", s => s.ReadAt, nullable: false)
            .Text("account_number", s => s.Number, nullable: false)
            .Text("account_type", s => SchwabJson.String(s.Account, "type"))
            .Decimal(
                "liquidation_value",
                s => SchwabJson.Decimal(s.Balances, "liquidationValue") ?? SchwabJson.Decimal(s.Aggregated, "liquidationValue"))
            .Decimal("cash_balance", s => SchwabJson.Decimal(s.Balances, "cashBalance"))
            .Decimal("total_cash", s => SchwabJson.Decimal(s.Balances, "totalCash"))
            .Decimal("cash_available_for_trading", s => SchwabJson.Decimal(s.Balances, "cashAvailableForTrading"))
            .Decimal("cash_available_for_withdrawal", s => SchwabJson.Decimal(s.Balances, "cashAvailableForWithdrawal"))
            .Decimal("unsettled_cash", s => SchwabJson.Decimal(s.Balances, "unsettledCash"))
            .Decimal("equity", s => SchwabJson.Decimal(s.Balances, "equity"))
            .Decimal("buying_power", s => SchwabJson.Decimal(s.Balances, "buyingPower"))
            .Decimal("available_funds", s => SchwabJson.Decimal(s.Balances, "availableFunds"))
            .Decimal("margin_balance", s => SchwabJson.Decimal(s.Balances, "marginBalance"))
            .Decimal("maintenance_requirement", s => SchwabJson.Decimal(s.Balances, "maintenanceRequirement"))
            .Json("current_balances", s => SchwabJson.Json(s.Account, "currentBalances")),
        primaryKey: ["read_at", "account_number"],
        cursorFields: ["read_at"],
        modes: [SyncMode.Incremental]);

    public static StreamDef<PositionSnapshot> Positions { get; } = new(
        SchwabSource.PositionsStream,
        new Columns<PositionSnapshot>()
            .Timestamp("read_at", s => s.ReadAt, nullable: false)
            .Text("account_number", s => s.Number, nullable: false)
            .Text("symbol", s => SchwabJson.String(s.Instrument, "symbol"), nullable: false)
            .Text("cusip", s => SchwabJson.String(s.Instrument, "cusip"))
            .Text("asset_type", s => SchwabJson.String(s.Instrument, "assetType"))
            .Text("description", s => SchwabJson.String(s.Instrument, "description"))
            .Decimal("long_quantity", s => SchwabJson.Decimal(s.Position, "longQuantity"))
            .Decimal("short_quantity", s => SchwabJson.Decimal(s.Position, "shortQuantity"))
            .Decimal("average_price", s => SchwabJson.Decimal(s.Position, "averagePrice"))
            .Decimal("market_value", s => SchwabJson.Decimal(s.Position, "marketValue"))
            .Decimal("current_day_profit_loss", s => SchwabJson.Decimal(s.Position, "currentDayProfitLoss"))
            .Decimal("long_open_profit_loss", s => SchwabJson.Decimal(s.Position, "longOpenProfitLoss"))
            .Decimal("short_open_profit_loss", s => SchwabJson.Decimal(s.Position, "shortOpenProfitLoss")),
        primaryKey: ["read_at", "account_number", "symbol"],
        cursorFields: ["read_at"],
        modes: [SyncMode.Incremental]);

    public static StreamDef<TransactionEntry> Transactions { get; } = new(
        SchwabSource.TransactionsStream,
        new Columns<TransactionEntry>()
            .Int64("activity_id", s => SchwabJson.Int64(s.Transaction, "activityId"), nullable: false)
            .Text("account_number", s => s.Number, nullable: false)
            .Timestamp("time", s => SchwabJson.Timestamp(s.Transaction, "time"), nullable: false)
            .Timestamp("trade_date", s => SchwabJson.Timestamp(s.Transaction, "tradeDate"))
            .Timestamp("settlement_date", s => SchwabJson.Timestamp(s.Transaction, "settlementDate"))
            .Text("type", s => SchwabJson.String(s.Transaction, "type"))
            .Text("status", s => SchwabJson.String(s.Transaction, "status"))
            .Text("sub_account", s => SchwabJson.String(s.Transaction, "subAccount"))
            .Text("activity_type", s => SchwabJson.String(s.Transaction, "activityType"))
            .Text("description", s => SchwabJson.String(s.Transaction, "description"))
            .Int64("order_id", s => SchwabJson.Int64(s.Transaction, "orderId"))
            .Int64("position_id", s => SchwabJson.Int64(s.Transaction, "positionId"))
            .Decimal("net_amount", s => SchwabJson.Decimal(s.Transaction, "netAmount"))
            .Text("symbol", s => SchwabJson.String(s.Instrument, "symbol"))
            .Text("asset_type", s => SchwabJson.String(s.Instrument, "assetType"))
            .Decimal("quantity", s => SchwabJson.Decimal(s.Security, "amount"))
            .Decimal("price", s => SchwabJson.Decimal(s.Security, "price"))
            .Json("transfer_items", s => SchwabJson.Json(s.Transaction, "transferItems")),
        primaryKey: ["activity_id"],
        cursorFields: ["time"],
        modes: [SyncMode.FullRefresh, SyncMode.Incremental]);

    public static StreamDef<OrderEntry> Orders { get; } = new(
        SchwabSource.OrdersStream,
        new Columns<OrderEntry>()
            .Int64("order_id", s => SchwabJson.Int64(s.Order, "orderId"), nullable: false)
            .Text("account_number", s => s.Number, nullable: false)
            .Timestamp("entered_time", s => SchwabJson.Timestamp(s.Order, "enteredTime"))
            .Timestamp("close_time", s => SchwabJson.Timestamp(s.Order, "closeTime"))
            .Text("status", s => SchwabJson.String(s.Order, "status"))
            .Text("status_description", s => SchwabJson.String(s.Order, "statusDescription"))
            .Text("order_type", s => SchwabJson.String(s.Order, "orderType"))
            .Text("order_strategy_type", s => SchwabJson.String(s.Order, "orderStrategyType"))
            .Text("complex_order_strategy_type", s => SchwabJson.String(s.Order, "complexOrderStrategyType"))
            .Text("session", s => SchwabJson.String(s.Order, "session"))
            .Text("duration", s => SchwabJson.String(s.Order, "duration"))
            .Decimal("quantity", s => SchwabJson.Decimal(s.Order, "quantity"))
            .Decimal("filled_quantity", s => SchwabJson.Decimal(s.Order, "filledQuantity"))
            .Decimal("remaining_quantity", s => SchwabJson.Decimal(s.Order, "remainingQuantity"))
            .Decimal("price", s => SchwabJson.Decimal(s.Order, "price"))
            .Decimal("stop_price", s => SchwabJson.Decimal(s.Order, "stopPrice"))
            .Text("symbol", s => SchwabJson.String(s.Instrument, "symbol"))
            .Text("instruction", s => SchwabJson.String(s.Leg, "instruction"))
            .Boolean("cancelable", s => SchwabJson.Boolean(s.Order, "cancelable"))
            .Boolean("editable", s => SchwabJson.Boolean(s.Order, "editable"))
            .Json("legs", s => SchwabJson.Json(s.Order, "orderLegCollection"))
            .Json("child_orders", s => SchwabJson.Json(s.Order, "childOrderStrategies"))
            .Json("activity", s => SchwabJson.Json(s.Order, "orderActivityCollection")),
        primaryKey: ["order_id"],
        cursorFields: [],
        modes: [SyncMode.Incremental]);
}
