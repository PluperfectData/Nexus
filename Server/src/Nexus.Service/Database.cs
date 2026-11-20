using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Nexus.Service.Storage;

namespace Nexus.Service;

/// <summary>Why a database could not be opened or brought up to date, in the server's own words.</summary>
public sealed class DatabaseException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The server did not answer at all, as while it starts or restarts, so trying again later can work.</summary>
    public bool NotAnswering { get; init; }
}

/// <summary>Waits for a database server still starting. A restart or reboot starts Nexus whether or not MySQL is up.</summary>
public static class DatabaseStart
{
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs <paramref name="prepare"/> until it succeeds, trying again while the server is not answering, for up to
    /// <paramref name="limit"/>. Answers how long it waited, or null when the server answered at once.
    /// </summary>
    public static async Task<TimeSpan?> WaitAsync(
        Func<CancellationToken, Task> prepare,
        TimeSpan limit,
        TimeSpan pause,
        Action<DatabaseException> waiting,
        CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var waited = false;
        while (true)
        {
            try
            {
                await prepare(ct);
                return waited ? Stopwatch.GetElapsedTime(started) : null;
            }
            catch (DatabaseException ex) when (ex.NotAnswering && Stopwatch.GetElapsedTime(started) < limit)
            {
                if (!waited)
                {
                    waiting(ex);
                    waited = true;
                }

                await Task.Delay(pause, ct);
            }
        }
    }

    /// <summary>Whether <paramref name="ex"/>, or an exception it wraps, is MySQL not answering at all.</summary>
    public static bool NotAnswering(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is MySqlException { ErrorCode: MySqlErrorCode.UnableToConnectToHost or MySqlErrorCode.ServerShutdown })
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Where the service keeps its records. Tests stand in a file for MySQL.</summary>
public interface IDatabaseHost
{
    void Configure(DbContextOptionsBuilder options, string connectionString);

    /// <summary>Opens the database, making it when it is missing and the user may, and applies every migration.</summary>
    /// <exception cref="DatabaseException">The server could not be reached, or refused.</exception>
    Task PrepareAsync(string connectionString, CancellationToken ct);
}

public sealed class MySqlDatabaseHost : IDatabaseHost
{
    private const uint ConnectSeconds = 10;

    public void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseMySql(connectionString, NexusDbContext.ServerVersion);

    public async Task PrepareAsync(string connectionString, CancellationToken ct)
    {
        MySqlConnectionStringBuilder target;
        try
        {
            target = new MySqlConnectionStringBuilder(connectionString) { ConnectionTimeout = ConnectSeconds };
        }
        catch (ArgumentException ex)
        {
            throw new DatabaseException($"MySQL cannot read the connection string: {ex.Message}", ex);
        }

        try
        {
            await using var probe = new MySqlConnection(target.ConnectionString);
            await probe.OpenAsync(ct);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.UnknownDatabase && !string.IsNullOrEmpty(target.Database))
        {
            await CreateAsync(target, ct);
        }
        catch (MySqlException ex)
        {
            throw new DatabaseException(ex.Message, ex)
            {
                NotAnswering = DatabaseStart.NotAnswering(ex),
            };
        }

        try
        {
            var options = new DbContextOptionsBuilder<NexusDbContext>().UseMySql(target.ConnectionString, NexusDbContext.ServerVersion).Options;
            await using var db = new NexusDbContext(options);
            // One schema change on a slow disk can outlast the 30 seconds a query gets.
            db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
            await db.Database.MigrateAsync(ct);
        }
        catch (Exception ex) when (MySqlCause(ex) is { } cause)
        {
            throw new DatabaseException($"MySQL refused to bring the database up to date: {cause.Message}", ex);
        }
    }

    private static async Task CreateAsync(MySqlConnectionStringBuilder target, CancellationToken ct)
    {
        var server = new MySqlConnectionStringBuilder(target.ConnectionString) { Database = string.Empty };
        try
        {
            await using var connection = new MySqlConnection(server.ConnectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE IF NOT EXISTS `{target.Database.Replace("`", "``", StringComparison.Ordinal)}` CHARACTER SET utf8mb4";
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (MySqlException ex)
        {
            throw new DatabaseException($"The database {target.Database} does not exist, and MySQL would not make it: {ex.Message}", ex);
        }
    }

    // EF wraps some of the server's refusals in its own exceptions.
    private static MySqlException? MySqlCause(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is MySqlException mysql)
            {
                return mysql;
            }
        }

        return null;
    }
}
