using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Controllers;
using Nexus.Service.Storage;

namespace Nexus.Service;

/// <summary>
/// Adds a connection once its settings connect, for a workspace's members and for connect pages alike, and builds the
/// config a connector reads a connection with.
/// </summary>
public sealed class ConnectionMaker(NexusDbContext db, SourceConnectors connectors, ConnectionFiles files, Secrets secrets, TimeProvider time)
{
    /// <summary>
    /// The connection made, or the reason none was. A connector that reads a file takes it as <paramref name="file"/>,
    /// already saved, which becomes the connection's own; on a refusal the file is deleted.
    /// </summary>
    public async Task<(Connection? Connection, string? Problem)> AddAsync(
        int workspaceId,
        string? name,
        string? connectorId,
        IReadOnlyDictionary<string, string?>? given,
        UploadedFile? file,
        CancellationToken ct)
    {
        var (connection, problem) = await MakeAsync(workspaceId, name, connectorId, given, file, ct);
        if (connection is null)
        {
            files.Delete(file?.StoredFile);
        }

        return (connection, problem);
    }

    /// <summary>The config a connector reads the connection with: its settings, its tokens, and its file.</summary>
    public ConnectorConfig ConfigOf(Connection connection) =>
        new(
            secrets.Unprotect(connection.Settings),
            Tokens: secrets.UnprotectTokens(connection.Tokens),
            Files: FilesOf(connectors.Find(connection.Connector), connection.FileName, connection.StoredFile));

    /// <summary>
    /// Replaces the connection's file with <paramref name="file"/> once the connector reads it, deleting the old copy.
    /// Answers why it did not, deleting the new copy instead.
    /// </summary>
    public async Task<string?> ReplaceFileAsync(Connection connection, UploadedFile file, CancellationToken ct)
    {
        if (connectors.Find(connection.Connector) is not { } connector || SourceConnectors.FileFieldOf(connector) is null)
        {
            files.Delete(file.StoredFile);
            return "This connection reads no file.";
        }

        var check = await connector.CheckAsync(ConfigOf(connection) with { Files = FilesOf(connector, file.FileName, file.StoredFile) }, ct);
        if (!check.Succeeded)
        {
            files.Delete(file.StoredFile);
            return check.Message;
        }

        var old = connection.StoredFile;
        connection.FileName = file.FileName;
        connection.StoredFile = file.StoredFile;
        await db.SaveChangesAsync(ct);
        files.Delete(old);
        return null;
    }

    /// <summary>The connection with every setting but its secrets, and the name of its file.</summary>
    public ConnectionView ViewOf(Connection connection)
    {
        var shown = connectors.Find(connection.Connector)?.Spec.Fields
            .Where(f => f.Kind is not (SettingKind.Secret or SettingKind.File))
            .Select(f => f.Name)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var settings = secrets.Unprotect(connection.Settings)
            .Where(setting => shown.Contains(setting.Key))
            .ToDictionary(setting => setting.Key, setting => setting.Value);
        return new ConnectionView(
            connection.Id, connection.Name, connection.Connector, settings, connection.FileName, connection.NeedsSignIn, connection.CreatedAt);
    }

    /// <summary>The streams the connection's source offers now.</summary>
    public async Task<(SourceCatalog? Catalog, string? Problem)> DiscoverAsync(Connection connection, CancellationToken ct)
    {
        if (connectors.Find(connection.Connector) is not { } connector)
        {
            return (null, $"'{connection.Connector}' is not a connector the service reads.");
        }

        try
        {
            return (await connector.DiscoverAsync(ConfigOf(connection), ct), null);
        }
        catch (ConnectorException ex)
        {
            return (null, ex.Message);
        }
    }

    private async Task<(Connection? Connection, string? Problem)> MakeAsync(
        int workspaceId,
        string? name,
        string? connectorId,
        IReadOnlyDictionary<string, string?>? given,
        UploadedFile? file,
        CancellationToken ct)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > Connection.MaxNameLength)
        {
            return (null, $"A connection needs a name of {Connection.MaxNameLength} characters or fewer.");
        }

        if (connectors.Find(connectorId ?? string.Empty) is not { } connector)
        {
            return (null, $"'{connectorId}' is not a connector the service reads.");
        }

        var fileField = SourceConnectors.FileFieldOf(connector);
        if (fileField is null && file is not null)
        {
            return (null, $"{connector.Spec.DisplayName} reads no file.");
        }

        if (fileField is { Required: true } && file is null)
        {
            return (null, $"{fileField.Label} is required.");
        }

        given ??= new Dictionary<string, string?>();
        var settings = connector.Spec.Fields.Where(field => field.Kind != SettingKind.File).ToDictionary(
            field => field.Name,
            field => given.TryGetValue(field.Name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null);
        if (connector.Spec.Fields.FirstOrDefault(field => field.Required && field.Kind != SettingKind.File && settings[field.Name] is null) is { } missing)
        {
            return (null, $"{missing.Label} is required.");
        }

        var check = await connector.CheckAsync(new ConnectorConfig(settings, Files: FilesOf(connector, file?.FileName, file?.StoredFile)), ct);
        if (!check.Succeeded)
        {
            return (null, check.Message);
        }

        var connection = new Connection
        {
            WorkspaceId = workspaceId,
            Name = trimmed,
            Connector = connector.Spec.Id,
            Settings = secrets.Protect(settings),
            FileName = file?.FileName,
            StoredFile = file?.StoredFile,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.Connections.Add(connection);
        await db.SaveChangesAsync(ct);
        return (connection, null);
    }

    private Dictionary<string, ConnectorFile>? FilesOf(ISourceConnector? connector, string? fileName, string? stored) =>
        connector is not null && SourceConnectors.FileFieldOf(connector) is { } field && stored is not null
            ? new Dictionary<string, ConnectorFile> { [field.Name] = files.Open(fileName ?? stored, stored) }
            : null;
}

/// <summary>Adds syncs from a connection's streams into a destination, and queues the first run of each.</summary>
public sealed partial class SyncMaker(NexusDbContext db, ConnectionMaker connections, PendingRuns queue, TimeProvider time)
{
    public const string TableRule = "A table name starts with a lower-case letter and holds up to 63 lower-case letters, digits, and underscores.";

    /// <summary>The syncs made, or the reason none were.</summary>
    public async Task<(List<Sync>? Syncs, string? Problem)> AddAsync(
        Connection connection,
        Destination destination,
        IReadOnlyList<SyncStreamRequest>? picked,
        CancellationToken ct)
    {
        if (picked is null || picked.Count == 0)
        {
            return (null, "Pick at least one stream.");
        }

        var (catalog, problem) = await connections.DiscoverAsync(connection, ct);
        if (catalog is null)
        {
            return (null, problem);
        }

        var taken = (await db.Syncs.Where(s => s.DestinationId == destination.Id).Select(s => s.Table).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var syncs = new List<Sync>();
        foreach (var stream in picked)
        {
            if ((StreamProblem(stream, catalog) ?? TableProblem(stream.Table, taken)) is { } refused)
            {
                return (null, refused);
            }

            taken.Add(stream.Table!);
            syncs.Add(new Sync
            {
                ConnectionId = connection.Id,
                Connection = connection,
                DestinationId = destination.Id,
                Destination = destination,
                Stream = stream.Stream!,
                Table = stream.Table!,
                Mode = stream.Mode,
                CursorField = Cursor(stream),
                Status = SyncStatus.Queued,
                CreatedAt = time.GetUtcNow().UtcDateTime,
            });
        }

        db.Syncs.AddRange(syncs);
        await db.SaveChangesAsync(ct);
        foreach (var sync in syncs)
        {
            queue.Enqueue(new PendingRun(sync.Id, 1));
        }

        return (syncs, null);
    }

    /// <summary>
    /// Points the sync at the destination, table, and read mode asked for, and queues a run that reads from the start,
    /// since the new table holds none of its rows. Answers why nothing changed, if it did not.
    /// </summary>
    public async Task<string?> EditAsync(Sync sync, Destination destination, SyncStreamRequest edit, CancellationToken ct)
    {
        var cursor = Cursor(edit);
        var readChanged = edit.Mode != sync.Mode || cursor != sync.CursorField;
        if (destination.Id == sync.DestinationId && edit.Table == sync.Table && !readChanged)
        {
            return "Nothing changed. Pick another destination, table name, or way to read it.";
        }

        var taken = (await db.Syncs.Where(s => s.DestinationId == destination.Id && s.Id != sync.Id).Select(s => s.Table).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        if (TableProblem(edit.Table, taken) is { } refused)
        {
            return refused;
        }

        if (readChanged)
        {
            var (catalog, problem) = await connections.DiscoverAsync(sync.Connection!, ct);
            if (catalog is null)
            {
                return problem;
            }

            if (StreamProblem(edit, catalog) is { } unreadable)
            {
                return unreadable;
            }
        }

        sync.DestinationId = destination.Id;
        sync.Destination = destination;
        sync.Table = edit.Table!;
        sync.Mode = edit.Mode;
        sync.CursorField = cursor;
        sync.Checkpoint = null;
        sync.Status = SyncStatus.Queued;
        await db.SaveChangesAsync(ct);
        queue.Enqueue(new PendingRun(sync.Id, 1));
        return null;
    }

    private static string? Cursor(SyncStreamRequest stream) =>
        stream.Mode == SyncMode.Incremental && !string.IsNullOrWhiteSpace(stream.CursorField) ? stream.CursorField : null;

    private static string? TableProblem(string? table, HashSet<string> taken) =>
        table is null || !TablePattern().IsMatch(table) ? $"'{table}': {TableRule}"
        : taken.Contains(table) ? $"The destination already has a sync into '{table}'. Pick another table name."
        : null;

    private static string? StreamProblem(SyncStreamRequest stream, SourceCatalog catalog)
    {
        if (catalog.Streams.FirstOrDefault(s => s.Name == stream.Stream) is not { } spec)
        {
            return $"The connection has no stream named '{stream.Stream}'.";
        }

        if (!spec.SyncModes.Contains(stream.Mode))
        {
            return $"'{spec.Name}' cannot be read {(stream.Mode == SyncMode.Incremental ? "incrementally" : "in full")}.";
        }

        return stream.Mode == SyncMode.Incremental && !string.IsNullOrWhiteSpace(stream.CursorField) && !spec.CursorFields.Contains(stream.CursorField)
            ? $"'{spec.Name}' cannot track '{stream.CursorField}'. It can track: {string.Join(", ", spec.CursorFields)}."
            : null;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    private static partial Regex TablePattern();
}
