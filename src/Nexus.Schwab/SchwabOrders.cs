using System.Globalization;
using System.Text.RegularExpressions;

namespace Nexus.Schwab;

/// <summary>
/// Places, replaces, and cancels orders in a Schwab account. Every call refuses unless the connection's
/// <see cref="SchwabSettings.AllowOrders"/> setting is on. A request is sent once and never retried; a failure
/// after it was sent raises <see cref="ConnectorErrorKind.OutcomeUnknown"/>.
/// </summary>
public sealed partial class SchwabOrders(HttpClient client, TimeProvider? timeProvider = null)
{
    private readonly HttpClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Places an order in the account.</summary>
    /// <returns>Schwab's id for the new order, or null when Schwab accepted it without returning one.</returns>
    public async Task<ActionResult<long?>> PlaceAsync(
        ConnectorConfig config,
        string accountNumber,
        SchwabOrder order,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        var api = Open(config, accountNumber);
        var hash = await HashAsync(api, accountNumber, ct).ConfigureAwait(false);

        using var response = await api.SendActionAsync(HttpMethod.Post, $"accounts/{hash}/orders", order, ct)
            .ConfigureAwait(false);
        return new ActionResult<long?>(OrderId(response), api.Refreshed);
    }

    /// <summary>Replaces a working order with a changed one. Schwab cancels the old order and creates a new one.</summary>
    /// <returns>Schwab's id for the new order, or null when Schwab did not return one.</returns>
    public async Task<ActionResult<long?>> ReplaceAsync(
        ConnectorConfig config,
        string accountNumber,
        long orderId,
        SchwabOrder order,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        var api = Open(config, accountNumber);
        var hash = await HashAsync(api, accountNumber, ct).ConfigureAwait(false);

        using var response = await api.SendActionAsync(HttpMethod.Put, OrderPath(hash, orderId), order, ct)
            .ConfigureAwait(false);
        return new ActionResult<long?>(OrderId(response), api.Refreshed);
    }

    /// <summary>Cancels a working order.</summary>
    public async Task<ActionResult> CancelAsync(
        ConnectorConfig config,
        string accountNumber,
        long orderId,
        CancellationToken ct = default)
    {
        var api = Open(config, accountNumber);
        var hash = await HashAsync(api, accountNumber, ct).ConfigureAwait(false);

        using var response = await api.SendActionAsync(HttpMethod.Delete, OrderPath(hash, orderId), null, ct)
            .ConfigureAwait(false);
        return new ActionResult(api.Refreshed);
    }

    private static async Task<string> HashAsync(SchwabApi api, string accountNumber, CancellationToken ct) =>
        Uri.EscapeDataString(await api.HashForAsync(accountNumber, ct).ConfigureAwait(false));

    private static string OrderPath(string hash, long orderId) =>
        string.Create(CultureInfo.InvariantCulture, $"accounts/{hash}/orders/{orderId}");

    private static long? OrderId(HttpResponseMessage response) =>
        response.Headers.Location is { } location
        && OrderIdPattern().Match(location.OriginalString) is { Success: true } match
        && long.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;

    [GeneratedRegex(@"/orders/(\d+)/?$")]
    private static partial Regex OrderIdPattern();

    private SchwabApi Open(ConnectorConfig config, string accountNumber)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumber);

        return SchwabSettings.ReadAllowOrders(config)
            ? new SchwabApi(_client, _time, config)
            : throw new ConnectorException(
                ConnectorErrorKind.ConfigInvalid,
                "Orders are turned off for this Schwab connection. Turn on Allow orders to place, replace, or cancel them.");
    }
}
