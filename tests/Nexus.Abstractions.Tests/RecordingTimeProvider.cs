namespace Nexus.Tests;

// Records every delay and fires it at once, so retry tests never wait.
internal sealed class RecordingTimeProvider(DateTimeOffset now) : TimeProvider
{
    public List<TimeSpan> Delays { get; } = [];

    public override DateTimeOffset GetUtcNow() => now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Delays.Add(dueTime);
        ThreadPool.QueueUserWorkItem(_ => callback(state));
        return new SpentTimer();
    }

    private sealed class SpentTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
