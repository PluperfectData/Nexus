using System.Diagnostics;
using System.Globalization;

namespace Nexus.Sql;

internal static class SqlValues
{
    public static object Convert(object value, ColumnSpec column) => column.Type switch
    {
        ColumnType.String or ColumnType.Json =>
            value as string ?? System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        ColumnType.Int64 => System.Convert.ToInt64(value, CultureInfo.InvariantCulture),
        ColumnType.Decimal => System.Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        ColumnType.Float64 => System.Convert.ToDouble(value, CultureInfo.InvariantCulture),
        ColumnType.Boolean => System.Convert.ToBoolean(value, CultureInfo.InvariantCulture),
        ColumnType.Date => value switch
        {
            DateOnly date => date,
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            _ => throw Unexpected(value),
        },
        ColumnType.Timestamp => value switch
        {
            DateTimeOffset timestamp => timestamp.ToUniversalTime(),
            DateTime { Kind: DateTimeKind.Local } local => new DateTimeOffset(local.ToUniversalTime()),
            DateTime utc => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)),
            _ => throw Unexpected(value),
        },
        ColumnType.DateTime => value is DateTime dateTime
            ? DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified)
            : throw Unexpected(value),
        ColumnType.Time => value switch
        {
            TimeOnly time => time,
            TimeSpan span when span >= TimeSpan.Zero && span < TimeSpan.FromDays(1) => TimeOnly.FromTimeSpan(span),
            TimeSpan => throw new OverflowException("The time is outside one day."),
            _ => throw Unexpected(value),
        },
        ColumnType.Binary => value as byte[] ?? throw Unexpected(value),
        _ => throw new UnreachableException($"Unknown column type {column.Type}."),
    };

    private static InvalidCastException Unexpected(object value) =>
        new($"The driver returned a {value.GetType().Name}.");
}
