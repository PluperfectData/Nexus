using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Storage;

namespace Nexus.Service;

/// <summary>A sync waiting to run, and which try this is.</summary>
public sealed record PendingRun(int SyncId, int Attempt);

/// <summary>
/// The runs waiting for <see cref="SyncWorker"/>, in the order they were added. Each belongs to the database in use when
/// it was added, and is dropped once the service moves to another.
/// </summary>
public sealed class PendingRuns(TimeProvider time)
{
    private readonly Channel<(PendingRun Run, int Database)> _runs = Channel.CreateUnbounded<(PendingRun, int)>();
    private int _database;

    public void Enqueue(PendingRun run) => _runs.Writer.TryWrite((run, Volatile.Read(ref _database)));

    /// <summary>Adds the run once <paramref name="delay"/> has passed.</summary>
    public void EnqueueAfter(PendingRun run, TimeSpan delay)
    {
        var database = Volatile.Read(ref _database);
        if (delay <= TimeSpan.Zero)
        {
            _runs.Writer.TryWrite((run, database));
            return;
        }

        _ = Task.Delay(delay, time).ContinueWith(_ => _runs.Writer.TryWrite((run, database)), TaskScheduler.Default);
    }

    /// <summary>Drops every run waiting, and every run a retry would add: they belong to the database the service moves away from.</summary>
    public void Forget() => Interlocked.Increment(ref _database);

    /// <summary>Queues again every sync that was queued or running when the service last stopped.</summary>
    public async Task RequeueAsync(NexusDbContext db, CancellationToken ct)
    {
        var cut = await db.Syncs
            .Where(sync => sync.Status == SyncStatus.Queued || sync.Status == SyncStatus.Running)
            .OrderBy(sync => sync.Id)
            .Select(sync => sync.Id)
            .ToListAsync(ct);
        foreach (var id in cut)
        {
            Enqueue(new PendingRun(id, 1));
        }
    }

    public async IAsyncEnumerable<PendingRun> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var (run, database) in _runs.Reader.ReadAllAsync(ct))
        {
            if (database == Volatile.Read(ref _database))
            {
                yield return run;
            }
        }
    }
}

/// <summary>Runs each queued sync in turn, in the background, and queues again a run that failed for a reason that may pass.</summary>
public sealed partial class SyncWorker(PendingRuns queue, IServiceScopeFactory scopes, ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var pending in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var wait = await scope.ServiceProvider.GetRequiredService<SyncRunner>().RunAsync(pending.SyncId, pending.Attempt, stoppingToken);
                if (wait is { } delay)
                {
                    queue.EnqueueAfter(pending with { Attempt = pending.Attempt + 1 }, delay);
                }
            }
            // The runner records every failure it can; this one, such as a lost database, could not be recorded.
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailure(logger, ex, pending.SyncId);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Sync {SyncId} could not run.")]
    private static partial void LogFailure(ILogger logger, Exception ex, int syncId);
}
