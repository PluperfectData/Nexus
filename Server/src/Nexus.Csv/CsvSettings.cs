namespace Nexus.Csv;

internal sealed record CsvSettings(ConnectorFile File, string Stream, bool Header, char Delimiter)
{
    public static readonly string[] Delimiters = ["Comma", "Semicolon", "Tab", "Pipe"];

    public static CsvSettings From(ConnectorConfig config)
    {
        var file = config.Files is { } files && files.TryGetValue(CsvSource.FileSetting, out var upload)
            ? upload
            : throw new ConnectorException(ConnectorErrorKind.ConfigInvalid, "A CSV file is required.");

        var stream = Value(config, CsvSource.StreamSetting)
            ?? throw new ConnectorException(ConnectorErrorKind.ConfigInvalid, $"The '{CsvSource.StreamSetting}' setting is required.");

        var header = Value(config, CsvSource.HeaderSetting) switch
        {
            null => true,
            var text when bool.TryParse(text, out var flag) => flag,
            _ => throw new ConnectorException(
                ConnectorErrorKind.ConfigInvalid,
                $"The '{CsvSource.HeaderSetting}' setting must be true or false."),
        };

        var delimiter = Value(config, CsvSource.DelimiterSetting) switch
        {
            null => ',',
            var text when text.Equals("Comma", StringComparison.OrdinalIgnoreCase) => ',',
            var text when text.Equals("Semicolon", StringComparison.OrdinalIgnoreCase) => ';',
            var text when text.Equals("Tab", StringComparison.OrdinalIgnoreCase) => '\t',
            var text when text.Equals("Pipe", StringComparison.OrdinalIgnoreCase) => '|',
            _ => throw new ConnectorException(
                ConnectorErrorKind.ConfigInvalid,
                $"The '{CsvSource.DelimiterSetting}' setting must be one of: {string.Join(", ", Delimiters)}."),
        };

        return new CsvSettings(file, stream, header, delimiter);
    }

    private static string? Value(ConnectorConfig config, string key) =>
        config.Values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
