using System.Globalization;

namespace Nexus.Sql;

internal static class SettingValues
{
    public static string Required(ConnectorConfig config, string key) =>
        Optional(config, key)
        ?? throw new ConnectorException(ConnectorErrorKind.ConfigInvalid, $"The '{key}' setting is required.");

    public static string? Optional(ConnectorConfig config, string key) =>
        config.Values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    public static string Password(ConnectorConfig config) =>
        config.Values.TryGetValue(SqlSettings.Password, out var value) ? value ?? string.Empty : string.Empty;

    public static int? Port(ConnectorConfig config)
    {
        if (Optional(config, SqlSettings.Port) is not { } text)
        {
            return null;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? port
            : throw new ConnectorException(
                ConnectorErrorKind.ConfigInvalid,
                $"The '{SqlSettings.Port}' setting must be a number from 1 to 65535.");
    }

    public static string? Choice(ConnectorConfig config, string key, IReadOnlyList<string> choices)
    {
        if (Optional(config, key) is not { } text)
        {
            return null;
        }

        return choices.FirstOrDefault(choice => string.Equals(choice, text, StringComparison.OrdinalIgnoreCase))
            ?? throw new ConnectorException(
                ConnectorErrorKind.ConfigInvalid,
                $"The '{key}' setting must be one of: {string.Join(", ", choices)}.");
    }

    public static bool Flag(ConnectorConfig config, string key)
    {
        if (Optional(config, key) is not { } text)
        {
            return false;
        }

        return bool.TryParse(text, out var flag)
            ? flag
            : throw new ConnectorException(ConnectorErrorKind.ConfigInvalid, $"The '{key}' setting must be true or false.");
    }
}
