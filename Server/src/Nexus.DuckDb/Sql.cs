using System.Globalization;

namespace Nexus.DuckDb;

internal static class Sql
{
    public static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public static string ColumnList(IEnumerable<ColumnSpec> columns) =>
        string.Join(", ", columns.Select(column => Quote(column.Name)));

    public static string ColumnDefinitions(IEnumerable<ColumnSpec> columns) =>
        string.Join(", ", columns.Select(column => $"{Quote(column.Name)} {TypeOf(column)}"));

    public static string TypeOf(ColumnSpec column) => column.Type switch
    {
        ColumnType.String => "VARCHAR",
        ColumnType.Int64 => "BIGINT",
        ColumnType.Decimal => DecimalOf(column),
        ColumnType.Float64 => "DOUBLE",
        ColumnType.Boolean => "BOOLEAN",
        ColumnType.Date => "DATE",
        ColumnType.Timestamp => "TIMESTAMPTZ",
        ColumnType.DateTime => "TIMESTAMP",
        ColumnType.Time => "TIME",
        ColumnType.Json => "JSON",
        ColumnType.Binary => "BLOB",
        _ => throw new ArgumentOutOfRangeException(nameof(column), column.Type, "Unknown column type."),
    };

    public static Type ClrTypeOf(ColumnType type) => type switch
    {
        ColumnType.String or ColumnType.Json => typeof(string),
        ColumnType.Int64 => typeof(long),
        ColumnType.Decimal => typeof(decimal),
        ColumnType.Float64 => typeof(double),
        ColumnType.Boolean => typeof(bool),
        ColumnType.Date => typeof(DateOnly),
        ColumnType.Timestamp => typeof(DateTimeOffset),
        ColumnType.DateTime => typeof(DateTime),
        ColumnType.Time => typeof(TimeOnly),
        ColumnType.Binary => typeof(byte[]),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown column type."),
    };

    // DuckDB's DECIMAL holds at most 38 digits; a precision of 0 means the source did not declare one.
    private static string DecimalOf(ColumnSpec column)
    {
        var precision = column.Precision == 0 ? 38 : column.Precision;
        if (precision is < 1 or > 38 || column.Scale < 0 || column.Scale > precision)
        {
            throw new ConnectorException(
                ConnectorErrorKind.SourceError,
                $"Column '{column.Name}' declares DECIMAL({column.Precision}, {column.Scale}), which DuckDB cannot hold.");
        }

        return string.Create(CultureInfo.InvariantCulture, $"DECIMAL({precision}, {column.Scale})");
    }
}
