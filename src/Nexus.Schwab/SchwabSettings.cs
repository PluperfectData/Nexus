using System.Globalization;

namespace Nexus.Schwab;

/// <summary>The settings a Schwab connection uses, shared by <see cref="SchwabSource"/> and <see cref="SchwabOrders"/>.</summary>
public static class SchwabSettings
{
    /// <summary>The settings key for the date, as <c>yyyy-MM-dd</c>, the first transactions read starts from.</summary>
    public const string StartDate = "startDate";

    /// <summary>The settings key for whether <see cref="SchwabOrders"/> may place, replace, and cancel orders. Defaults to false.</summary>
    public const string AllowOrders = "allowOrders";

    internal static ConnectorSpec Spec { get; } = new(
        Id: "schwab",
        DisplayName: "Charles Schwab",
        Version: typeof(SchwabSettings).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        Auth: AuthKind.OAuth,
        Fields:
        [
            new SettingField(StartDate, "Read transactions from (yyyy-MM-dd)", SettingKind.Text, Required: true),
            new SettingField(AllowOrders, "Allow orders", SettingKind.Boolean, Required: false),
        ],
        OAuth: new OAuthSpec(SchwabApi.AuthorizeUri, SchwabApi.TokenUri, Scopes: []));

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

    private static string? Value(ConnectorConfig config, string key) =>
        config.Values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
