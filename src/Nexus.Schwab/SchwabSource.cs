using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Nexus.Schwab;

/// <summary>Reads balances, positions, transactions, and orders from every account linked to a Schwab sign-in.</summary>
public sealed class SchwabSource(HttpClient client, TimeProvider? timeProvider = null) : ISourceConnector
{
    /// <summary>A snapshot of each account's balances, appended on every read.</summary>
    public const string BalancesStream = "balances";

    /// <summary>A snapshot of each account's positions, appended on every read.</summary>
    public const string PositionsStream = "positions";

    /// <summary>Trades, dividends, interest, and transfers, read from the start date forward.</summary>
    public const string TransactionsStream = "transactions";

    /// <summary>Orders entered in Schwab's lookback window, read again on every sync.</summary>
    public const string OrdersStream = "orders";

    private const int BatchSize = 10_000;

    // Schwab returns at most 3,000 transactions or orders per request.
    private const int PageLimit = 3_000;

    private const string TransactionTypes =
        "TRADE,RECEIVE_AND_DELIVER,DIVIDEND_OR_INTEREST,ACH_RECEIPT,ACH_DISBURSEMENT,CASH_RECEIPT,CASH_DISBURSEMENT,"
        + "ELECTRONIC_FUND,WIRE_OUT,WIRE_IN,JOURNAL,MEMORANDUM,MARGIN_CALL,MONEY_MARKET,SMA_ADJUSTMENT";

    // One transactions request spans a year at most.
    private static readonly TimeSpan WindowLength = TimeSpan.FromDays(365);

    // Schwab rejects an orders query that starts more than 60 days back; the minute covers clock skew.
    private static readonly TimeSpan OrdersLookback = TimeSpan.FromDays(60) - TimeSpan.FromMinutes(1);

    private static readonly TimeSpan ShortestWindow = TimeSpan.FromSeconds(1);

    private static readonly JsonElement EmptyState = JsonSerializer.SerializeToElement(new { });

    private static readonly SourceCatalog Catalog = new(
    [
        SchwabStreams.Balances.Spec,
        SchwabStreams.Positions.Spec,
        SchwabStreams.Transactions.Spec,
        SchwabStreams.Orders.Spec,
    ]);

    private readonly HttpClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public ConnectorSpec Spec => SchwabSettings.Spec;

    /// <inheritdoc />
    public async Task<CheckResult> CheckAsync(ConnectorConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            _ = SchwabSettings.ReadStartDate(config);
            var accounts = await new SchwabApi(_client, _time, config).AccountsAsync(ct).ConfigureAwait(false);
            return accounts.Count > 0
                ? CheckResult.Success
                : CheckResult.Failure(ConnectorErrorKind.ConfigInvalid, "No accounts are linked to this Schwab sign-in.");
        }
        catch (ConnectorException ex)
        {
            return CheckResult.Failure(ex.Kind, ex.Message);
        }
    }

    /// <inheritdoc />
    public Task<SourceCatalog> DiscoverAsync(ConnectorConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        return Task.FromResult(Catalog);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SyncMessage> ReadAsync(
        ConnectorConfig config,
        ReadRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(request);

        foreach (var selection in request.Streams)
        {
            Validate(selection);
        }

        if (request.Streams.Count == 0)
        {
            yield break;
        }

        var api = new SchwabApi(_client, _time, config);
        JsonDocument? snapshot = null;
        try
        {
            if (request.Streams.Any(selection => selection.Stream is BalancesStream or PositionsStream))
            {
                snapshot = await api.GetAsync("accounts?fields=positions", ct).ConfigureAwait(false);
                if (api.TakeRefreshed() is { } tokens)
                {
                    yield return new CredentialsRefreshed(tokens);
                }
            }

            var readAt = _time.GetUtcNow();
            foreach (var selection in request.Streams)
            {
                switch (selection.Stream)
                {
                    case BalancesStream:
                        foreach (var message in Snapshot(SchwabStreams.Balances, Accounts(snapshot!, readAt), readAt))
                        {
                            yield return message;
                        }

                        break;

                    case PositionsStream:
                        foreach (var message in Snapshot(SchwabStreams.Positions, Positions(snapshot!, readAt), readAt))
                        {
                            yield return message;
                        }

                        break;

                    case TransactionsStream:
                        await foreach (var message in TransactionsAsync(api, config, selection, request, ct).ConfigureAwait(false))
                        {
                            yield return message;
                        }

                        break;

                    default:
                        await foreach (var message in OrdersAsync(api, ct).ConfigureAwait(false))
                        {
                            yield return message;
                        }

                        break;
                }
            }
        }
        finally
        {
            snapshot?.Dispose();
        }
    }

    private static void Validate(StreamSelection selection)
    {
        var spec = Catalog.Streams.FirstOrDefault(stream => stream.Name == selection.Stream)
            ?? throw new ConnectorException(
                ConnectorErrorKind.ConfigInvalid,
                $"Stream '{selection.Stream}' does not exist. The streams are balances, positions, transactions, and orders.");

        if (!spec.SyncModes.Contains(selection.Mode))
        {
            throw new ConnectorException(ConnectorErrorKind.ConfigInvalid, $"'{spec.Name}' is read incrementally only.");
        }

        if (selection.Mode == SyncMode.Incremental && selection.CursorField is { } field && !spec.CursorFields.Contains(field))
        {
            throw new ConnectorException(
                ConnectorErrorKind.ConfigInvalid,
                spec.CursorFields.Count == 0
                    ? $"'{spec.Name}' takes no cursor field."
                    : $"'{field}' cannot be the cursor of '{spec.Name}'. Its cursor is '{spec.CursorFields[0]}'.");
        }
    }

    private static IEnumerable<SyncMessage> Snapshot<T>(StreamDef<T> stream, IEnumerable<T> sources, DateTimeOffset readAt)
    {
        yield return new SchemaMessage(stream.Spec);
        foreach (var batch in Batches(sources.Select(stream.Row)))
        {
            yield return new RecordBatch(stream.Spec.Name, batch);
        }

        yield return new StateCheckpoint(stream.Spec.Name, Cursor("read_at", readAt));
    }

    private static IEnumerable<AccountSnapshot> Accounts(JsonDocument snapshot, DateTimeOffset readAt)
    {
        foreach (var entry in SchwabJson.Items(snapshot.RootElement))
        {
            var account = SchwabJson.Property(entry, "securitiesAccount")
                ?? throw new ConnectorException(ConnectorErrorKind.SourceError, "Schwab sent an account without its details.");

            yield return new AccountSnapshot(
                readAt,
                SchwabJson.RequiredString(account, "accountNumber"),
                account,
                SchwabJson.Property(account, "currentBalances") ?? default,
                SchwabJson.Property(entry, "aggregatedBalance") ?? default);
        }
    }

    private static IEnumerable<PositionSnapshot> Positions(JsonDocument snapshot, DateTimeOffset readAt)
    {
        foreach (var account in Accounts(snapshot, readAt))
        {
            var symbols = new HashSet<string>(StringComparer.Ordinal);
            foreach (var position in SchwabJson.Items(account.Account, "positions"))
            {
                var instrument = SchwabJson.Property(position, "instrument") ?? default;
                if (SchwabJson.String(instrument, "symbol") is { } symbol && !symbols.Add(symbol))
                {
                    throw new ConnectorException(
                        ConnectorErrorKind.SourceError,
                        $"Account {account.Number} holds two positions in {symbol}, so the positions stream cannot tell them apart.");
                }

                yield return new PositionSnapshot(readAt, account.Number, position, instrument);
            }
        }
    }

    private static TransactionEntry TransactionOf(string number, JsonElement transaction)
    {
        var security = SchwabJson.Items(transaction, "transferItems")
            .FirstOrDefault(item => SchwabJson.String(SchwabJson.Property(item, "instrument") ?? default, "assetType") is not (null or "CURRENCY"));

        return new TransactionEntry(number, transaction, security, SchwabJson.Property(security, "instrument") ?? default);
    }

    private static OrderEntry OrderOf(string number, JsonElement order)
    {
        var legs = SchwabJson.Items(order, "orderLegCollection").Take(2).ToList();
        var leg = legs.Count == 1 ? legs[0] : default;

        return new OrderEntry(number, order, leg, SchwabJson.Property(leg, "instrument") ?? default);
    }

    private static IEnumerable<List<object?[]>> Batches(IEnumerable<object?[]> rows)
    {
        var batch = new List<object?[]>();
        foreach (var row in rows)
        {
            batch.Add(row);
            if (batch.Count == BatchSize)
            {
                yield return batch;
                batch = [];
            }
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    private static JsonElement Cursor(string cursor, DateTimeOffset value) =>
        JsonSerializer.SerializeToElement(new { cursor, value = value.ToString("O", CultureInfo.InvariantCulture) });

    private static DateTimeOffset? SavedCursor(ReadRequest request, string stream)
    {
        if (request.State is null
            || !request.State.TryGetValue(stream, out var state)
            || SchwabJson.Property(state, "cursor") is null)
        {
            return null;
        }

        return SchwabJson.String(state, "value") is { } text
            && DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value.ToUniversalTime()
            : throw new ConnectorException(ConnectorErrorKind.ConfigInvalid, $"The saved state for '{stream}' cannot be read.");
    }

    /// <summary>Reads one time window, splitting it in half while Schwab returns as many results as it sends.</summary>
    private static async Task<List<JsonElement>> WindowAsync(
        SchwabApi api,
        string path,
        (string From, string To) keys,
        string extra,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        List<JsonElement> items;
        var query = $"{path}?{keys.From}={Uri.EscapeDataString(SchwabJson.Format(from))}"
            + $"&{keys.To}={Uri.EscapeDataString(SchwabJson.Format(to))}{extra}";
        using (var document = await api.GetAsync(query, ct).ConfigureAwait(false))
        {
            items = SchwabJson.Items(document.RootElement).Select(item => item.Clone()).ToList();
        }

        if (items.Count < PageLimit)
        {
            return items;
        }

        if (to - from <= ShortestWindow)
        {
            throw new ConnectorException(
                ConnectorErrorKind.SourceError,
                "Schwab returned as many results as it sends for a one-second window, so some may be missing.");
        }

        var middle = from + ((to - from) / 2);
        var earlier = await WindowAsync(api, path, keys, extra, from, middle, ct).ConfigureAwait(false);
        var later = await WindowAsync(api, path, keys, extra, middle, to, ct).ConfigureAwait(false);
        return [.. earlier, .. later];
    }

    private async IAsyncEnumerable<SyncMessage> TransactionsAsync(
        SchwabApi api,
        ConnectorConfig config,
        StreamSelection selection,
        ReadRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var stream = SchwabStreams.Transactions;
        var incremental = selection.Mode == SyncMode.Incremental;
        var from = (incremental ? SavedCursor(request, TransactionsStream) : null)
            ?? new DateTimeOffset(SchwabSettings.ReadStartDate(config), TimeOnly.MinValue, TimeSpan.Zero);

        var accounts = await api.AccountsAsync(ct).ConfigureAwait(false);
        if (api.TakeRefreshed() is { } signIn)
        {
            yield return new CredentialsRefreshed(signIn);
        }

        yield return new SchemaMessage(stream.Spec);

        var now = _time.GetUtcNow();
        var latest = from;
        var seen = new HashSet<long>();
        for (var start = from; start < now; start += WindowLength)
        {
            var end = start + WindowLength < now ? start + WindowLength : now;
            var rows = new List<(DateTimeOffset Time, object?[] Row)>();
            foreach (var account in accounts)
            {
                var items = await WindowAsync(
                    api,
                    $"accounts/{Uri.EscapeDataString(account.Hash)}/transactions",
                    ("startDate", "endDate"),
                    $"&types={TransactionTypes}",
                    start,
                    end,
                    ct).ConfigureAwait(false);

                foreach (var item in items)
                {
                    if (SchwabJson.Int64(item, "activityId") is not { } id || seen.Add(id))
                    {
                        var row = stream.Row(TransactionOf(account.Number, item));
                        rows.Add((SchwabJson.Timestamp(item, "time")!.Value, row));
                    }
                }
            }

            if (api.TakeRefreshed() is { } tokens)
            {
                yield return new CredentialsRefreshed(tokens);
            }

            rows.Sort((a, b) => a.Time.CompareTo(b.Time));
            foreach (var batch in Batches(rows.Select(entry => entry.Row)))
            {
                yield return new RecordBatch(stream.Spec.Name, batch);
            }

            if (rows.Count > 0 && rows[^1].Time > latest)
            {
                latest = rows[^1].Time;
            }

            if (incremental)
            {
                yield return new StateCheckpoint(stream.Spec.Name, Cursor("time", latest));
            }
        }

        if (!incremental)
        {
            yield return new StateCheckpoint(stream.Spec.Name, EmptyState);
        }
        else if (from >= now)
        {
            yield return new StateCheckpoint(stream.Spec.Name, Cursor("time", latest));
        }
    }

    private async IAsyncEnumerable<SyncMessage> OrdersAsync(SchwabApi api, [EnumeratorCancellation] CancellationToken ct)
    {
        var stream = SchwabStreams.Orders;
        var accounts = await api.AccountsAsync(ct).ConfigureAwait(false);
        yield return new SchemaMessage(stream.Spec);

        var now = _time.GetUtcNow();
        var seen = new HashSet<long>();
        var rows = new List<object?[]>();
        foreach (var account in accounts)
        {
            var items = await WindowAsync(
                api,
                $"accounts/{Uri.EscapeDataString(account.Hash)}/orders",
                ("fromEnteredTime", "toEnteredTime"),
                string.Create(CultureInfo.InvariantCulture, $"&maxResults={PageLimit}"),
                now - OrdersLookback,
                now,
                ct).ConfigureAwait(false);

            foreach (var item in items)
            {
                if (SchwabJson.Int64(item, "orderId") is not { } id || seen.Add(id))
                {
                    rows.Add(stream.Row(OrderOf(account.Number, item)));
                }
            }

            if (api.TakeRefreshed() is { } tokens)
            {
                yield return new CredentialsRefreshed(tokens);
            }
        }

        foreach (var batch in Batches(rows))
        {
            yield return new RecordBatch(stream.Spec.Name, batch);
        }

        yield return new StateCheckpoint(stream.Spec.Name, EmptyState);
    }
}
