using System.Diagnostics.CodeAnalysis;

namespace Nexus;

/// <summary>The kind of value a column holds, named with the CLR type a row carries for it.</summary>
[SuppressMessage("Naming", "CA1720", Justification = "Members name column types, as System.Data.DbType does.")]
public enum ColumnType
{
    /// <summary>A <see cref="string"/>.</summary>
    String,

    /// <summary>A <see cref="long"/>.</summary>
    Int64,

    /// <summary>A <see cref="decimal"/>, with <see cref="ColumnSpec.Precision"/> and <see cref="ColumnSpec.Scale"/>.</summary>
    Decimal,

    /// <summary>A <see cref="double"/>.</summary>
    Float64,

    /// <summary>A <see cref="bool"/>.</summary>
    Boolean,

    /// <summary>A <see cref="DateOnly"/>.</summary>
    Date,

    /// <summary>A <see cref="DateTimeOffset"/> at UTC.</summary>
    Timestamp,

    /// <summary>A <see cref="TimeOnly"/>.</summary>
    Time,

    /// <summary>JSON text in a <see cref="string"/>.</summary>
    Json,

    /// <summary>A <see cref="byte"/> array.</summary>
    Binary,
}

/// <summary>One column in a stream.</summary>
/// <param name="Name">The column's name.</param>
/// <param name="Type">The kind of value it holds.</param>
/// <param name="Nullable">Whether a row may hold null.</param>
/// <param name="Precision">The total digits of a <see cref="ColumnType.Decimal"/>.</param>
/// <param name="Scale">The digits after the point of a <see cref="ColumnType.Decimal"/>.</param>
public sealed record ColumnSpec(string Name, ColumnType Type, bool Nullable, int Precision = 0, int Scale = 0);

/// <summary>How a stream is read.</summary>
public enum SyncMode
{
    /// <summary>Every row, every time.</summary>
    FullRefresh,

    /// <summary>Only rows past the saved cursor.</summary>
    Incremental,
}

/// <summary>One stream a source offers, such as a table, a sheet, or a Stripe object.</summary>
/// <param name="Name">The stream's name, unique within its source.</param>
/// <param name="Columns">The columns, in the order rows carry them.</param>
/// <param name="PrimaryKey">The columns that identify a row, or none.</param>
/// <param name="CursorFields">The columns an incremental read can track.</param>
/// <param name="SyncModes">The modes the stream supports.</param>
public sealed record StreamSpec(
    string Name,
    IReadOnlyList<ColumnSpec> Columns,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<string> CursorFields,
    IReadOnlyList<SyncMode> SyncModes);

/// <summary>Every stream a source offers.</summary>
/// <param name="Streams">The streams.</param>
public sealed record SourceCatalog(IReadOnlyList<StreamSpec> Streams);
