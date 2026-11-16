using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Nexus;

/// <summary>One message a source yields while reading.</summary>
public abstract record SyncMessage;

/// <summary>Starts a stream, before any of its records.</summary>
/// <param name="Stream">The stream's columns and keys as read.</param>
public sealed record SchemaMessage(StreamSpec Stream) : SyncMessage;

/// <summary>Rows from one stream, each in the order of the stream's columns.</summary>
/// <param name="Stream">The stream's name.</param>
/// <param name="Rows">The rows, which the source never reuses after yielding them.</param>
public sealed record RecordBatch(string Stream, IReadOnlyList<object?[]> Rows) : SyncMessage;

/// <summary>Progress the host saves, so a later read resumes from here.</summary>
/// <param name="Stream">The stream's name.</param>
/// <param name="State">Progress in the source's own shape, passed back in <see cref="ReadRequest.State"/>.</param>
public sealed record StateCheckpoint(string Stream, JsonElement State) : SyncMessage;

/// <summary>New OAuth tokens the host stores in place of the old ones.</summary>
/// <param name="Tokens">The new tokens.</param>
public sealed record CredentialsRefreshed(OAuthTokens Tokens) : SyncMessage;

/// <summary>A log line from the connector.</summary>
/// <param name="Level">The line's severity.</param>
/// <param name="Text">The line.</param>
public sealed record LogMessage(LogLevel Level, string Text) : SyncMessage;
