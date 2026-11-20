using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Service.Tests;

/// <summary>The background workers losing the database while the service runs, as when MySQL restarts.</summary>
[TestFixture]
public sealed class DatabaseLostTests
{
    /// <summary>MySqlConnector's own refusal, from a connection to a local port nothing listens on.</summary>
    private static async Task<MySqlException> RefusedAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var connection = new MySqlConnection($"Server=127.0.0.1;Port={port};User ID=nexus;Connection Timeout=10");
        return await Should.ThrowAsync<MySqlException>(() => connection.OpenAsync());
    }

    private static IServiceScopeFactory Throwing<TService>(Exception ex)
        where TService : class
    {
        var services = new ServiceCollection();
        services.AddScoped<TService>(_ => throw ex);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static async Task<(LogLevel Level, string Text, Exception? Exception)> FirstEntryAsync<T>(
        BackgroundService worker,
        ListLogger<T> logger)
    {
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (logger.Entries.IsEmpty && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        logger.Entries.Count.ShouldBe(1);
        return logger.Entries.Single();
    }

    [Test]
    public async Task A_refused_connection_is_not_answering_wherever_it_is_wrapped()
    {
        var refused = await RefusedAsync();

        refused.ErrorCode.ShouldBe(MySqlErrorCode.UnableToConnectToHost);
        DatabaseStart.NotAnswering(refused).ShouldBeTrue();
        DatabaseStart.NotAnswering(new InvalidOperationException("An exception was thrown while reading.", refused)).ShouldBeTrue();
        DatabaseStart.NotAnswering(new InvalidOperationException("Access denied for user 'nexus'")).ShouldBeFalse();
    }

    [Test]
    public async Task A_sync_that_loses_the_database_logs_one_line()
    {
        var queue = new PendingRuns(TimeProvider.System);
        queue.Enqueue(new PendingRun(7, 1));
        var logger = new ListLogger<SyncWorker>();

        var entry = await FirstEntryAsync(new SyncWorker(queue, Throwing<SyncRunner>(await RefusedAsync()), logger), logger);

        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Text.ShouldBe("Sync 7 could not run: the database is not answering.");
        entry.Exception.ShouldBeNull();
    }

    [Test]
    public async Task A_sync_that_fails_otherwise_logs_the_exception()
    {
        var queue = new PendingRuns(TimeProvider.System);
        queue.Enqueue(new PendingRun(7, 1));
        var logger = new ListLogger<SyncWorker>();
        var failure = new InvalidOperationException("Something else broke.");

        var entry = await FirstEntryAsync(new SyncWorker(queue, Throwing<SyncRunner>(failure), logger), logger);

        entry.Level.ShouldBe(LogLevel.Error);
        entry.Exception.ShouldBeSameAs(failure);
    }

    [Test]
    public async Task A_sign_in_check_that_loses_the_database_logs_one_line()
    {
        using var factory = new ServiceFactory();
        var installation = factory.Services.GetRequiredService<Installation>();
        installation.DatabaseReady.ShouldBeTrue();
        var logger = new ListLogger<SignInWatcher>();

        var entry = await FirstEntryAsync(
            new SignInWatcher(installation, Throwing<SignInChecks>(await RefusedAsync()), new OneTick(), logger),
            logger);

        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Text.ShouldStartWith("The connections' sign-ins could not be checked: the database is not answering.");
        entry.Exception.ShouldBeNull();
    }

    [Test]
    public async Task A_sign_in_check_that_fails_otherwise_logs_the_exception()
    {
        using var factory = new ServiceFactory();
        var installation = factory.Services.GetRequiredService<Installation>();
        var logger = new ListLogger<SignInWatcher>();
        var failure = new InvalidOperationException("Something else broke.");

        var entry = await FirstEntryAsync(new SignInWatcher(installation, Throwing<SignInChecks>(failure), new OneTick(), logger), logger);

        entry.Level.ShouldBe(LogLevel.Error);
        entry.Exception.ShouldBeSameAs(failure);
    }

    [Test]
    public async Task Pruning_that_loses_the_database_logs_one_line()
    {
        using var factory = new ServiceFactory();
        var installation = factory.Services.GetRequiredService<Installation>();
        installation.DatabaseReady.ShouldBeTrue();
        var logger = new ListLogger<LogPruner>();

        var entry = await FirstEntryAsync(new LogPruner(installation, Throwing<LogPruning>(await RefusedAsync()), TimeProvider.System, logger), logger);

        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Text.ShouldStartWith("The operator log could not be pruned: the database is not answering.");
        entry.Exception.ShouldBeNull();
    }

    [Test]
    public async Task Pruning_that_fails_otherwise_logs_the_exception()
    {
        using var factory = new ServiceFactory();
        var installation = factory.Services.GetRequiredService<Installation>();
        var logger = new ListLogger<LogPruner>();
        var failure = new InvalidOperationException("Something else broke.");

        var entry = await FirstEntryAsync(new LogPruner(installation, Throwing<LogPruning>(failure), TimeProvider.System, logger), logger);

        entry.Level.ShouldBe(LogLevel.Error);
        entry.Exception.ShouldBeSameAs(failure);
    }

    /// <summary>Fires each timer once, at once, so a periodic loop runs one round without waiting for its period.</summary>
    private sealed class OneTick : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new Idle();
        }

        private sealed class Idle : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Text, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception), exception));
    }
}
