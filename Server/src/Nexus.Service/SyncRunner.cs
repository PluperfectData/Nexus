using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexus.Push;
using Nexus.Service.Storage;

namespace Nexus.Service;

/// <summary>How long the service waits before each retry of a run that failed for a reason that may pass.</summary>
/// <param name="Delays">The wait before the second try, the third, and so on; their count is the number of retries.</param>
public sealed record RetryPolicy(IReadOnlyList<TimeSpan> Delays)
{
    public static RetryPolicy Default { get; } = new([TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)]);
}

/// <summary>Runs one sync: reads its stream from its connection and pushes the rows to its destination.</summary>
public sealed partial class SyncRunner(
    NexusDbContext db,
    SourceConnectors connectors,
    ConnectionMaker connections,
    OAuthApps apps,
    PushDestination destination,
    Secrets secrets,
    RetryPolicy retry,
    TimeProvider time,
    OperatorLog log,
    ILogger<SyncRunner> logger)
{
    public const string NoDestination = "Its destination was removed. Pick a new one to send it to.";

    public const string Unexpected = "The run stopped on an error the service did not expect. The Log page has the error.";

    /// <summary>
    /// Runs the sync and records how it went, on the run and on the sync. Answers how long to wait before trying again,
    /// or null when the run succeeded, failed for good, or found no sync.
    /// </summary>
    public async Task<TimeSpan?> RunAsync(int syncId, int attempt, CancellationToken ct)
    {
        var sync = await db.Syncs
            .Include(s => s.Connection).ThenInclude(c => c!.Workspace)
            .Include(s => s.Destination).ThenInclude(d => d!.App)
            .FirstOrDefaultAsync(s => s.Id == syncId, ct);
        if (sync is null)
        {
            return null;
        }

        var run = new Run { SyncId = sync.Id, Attempt = attempt, Status = RunStatus.Running, StartedAt = Now() };
        db.Runs.Add(run);
        sync.Status = SyncStatus.Running;
        await db.SaveChangesAsync(ct);

        try
        {
            await PushAsync(sync, run, ct);
            run.Status = RunStatus.Succeeded;
            sync.Status = SyncStatus.Ready;
            sync.Connection!.NeedsSignIn = false;
            log.Info($"{Named(sync)} sent {LogWords.Rows(run.Rows)} from {LogWords.Connection(sync.Connection, connectors)}, stream {sync.Stream}, into {Into(sync)}.", Place(sync));
            return await EndAsync(run, null, ct);
        }
        catch (ConnectorException ex)
        {
            var connection = sync.Connection!;
            if (ex.Kind == ConnectorErrorKind.AuthExpired && !connection.NeedsSignIn)
            {
                connection.NeedsSignIn = true;
                log.Warn($"{LogWords.Connection(connection, connectors)} needs a new sign-in. Its syncs stop until someone signs in again from the workspace page.", LogPlace.On(connection));
            }

            Fail(run, ex.Kind, ex.Message);
            var retried = (ex.Kind is ConnectorErrorKind.Transient or ConnectorErrorKind.RateLimited) && attempt <= retry.Delays.Count;
            sync.Status = retried ? SyncStatus.Queued : SyncStatus.Failed;
            var wait = retried ? Max(retry.Delays[attempt - 1], ex.RetryAfter) : (TimeSpan?)null;
            if (wait is { } delay)
            {
                log.Warn($"{Named(sync)} failed: {ex.Message} It tries again {LogWords.After(delay)} (try {attempt + 1} of {retry.Delays.Count + 1}).", Place(sync));
            }
            else
            {
                log.Error($"{Named(sync)} failed: {ex.Message}", Place(sync));
            }

            return await EndAsync(run, wait, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogFailure(logger, ex, sync.Id);
            Fail(run, null, Unexpected);
            sync.Status = SyncStatus.Failed;
            log.Error($"{Named(sync)} stopped on an error the service did not expect: {ex.GetType().Name}: {ex.Message}", Place(sync));
            return await EndAsync(run, null, CancellationToken.None);
        }
    }

    private async Task PushAsync(Sync sync, Run run, CancellationToken ct)
    {
        var connection = sync.Connection!;
        var source = connectors.Find(connection.Connector)
            ?? throw new ConnectorException(ConnectorErrorKind.ConfigInvalid, $"'{connection.Connector}' is not a connector the service reads.");
        var target = sync.Destination
            ?? throw new ConnectorException(ConnectorErrorKind.ConfigInvalid, NoDestination);
        if (source.Spec.Auth == AuthKind.OAuth && await apps.ProblemForAsync(connection.Connector, connection.WorkspaceId, ct) is { } missing)
        {
            throw new ConnectorException(ConnectorErrorKind.ConfigInvalid, missing);
        }

        var sourceConfig = await connections.ConfigOfAsync(connection, ct);
        var destinationConfig = new ConnectorConfig(new Dictionary<string, string?>
        {
            [PushDestination.UrlSetting] = target.Url,
            [PushDestination.KeySetting] = secrets.UnprotectText(target.Key),
            [PushDestination.OriginSetting] = OriginOf(sync, source),
        });

        var incremental = sync.Mode == SyncMode.Incremental;
        var state = incremental && sync.Checkpoint is { } saved
            ? new Dictionary<string, JsonElement> { [sync.Stream] = JsonSerializer.Deserialize<JsonElement>(saved) }
            : null;
        var read = source.ReadAsync(
            sourceConfig,
            new ReadRequest([new StreamSelection(sync.Stream, sync.Mode, sync.CursorField)], state),
            ct);

        // Filled in when the stream's schema arrives, before the destination reads it, since the write mode depends on
        // whether the stream has a primary key.
        var modes = new Dictionary<string, WriteMode>();
        await foreach (var checkpoint in destination.WriteAsync(destinationConfig, new WriteRequest(modes), Renamed(read, sync, run, modes, ct), ct))
        {
            if (incremental)
            {
                sync.Checkpoint = checkpoint.State.GetRawText();
                await db.SaveChangesAsync(ct);
            }
        }
    }

    // The table is named for the sync, not for the stream, which may be a database's schema.table. Refreshed tokens are
    // saved as they come. The connector's log lines go to the operator log, or to the developer log alone below Information.
    private async IAsyncEnumerable<SyncMessage> Renamed(
        IAsyncEnumerable<SyncMessage> messages,
        Sync sync,
        Run run,
        Dictionary<string, WriteMode> modes,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var message in messages.WithCancellation(ct))
        {
            switch (message)
            {
                case SchemaMessage schema:
                    modes[sync.Table] = sync.Mode == SyncMode.FullRefresh ? WriteMode.Overwrite
                        : schema.Stream.PrimaryKey.Count > 0 ? WriteMode.Upsert
                        : WriteMode.Append;
                    yield return new SchemaMessage(schema.Stream with { Name = sync.Table });
                    break;

                case RecordBatch batch:
                    run.Rows += batch.Rows.Count;
                    yield return new RecordBatch(sync.Table, batch.Rows);
                    break;

                case StateCheckpoint checkpoint:
                    yield return new StateCheckpoint(sync.Table, checkpoint.State);
                    break;

                case CredentialsRefreshed refreshed:
                    sync.Connection!.Tokens = secrets.ProtectTokens(refreshed.Tokens);
                    await db.SaveChangesAsync(ct);
                    break;

                case LogMessage line when line.Level >= LogLevel.Information:
                    var said = $"{Named(sync)}: {line.Text}";
                    if (line.Level >= LogLevel.Error)
                    {
                        log.Error(said, Place(sync));
                    }
                    else if (line.Level == LogLevel.Warning)
                    {
                        log.Warn(said, Place(sync));
                    }
                    else
                    {
                        log.Info(said, Place(sync));
                    }

                    break;

                case LogMessage line:
                    LogConnector(logger, line.Level, sync.Id, line.Text);
                    break;
            }
        }
    }

    private static string Named(Sync sync) => $"Sync {sync.Table}";

    private static string Into(Sync sync) => LogWords.Destination(sync.Destination!, sync.Destination!.App!.Name);

    private static LogPlace Place(Sync sync) => LogPlace.On(sync, sync.Connection!);

    /// <summary>What the push API is told of where a table's rows come from.</summary>
    internal static string OriginOf(Sync sync, ISourceConnector source) =>
        $"Nexus workspace {sync.Connection!.Workspace!.Name}, connection {sync.Connection.Name} ({source.Spec.DisplayName}), stream {sync.Stream}";

    private static void Fail(Run run, ConnectorErrorKind? kind, string message)
    {
        run.Status = RunStatus.Failed;
        run.ErrorKind = kind;
        run.Error = message.Length > Run.MaxErrorLength ? message[..Run.MaxErrorLength] : message;
    }

    private async Task<TimeSpan?> EndAsync(Run run, TimeSpan? wait, CancellationToken ct)
    {
        run.EndedAt = Now();
        await db.SaveChangesAsync(ct);
        return wait;
    }

    private static TimeSpan Max(TimeSpan delay, TimeSpan? asked) => asked is { } after && after > delay ? after : delay;

    private DateTime Now() => time.GetUtcNow().UtcDateTime;

    [LoggerMessage(Level = LogLevel.Error, Message = "Sync {SyncId} stopped on an unexpected error.")]
    private static partial void LogFailure(ILogger logger, Exception ex, int syncId);

    [LoggerMessage(Message = "Sync {SyncId}: {Text}")]
    private static partial void LogConnector(ILogger logger, LogLevel level, int syncId, string text);
}
