namespace Nexus.Sql;

/// <summary>One row of the dialect's columns query.</summary>
internal sealed record CatalogColumn(
    string Schema,
    string Table,
    string Name,
    string DataType,
    string Detail,
    bool Nullable,
    int? Precision,
    int? Scale);

internal sealed record SourceColumn(ColumnSpec Spec, string Expression, bool IsCursor)
{
    public const int UnboundedPrecision = 38;
    public const int UnboundedScale = 18;

    public static SourceColumn Of(CatalogColumn column, ColumnType type, string expression, int precision = 0, int scale = 0) =>
        new(
            new ColumnSpec(column.Name, type, column.Nullable, precision, scale),
            expression,
            type is ColumnType.Int64 or ColumnType.Decimal or ColumnType.Date or ColumnType.Timestamp or ColumnType.DateTime);

    // A decimal with no usable precision is rounded to Decimal(38, 18), so it cannot be a cursor.
    public static SourceColumn Decimal(CatalogColumn column, string quoted) =>
        column is { Precision: >= 1 and <= 38 and var precision, Scale: >= 0 and var scale } && scale <= precision
            ? Of(column, ColumnType.Decimal, quoted, precision, scale)
            : new SourceColumn(
                new ColumnSpec(column.Name, ColumnType.Decimal, column.Nullable, UnboundedPrecision, UnboundedScale),
                $"ROUND({quoted}, 18)",
                IsCursor: false);
}

internal sealed record DiscoveredStream(StreamSpec Spec, string Table, IReadOnlyList<SourceColumn> Columns);
