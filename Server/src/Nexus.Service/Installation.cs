using Microsoft.EntityFrameworkCore;
using Nexus.Service.Auth;
using Nexus.Service.Storage;

namespace Nexus.Service;

public enum InstallStage
{
    Database,
    Address,
    Admin,
    Done,
}

/// <summary>
/// Whether the instance is set up: its database opens and is up to date, it knows its client's address, and it has an
/// admin. Until then, only the installer and the health check answer.
/// </summary>
public sealed partial class Installation(
    Instance instance,
    IDatabaseHost database,
    IServiceScopeFactory scopes,
    PendingRuns runs,
    InstallCode code,
    ILogger<Installation> logger) : IDisposable
{
    private static readonly TimeSpan RecoveryLifetime = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _switching = new(1, 1);
    private volatile bool _ready;
    private volatile bool _hasAdmin;
    private volatile string? _problem;

    /// <summary>The database contexts open, or null before one is set.</summary>
    public string? ConnectionString => instance.ConfiguredConnectionString ?? instance.File.Database?.ConnectionString;

    public bool DatabaseReady => _ready;

    /// <summary>Why the database set could not be opened, in the server's words.</summary>
    public string? DatabaseProblem => _problem;

    /// <summary>Opens the database at the service's start, and writes the install code to the log while it is not set up.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (ConnectionString is { } connectionString)
        {
            await OpenAsync(connectionString, ct);
        }
        else if (instance.File.DatabasePlace is not null)
        {
            _problem = "Nexus cannot decrypt its database's password, since the keys in its data folder changed. Enter the password again.";
        }

        if (!await IsInstalledAsync(ct))
        {
            Announce();
        }
    }

    public async Task<bool> IsInstalledAsync(CancellationToken ct) => await StageAsync(ct) == InstallStage.Done;

    public async Task<InstallStage> StageAsync(CancellationToken ct) =>
        !_ready ? InstallStage.Database
        : instance.ClientOrigin is null ? InstallStage.Address
        : !await HasAdminAsync(ct) ? InstallStage.Admin
        : InstallStage.Done;

    /// <summary>
    /// Opens the database, makes it if it is missing, and brings it up to date, then keeps it in place of any set before.
    /// Answers why it could not, leaving the one before in place.
    /// </summary>
    public async Task<string?> UseDatabaseAsync(DatabaseSettings settings, CancellationToken ct)
    {
        await _switching.WaitAsync(ct);
        try
        {
            try
            {
                await database.PrepareAsync(settings.ConnectionString, ct);
            }
            catch (DatabaseException ex)
            {
                return ex.Message;
            }

            var moved = !_ready || instance.File.DatabasePlace is not { } before || !before.SamePlace(settings);
            instance.File.SaveDatabase(settings);
            _problem = null;
            if (moved)
            {
                // Queued runs name syncs in the database left behind.
                runs.Forget();
                _hasAdmin = false;
                _ready = true;
                await RequeueAsync(ct);
            }

            return null;
        }
        finally
        {
            _switching.Release();
        }
    }

    /// <summary>Records that the database has an admin, and spends the install code once that sets the instance up.</summary>
    public async Task AdminMadeAsync(CancellationToken ct)
    {
        _hasAdmin = true;
        if (await IsInstalledAsync(ct))
        {
            code.Clear();
        }
    }

    /// <summary>
    /// Writes the install code to the log, with the installer's address. On an instance already set up, the code is one
    /// for making an admin, and lasts an hour.
    /// </summary>
    public async Task AnnounceAsync(CancellationToken ct)
    {
        if (await IsInstalledAsync(ct))
        {
            var issued = code.Issue(RecoveryLifetime);
            if (code.MayAnnounce())
            {
                LogRecovery(logger, Page(issued), issued);
            }

            return;
        }

        Announce();
    }

    private void Announce()
    {
        var issued = code.Issue(lifetime: null);
        if (code.MayAnnounce())
        {
            LogInstall(logger, Page(issued), issued);
        }
    }

    private string Page(string issued) =>
        instance.ClientOrigin is { } client ? $"{client}/install#{issued}" : "/install on the Nexus client";

    private async Task OpenAsync(string connectionString, CancellationToken ct)
    {
        try
        {
            await database.PrepareAsync(connectionString, ct);
        }
        catch (DatabaseException ex)
        {
            _problem = ex.Message;
            LogUnreachable(logger, ex.Message);
            return;
        }

        _ready = true;
        await RequeueAsync(ct);
    }

    // A run that a restart, or a change of database, cut off starts again.
    private async Task RequeueAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await runs.RequeueAsync(scope.ServiceProvider.GetRequiredService<NexusDbContext>(), ct);
    }

    // An instance keeps at least one admin, so once one is seen the answer holds until the database changes.
    private async Task<bool> HasAdminAsync(CancellationToken ct)
    {
        if (_hasAdmin)
        {
            return true;
        }

        await using var scope = scopes.CreateAsyncScope();
        _hasAdmin = await scope.ServiceProvider.GetRequiredService<NexusDbContext>().Users.AnyAsync(u => u.IsAdmin, ct);
        return _hasAdmin;
    }

    public void Dispose() => _switching.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Nexus is not set up yet. Open {Page} to set it up. A page on another machine asks for this install code: {Code}")]
    private static partial void LogInstall(ILogger logger, string page, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An install code was asked for, to make an admin. Open {Page} within an hour, and enter this code: {Code}")]
    private static partial void LogRecovery(ILogger logger, string page, string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "Nexus cannot open its database: {Problem}")]
    private static partial void LogUnreachable(ILogger logger, string problem);}
