namespace Nexus.Tests;

internal sealed class StubHandler(params Func<HttpResponseMessage>[] replies) : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _replies = new(replies);

    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(_replies.Dequeue()());
    }
}
