using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nexus.Service.Auth;
using Nexus.Service.Storage;

namespace Nexus.Service;

/// <summary>Where an operator log entry belongs, which decides who sees it.</summary>
public readonly record struct LogPlace(int? WorkspaceId, Workspace? Workspace, Connection? Connection, Sync? Sync)
{
    /// <summary>The whole instance, which only admins see.</summary>
    public static LogPlace Instance => default;

    public static LogPlace In(int workspaceId) => new(workspaceId, null, null, null);

    public static LogPlace In(Workspace workspace) => new(null, workspace, null, null);

    public static LogPlace On(Connection connection) => new(connection.WorkspaceId, null, connection, null);

    public static LogPlace On(Sync sync, Connection connection) => new(connection.WorkspaceId, null, connection, sync);
}

/// <summary>
/// Adds operator log entries to the request's own context, so each is saved with the change it describes, or not at all.
/// The caller saves.
/// </summary>
public sealed class OperatorLog(NexusDbContext db, IHttpContextAccessor http, TimeProvider time)
{
    public static string AppActor(string name) => "app " + name;

    public void Info(string text, LogPlace place, string? actor = null) => Add(EntryLevel.Information, text, place, actor);

    public void Warn(string text, LogPlace place, string? actor = null) => Add(EntryLevel.Warning, text, place, actor);

    public void Error(string text, LogPlace place, string? actor = null) => Add(EntryLevel.Error, text, place, actor);

    private void Add(EntryLevel level, string text, LogPlace place, string? actor)
    {
        var entry = new LogEntry
        {
            At = time.GetUtcNow().UtcDateTime,
            Level = level,
            Text = Clip(text, LogEntry.MaxTextLength),
            Actor = (actor ?? Caller()) is { } who ? Clip(who, LogEntry.MaxActorLength) : null,
            WorkspaceId = place.WorkspaceId,
            Pending = true,
        };

        // A row made in this same request has no id until it is saved, so the entry points at the row itself.
        if (place.Workspace is { } workspace)
        {
            if (workspace.Id == 0)
            {
                entry.Workspace = workspace;
            }
            else
            {
                entry.WorkspaceId = workspace.Id;
            }
        }

        if (place.Connection is { } connection)
        {
            if (connection.Id == 0)
            {
                entry.Connection = connection;
            }
            else
            {
                entry.ConnectionId = connection.Id;
            }
        }

        if (place.Sync is { } sync)
        {
            if (sync.Id == 0)
            {
                entry.Sync = sync;
            }
            else
            {
                entry.SyncId = sync.Id;
            }
        }

        db.LogEntries.Add(entry);
    }

    private string? Caller()
    {
        var user = http.HttpContext?.User;
        if (user?.FindFirstValue(AppKeyHandler.AppNameClaim) is { } app)
        {
            return AppActor(app);
        }

        return user?.Identity?.IsAuthenticated == true ? user.FindFirstValue(ClaimTypes.Name) : null;
    }

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>The words operator log entries share.</summary>
public static class LogWords
{
    public static string Rows(long count) => count == 1 ? "1 row" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} rows";

    /// <summary>"Shop (MySQL)".</summary>
    public static string Connection(Connection connection, SourceConnectors connectors) =>
        $"{connection.Name} ({connectors.Find(connection.Connector)?.Spec.DisplayName ?? connection.Connector})";

    /// <summary>"Scry at http://localhost:6161/ingest".</summary>
    public static string Destination(Destination destination, string appName) => $"{appName} at {destination.Url}";

    /// <summary>"in full", or "incrementally by updated_at".</summary>
    public static string Read(SyncMode mode, string? cursor) =>
        mode == SyncMode.FullRefresh ? "in full" : cursor is null ? "incrementally" : $"incrementally by {cursor}";

    /// <summary>Said of a change made on an app's connect page, whose app is the actor.</summary>
    public static string OnConnectPage(string? app) => app is null ? string.Empty : " on a connect page";

    public static string? ConnectPageActor(string? app) => app is null ? null : OperatorLog.AppActor(app);

    /// <summary>"in 5 minutes".</summary>
    public static string After(TimeSpan wait) =>
        wait <= TimeSpan.Zero ? "now"
        : wait < TimeSpan.FromMinutes(1) ? "in " + Count((int)Math.Ceiling(wait.TotalSeconds), "second")
        : wait < TimeSpan.FromHours(1) ? "in " + Count((int)Math.Ceiling(wait.TotalMinutes), "minute")
        : "in " + Count((int)Math.Ceiling(wait.TotalHours), "hour");

    /// <summary>"1 sync", or "3 syncs".</summary>
    public static string Count(int count, string unit) => count == 1 ? $"1 {unit}" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} {unit}s";

    /// <summary>"account …1234", or "accounts …1234 and …5678".</summary>
    public static string Accounts(IReadOnlyList<SignInAccount> accounts) =>
        (accounts.Count == 1 ? "account " : "accounts ") + List([.. accounts.Select(account => account.Label)]);

    /// <summary>"a", "a and b", or "a, b, and c".</summary>
    public static string List(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))}, and {items[^1]}",
    };
}

/// <summary>Writes each operator log entry to the developer log once it is saved.</summary>
public sealed partial class OperatorLogWriter(ILoggerFactory loggers) : SaveChangesInterceptor
{
    public const string Category = "Nexus.Operator";

    private readonly ILogger _logger = loggers.CreateLogger(Category);

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Write(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Write(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Write(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<LogEntry>().Select(e => e.Entity).Where(e => e.Pending).ToList())
        {
            entry.Pending = false;
            var level = entry.Level switch
            {
                EntryLevel.Error => LogLevel.Error,
                EntryLevel.Warning => LogLevel.Warning,
                _ => LogLevel.Information,
            };

            if (entry.Actor is { } actor)
            {
                LogBy(_logger, level, entry.Text, actor);
            }
            else
            {
                Log(_logger, level, entry.Text);
            }
        }
    }

    [LoggerMessage(Message = "{Text}")]
    private static partial void Log(ILogger logger, LogLevel level, string text);

    [LoggerMessage(Message = "{Text} ({Actor})")]
    private static partial void LogBy(ILogger logger, LogLevel level, string text, string actor);
}

/// <summary>Deletes the operator log entries older than the instance keeps them.</summary>
public sealed class LogPruning(NexusDbContext db, Instance instance, TimeProvider time)
{
    public async Task<int> PruneAsync(CancellationToken ct)
    {
        var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-instance.LogDays);
        return await db.LogEntries.Where(e => e.At < cutoff).ExecuteDeleteAsync(ct);
    }
}

/// <summary>Prunes the operator log when the service starts, and every hour after, while it has a database.</summary>
public sealed partial class LogPruner(Installation installation, IServiceScopeFactory scopes, TimeProvider time, ILogger<LogPruner> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every, time);
        do
        {
            if (installation.DatabaseReady)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<LogPruning>().PruneAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    if (DatabaseStart.NotAnswering(ex))
                    {
                        LogDatabaseLost(logger);
                    }
                    else
                    {
                        LogFailure(logger, ex);
                    }
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The operator log could not be pruned.")]
    private static partial void LogFailure(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The operator log could not be pruned: the database is not answering. The next try is in an hour.")]
    private static partial void LogDatabaseLost(ILogger logger);
}
