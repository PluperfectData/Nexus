using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Storage;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Service.Tests;

[TestFixture]
public sealed class SyncRunTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private ServiceFactory _factory = null!;

    [SetUp]
    public void SetUp() => _factory = new ServiceFactory();

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task A_full_refresh_replaces_the_table_at_the_destination()
    {
        _factory.Source.Rows.AddRange([[1L, "pen"], [2L, "ink"]]);
        var syncId = await SeedAsync(SyncMode.FullRefresh);

        var run = await RunAsync(syncId);

        run.Status.ShouldBe(RunStatus.Succeeded);
        run.Rows.ShouldBe(2);
        run.EndedAt.ShouldNotBeNull();
        _factory.Push.Tables["orders"].ShouldBe(["""[1,"pen"]""", """[2,"ink"]"""]);
        _factory.Push.Requests.ShouldContain("POST /ingest/sources/orders/replace");
        _factory.Push.Origins["orders"].ShouldBe("Nexus workspace Acme, connection Shop (Fake), stream shop.orders");
        var sync = await FindSyncAsync(syncId);
        sync.Status.ShouldBe(SyncStatus.Ready);
        sync.Checkpoint.ShouldBeNull();
        _factory.Source.Configs.Single().Values["host"].ShouldBe("db.test");
    }

    [Test]
    public async Task A_run_whose_destination_was_removed_fails_and_asks_for_another()
    {
        var syncId = await SeedAsync(SyncMode.FullRefresh);
        await using (var db = _factory.NewContext())
        {
            await db.Destinations.ExecuteDeleteAsync();
        }

        var run = await RunAsync(syncId);

        run.Status.ShouldBe(RunStatus.Failed);
        run.ErrorKind.ShouldBe(ConnectorErrorKind.ConfigInvalid);
        run.Error.ShouldBe(SyncRunner.NoDestination);
        var sync = await FindSyncAsync(syncId);
        sync.DestinationId.ShouldBeNull();
        sync.Status.ShouldBe(SyncStatus.Failed);
        _factory.Source.Reads.ShouldBeEmpty();
    }

    [Test]
    public async Task An_incremental_sync_resumes_from_the_checkpoint_the_destination_committed()
    {
        _factory.Source.Rows.AddRange([[1L, "pen"], [2L, "ink"]]);
        var syncId = await SeedAsync(SyncMode.Incremental);
        await RunAsync(syncId);
        (await FindSyncAsync(syncId)).Checkpoint.ShouldBe("""{"cursor":2}""");

        _factory.Source.Rows.Clear();
        _factory.Source.Rows.AddRange([[1L, "pen"], [2L, "blue ink"], [3L, "nib"]]);
        var second = await RunAsync(syncId, expectedRuns: 2);

        second.Rows.ShouldBe(2);
        _factory.Source.Reads[1].State!["shop.orders"].GetRawText().ShouldBe("""{"cursor":2}""");
        _factory.Push.Tables["orders"].ShouldBe(["""[1,"pen"]""", """[2,"blue ink"]""", """[3,"nib"]"""]);
        (await FindSyncAsync(syncId)).Checkpoint.ShouldBe("""{"cursor":3}""");
    }

    [Test]
    public async Task Tries_again_after_a_failure_that_may_pass()
    {
        _factory.Source.Rows.Add([1L, "pen"]);
        _factory.Push.FailingBatches = 1;
        var syncId = await SeedAsync(SyncMode.FullRefresh);

        await RunAsync(syncId, expectedRuns: 2);

        var runs = await RunsAsync(syncId);
        runs[0].Status.ShouldBe(RunStatus.Failed);
        runs[0].ErrorKind.ShouldBe(ConnectorErrorKind.Transient);
        runs[1].Attempt.ShouldBe(2);
        runs[1].Status.ShouldBe(RunStatus.Succeeded);
        _factory.Push.Tables["orders"].ShouldBe(["""[1,"pen"]"""]);
    }

    [Test]
    public async Task Stops_trying_once_the_retries_run_out()
    {
        for (var i = 0; i < 4; i++)
        {
            _factory.Source.Failures.Enqueue(new ConnectorException(ConnectorErrorKind.Transient, "The database is busy."));
        }

        var syncId = await SeedAsync(SyncMode.FullRefresh);

        var last = await RunAsync(syncId, expectedRuns: 4);

        last.Attempt.ShouldBe(4);
        last.Error.ShouldBe("The database is busy.");
        (await FindSyncAsync(syncId)).Status.ShouldBe(SyncStatus.Failed);
    }

    [Test]
    public async Task Marks_the_connection_as_needing_a_sign_in_when_the_provider_ends_it()
    {
        _factory.Source.Failures.Enqueue(new ConnectorException(ConnectorErrorKind.AuthExpired, "Sign in again."));
        var syncId = await SeedAsync(SyncMode.FullRefresh);

        var run = await RunAsync(syncId);

        run.Status.ShouldBe(RunStatus.Failed);
        run.ErrorKind.ShouldBe(ConnectorErrorKind.AuthExpired);
        await using var db = _factory.NewContext();
        var sync = await db.Syncs.Include(s => s.Connection).SingleAsync(s => s.Id == syncId);
        sync.Connection!.NeedsSignIn.ShouldBeTrue();
        sync.Status.ShouldBe(SyncStatus.Failed);
        (await RunsAsync(syncId)).Count.ShouldBe(1);
    }

    [Test]
    public async Task A_key_the_destination_refuses_fails_the_run_for_good()
    {
        _factory.Source.Rows.Add([1L, "pen"]);
        var syncId = await SeedAsync(SyncMode.FullRefresh, key: "scry_revoked");

        var run = await RunAsync(syncId);

        run.ErrorKind.ShouldBe(ConnectorErrorKind.ConfigInvalid);
        run.Error.ShouldBe("The push API refused the key. Send a push API key.");
        (await RunsAsync(syncId)).Count.ShouldBe(1);
    }

    [Test]
    public async Task Keeps_tokens_a_connector_refreshes()
    {
        var tokens = new OAuthTokens("access-2", "refresh-2", new DateTimeOffset(2026, 11, 15, 13, 0, 0, TimeSpan.Zero));
        _factory.Source.Refreshed = tokens;
        var syncId = await SeedAsync(SyncMode.FullRefresh);

        await RunAsync(syncId);

        await using var db = _factory.NewContext();
        var connection = await db.Connections.SingleAsync();
        connection.Tokens.ShouldNotBeNull();
        connection.Tokens.ShouldNotContain("access-2");
        _factory.Services.GetRequiredService<Secrets>().UnprotectTokens(connection.Tokens).ShouldBe(tokens);
    }

    [Test]
    public async Task Runs_again_a_sync_that_a_restart_cut_off()
    {
        _factory.Source.Rows.Add([1L, "pen"]);
        int syncId;
        await using (var db = _factory.NewContext())
        {
            syncId = await SeedAsync(db, _factory.NewSecrets(), SyncMode.FullRefresh, SyncStatus.Running, FakePush.Key);
        }

        // Starting the service queues the sync again.
        _ = _factory.Services;
        var run = await WaitForRunsAsync(syncId, 1);

        run.Status.ShouldBe(RunStatus.Succeeded);
        _factory.Push.Tables["orders"].ShouldBe(["""[1,"pen"]"""]);
    }

    private async Task<int> SeedAsync(SyncMode mode, string key = FakePush.Key)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        return await SeedAsync(db, scope.ServiceProvider.GetRequiredService<Secrets>(), mode, SyncStatus.Ready, key);
    }

    private static async Task<int> SeedAsync(NexusDbContext db, Secrets secrets, SyncMode mode, SyncStatus status, string key)
    {
        var now = DateTime.UtcNow;
        var workspace = new Workspace { Name = "Acme", CreatedAt = now };
        var app = new App { Workspace = workspace, Name = "Scry", CreatedAt = now };
        var destination = new Destination { App = app, Url = "http://scry.test/ingest", Key = secrets.ProtectText(key), CreatedAt = now };
        var connection = new Connection
        {
            Workspace = workspace,
            Name = "Shop",
            Connector = FakeSource.Id,
            Settings = secrets.Protect(new Dictionary<string, string?> { ["host"] = "db.test" }),
            CreatedAt = now,
        };
        var sync = new Sync
        {
            Connection = connection,
            Destination = destination,
            Stream = FakeSource.StreamName,
            Table = "orders",
            Mode = mode,
            Status = status,
            CreatedAt = now,
        };
        db.Syncs.Add(sync);
        await db.SaveChangesAsync();
        return sync.Id;
    }

    // Queues the sync, then waits until it has ended this many runs in all, and answers the last.
    private async Task<Run> RunAsync(int syncId, int expectedRuns = 1)
    {
        _factory.Services.GetRequiredService<PendingRuns>().Enqueue(new PendingRun(syncId, 1));
        return await WaitForRunsAsync(syncId, expectedRuns);
    }

    private async Task<Run> WaitForRunsAsync(int syncId, int count)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            var runs = await RunsAsync(syncId);
            var ended = runs.Where(run => run.EndedAt is not null).ToList();
            var sync = await FindSyncAsync(syncId);
            if (ended.Count >= count && (sync.Status is SyncStatus.Ready or SyncStatus.Failed))
            {
                return ended[^1];
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Sync {syncId} ended {ended.Count} of {count} runs and is {sync.Status}.");
            }

            await Task.Delay(50);
        }
    }

    private async Task<List<Run>> RunsAsync(int syncId)
    {
        await using var db = _factory.NewContext();
        return await db.Runs.Where(run => run.SyncId == syncId).OrderBy(run => run.Id).ToListAsync();
    }

    private async Task<Sync> FindSyncAsync(int syncId)
    {
        await using var db = _factory.NewContext();
        return await db.Syncs.SingleAsync(sync => sync.Id == syncId);
    }
}
