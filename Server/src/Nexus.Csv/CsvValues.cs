using System.Globalization;

namespace Nexus.Csv;

internal static class CsvValues
{
    // A .NET decimal parses up to 28 significant digits without rounding.
    public const int MaxDecimalDigits = 28;

    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd HH:mm",
    ];

    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF'Z'",
    ];

    public static bool IsBoolean(string text) =>
        text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("false", StringComparison.OrdinalIgnoreCase);

    public static bool TryInt64(string text, out long value) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    public static bool TryDecimal(string text, out decimal value, out int integerDigits, out int scale)
    {
        value = 0;
        var span = text.AsSpan();
        if (span is ['-' or '+', ..])
        {
            span = span[1..];
        }

        var point = span.IndexOf('.');
        var whole = point < 0 ? span : span[..point];
        var fraction = point < 0 ? ReadOnlySpan<char>.Empty : span[(point + 1)..];
        integerDigits = whole.TrimStart('0').Length;
        scale = fraction.Length;

        return whole.Length + fraction.Length > 0
            && !whole.ContainsAnyExceptInRange('0', '9')
            && !fraction.ContainsAnyExceptInRange('0', '9')
            && integerDigits + scale <= MaxDecimalDigits
            && decimal.TryParse(
                text,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out value);
    }

    public static bool TryFloat64(string text, out double value) =>
        double.TryParse(
            text,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture,
            out value);

    public static bool TryDate(string text, out DateOnly value) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    public static bool TryTimestamp(string text, out DateTimeOffset value)
    {
        var parsed = DateTimeOffset.TryParseExact(
            text,
            TimestampFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out value);
        value = value.ToUniversalTime();
        return parsed;
    }

    public static bool TryDateTime(string text, out DateTime value) =>
        DateTime.TryParseExact(text, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    public static object? Parse(string? text, ColumnType type)
    {
        if (text is null)
        {
            return null;
        }

        return type switch
        {
            ColumnType.String => text,
            ColumnType.Boolean when IsBoolean(text) => text.Equals("true", StringComparison.OrdinalIgnoreCase),
            ColumnType.Int64 when TryInt64(text, out var number) => number,
            ColumnType.Decimal when TryDecimal(text, out var number, out _, out _) => number,
            ColumnType.Float64 when TryFloat64(text, out var number) => number,
            ColumnType.Date when TryDate(text, out var date) => date,
            ColumnType.Timestamp when TryTimestamp(text, out var timestamp) => timestamp,
            ColumnType.DateTime when TryDateTime(text, out var dateTime) => dateTime,
            _ => throw new FormatException(),
        };
    }
}
