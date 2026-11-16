using DuckDB.NET.Data;
using DuckDB.NET.Native;

namespace Nexus.DuckDb;

internal static class DuckDbErrors
{
    // DuckDB words a lock conflict differently on POSIX and Windows; both include this.
    private const string LockConflict = "Conflicting lock is held in";

    public static ConnectorException ForOpen(DuckDBException ex) =>
        ex.Message.Contains(LockConflict, StringComparison.Ordinal)
            ? new ConnectorException(
                ConnectorErrorKind.Transient,
                "Another process holds the database file. Try again later.",
                ex)
            : new ConnectorException(
                ConnectorErrorKind.ConfigInvalid,
                "The database file could not be opened. Check its path and permissions.",
                ex);

    public static ConnectorException? ForWrite(DuckDBException ex) => ex.ErrorType switch
    {
        DuckDBErrorType.Conversion
            or DuckDBErrorType.OutOfRange
            or DuckDBErrorType.Decimal
            or DuckDBErrorType.MismatchType
            or DuckDBErrorType.Constraint
            or DuckDBErrorType.InvalidInput
            or DuckDBErrorType.ObjectSize => new ConnectorException(
                ConnectorErrorKind.SourceError,
                $"A value from the source does not fit its column. {ex.Message}",
                ex),

        DuckDBErrorType.Transaction
            or DuckDBErrorType.OutOfMemory
            or DuckDBErrorType.Io
            or DuckDBErrorType.Interrupt => new ConnectorException(
                ConnectorErrorKind.Transient,
                "The database could not complete the write. Try again later.",
                ex),

        DuckDBErrorType.Permission => new ConnectorException(
            ConnectorErrorKind.ConfigInvalid,
            "The database file cannot be written. Check its path and permissions.",
            ex),

        _ => null,
    };
}
