using System.Runtime.CompilerServices;

namespace Nexus.Service.Tests;

// A connector that signs in, with the life it is given, for the kinds no real connector has yet. It reads nothing, and
// each keep-alive call throws the next queued failure, if any.
internal sealed class StandInSignIn(string id, SignInLife life) : ISourceConnector, IKeepsSignInAlive
{
    public ConnectorSpec Spec { get; } = new(id, $"Stand-in {id}", "Signs in.", "1.0.0", AuthKind.OAuth, [], SignIn: life);

    public Queue<Exception> KeepAliveFailures { get; } = [];

    public int KeepAlives { get; private set; }

    public Task<CheckResult> CheckAsync(ConnectorConfig config, CancellationToken ct) => Task.FromResult(CheckResult.Success);

    public Task<SourceCatalog> DiscoverAsync(ConnectorConfig config, CancellationToken ct) => Task.FromResult(new SourceCatalog([FakeSource.Orders]));

    public async IAsyncEnumerable<SyncMessage> ReadAsync(ConnectorConfig config, ReadRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        yield break;
    }

    public Task<TimeSpan?> KeepAliveAsync(ConnectorConfig config, CancellationToken ct)
    {
        KeepAlives++;
        return KeepAliveFailures.TryDequeue(out var failure) ? Task.FromException<TimeSpan?>(failure) : Task.FromResult<TimeSpan?>(TimeSpan.FromMinutes(5));
    }
}
