using System.Text.Json;

namespace Nexus;

/// <summary>One stream to read, and how.</summary>
/// <param name="Stream">The <see cref="StreamSpec.Name"/> to read.</param>
/// <param name="Mode">How to read it.</param>
/// <param name="CursorField">The column to track, for an <see cref="SyncMode.Incremental"/> read.</param>
public sealed record StreamSelection(string Stream, SyncMode Mode, string? CursorField = null);

/// <summary>What a source reads.</summary>
/// <param name="Streams">The streams to read.</param>
/// <param name="State">The last saved <see cref="StateCheckpoint.State"/> for each stream, keyed by stream name.</param>
public sealed record ReadRequest(
    IReadOnlyList<StreamSelection> Streams,
    IReadOnlyDictionary<string, JsonElement>? State = null);

/// <summary>How a destination writes a stream.</summary>
public enum WriteMode
{
    /// <summary>Replaces every row, for a full refresh.</summary>
    Overwrite,

    /// <summary>Adds every row.</summary>
    Append,

    /// <summary>Replaces rows that share a primary key, for an incremental read.</summary>
    Upsert,
}

/// <summary>How a destination writes each stream.</summary>
/// <param name="Modes">The write mode for each stream, keyed by stream name.</param>
public sealed record WriteRequest(IReadOnlyDictionary<string, WriteMode> Modes);
