using System.Globalization;

namespace Nexus.Schwab;

/// <summary>A price history candle size, and the query values Schwab takes for it.</summary>
internal sealed record CandleSize(string Label, string PeriodType, string FrequencyType, int Frequency);

/// <summary>The settings a Schwab connection uses, shared by <see cref="SchwabSource"/> and <see cref="SchwabOrders"/>.</summary>
public static class SchwabSettings
{
    /// <summary>The settings key for the date, as <c>yyyy-MM-dd</c>, the first transactions and candles read start from.</summary>
    public const string StartDate = "startDate";

    /// <summary>The settings key for whether <see cref="SchwabOrders"/> may place, replace, and cancel orders. Defaults to false.</summary>
    public const string AllowOrders = "allowOrders";

    /// <summary>The settings key for symbols, comma-separated, that quotes and price history read beside the account's holdings.</summary>
    public const string Symbols = "symbols";

    /// <summary>The settings key for the price history candle size, such as <c>Daily</c> or <c>5 minutes</c>. Defaults to daily.</summary>
    public const string Candles = "candles";

    /// <summary>The settings key for indexes, comma-separated, such as <c>$SPX</c>, that movers are read for.</summary>
    public const string Movers = "movers";

    private const string DefaultCandles = "Daily";

    internal static IReadOnlyList<CandleSize> CandleSizes { get; } =
    [
        new("1 minute", "day", "minute", 1),
        new("5 minutes", "day", "minute", 5),
        new("10 minutes", "day", "minute", 10),
        new("15 minutes", "day", "minute", 15),
        new("30 minutes", "day", "minute", 30),
        new(DefaultCandles, "year", "daily", 1),
        new("Weekly", "year", "weekly", 1),
        new("Monthly", "year", "monthly", 1),
    ];

    internal static ConnectorSpec Spec { get; } = new(
        Id: "schwab",
        DisplayName: "Charles Schwab",
        Description: "Reads balances, positions, transactions, orders, and market data from a Schwab sign-in.",
        Version: typeof(SchwabSettings).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        Auth: AuthKind.OAuth,
        Fields:
        [
            new SettingField(StartDate, "Read history from (yyyy-MM-dd)", SettingKind.Text, Required: true),
            new SettingField(Symbols, "Symbols (comma-separated)", SettingKind.Text, Required: false),
            new SettingField(
                Candles,
                "Candle size",
                SettingKind.Choice,
                Required: false,
                Choices: [.. CandleSizes.Select(size => size.Label)]),
            new SettingField(Movers, "Movers indexes (comma-separated, such as $SPX)", SettingKind.Text, Required: false),
            new SettingField(AllowOrders, "Allow orders", SettingKind.Boolean, Required: false),
        ],
        OAuth: new OAuthSpec(SchwabApi.AuthorizeUri, SchwabApi.TokenUri, Scopes: []),
        // Schwab's refresh token lasts 7 days from sign-in, and a refresh does not extend it.
        SignIn: SignInLife.After(TimeSpan.FromDays(7), TimeSpan.FromDays(1)));

    internal static CandleSize ReadCandles(ConnectorConfig config)
    {
        var label = Value(config, Candles) ?? DefaultCandles;
        return CandleSizes.FirstOrDefault(size => size.Label == label)
            ?? throw new ConnectorException(
                ConnectorErrorKind.ConfigInvalid,
                $"The '{Candles}' setting must be one of: {string.Join(", ", CandleSizes.Select(size => size.Label))}.");
    }

    internal static IReadOnlyList<string> ReadSymbols(ConnectorConfig config) => List(config, Symbols);

    internal static IReadOnlyList<string> ReadMovers(ConnectorConfig config)
    {
        var indexes = List(config, Movers);
        return indexes.FirstOrDefault(index => !index.All(c => char.IsAsciiLetterOrDigit(c) || c is '$' or '_')) is { } bad
            ? throw new ConnectorException(
                ConnectorErrorKind.ConfigInvalid,
                $"'{bad}' in the '{Movers}' setting is not an index name such as $SPX.")
            : indexes;
    }

    internal static DateOnly ReadStartDate(ConnectorConfig config)
    {
        var text = Value(config, StartDate)
            ?? throw new ConnectorException(ConnectorErrorKind.ConfigInvalid, $"The '{StartDate}' setting is required.");

        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new ConnectorException(
                ConnectorErrorKind.ConfigInvalid,
                $"The '{StartDate}' setting must be a date such as 2024-01-31.");
    }

    internal static bool ReadAllowOrders(ConnectorConfig config)
    {
        if (Value(config, AllowOrders) is not { } text)
        {
            return false;
        }

        return bool.TryParse(text, out var allow)
            ? allow
            : throw new ConnectorException(ConnectorErrorKind.ConfigInvalid, $"The '{AllowOrders}' setting must be true or false.");
    }

    // Split on commas only: an option symbol such as "AAPL  261218C00150000" holds spaces.
    private static List<string> List(ConnectorConfig config, string key) =>
        Value(config, key)?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList()
        ?? [];

    private static string? Value(ConnectorConfig config, string key) =>
        config.Values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
