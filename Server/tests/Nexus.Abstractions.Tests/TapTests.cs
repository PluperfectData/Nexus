using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Tests;

[TestFixture]
public sealed class TapTests
{
    [Test]
    public async Task Passes_every_message_through_in_order()
    {
        SyncMessage[] messages =
        [
            new RecordBatch("orders", [[1L]]),
            new CredentialsRefreshed(new OAuthTokens("access", "refresh", null)),
            new LogMessage(LogLevel.Information, "Read 1 row."),
        ];

        var passed = new List<SyncMessage>();
        await foreach (var message in Yield(messages).Tap((_, _) => Task.CompletedTask, new ListLogger()))
        {
            passed.Add(message);
        }

        passed.ShouldBe(messages);
    }

    [Test]
    public async Task Stores_refreshed_tokens_before_passing_the_message_on()
    {
        var events = new List<string>();
        var messages = Yield(
            new CredentialsRefreshed(new OAuthTokens("access", "refresh", null)),
            new RecordBatch("orders", [[1L]]));

        await foreach (var message in messages.Tap(StoreAsync))
        {
            events.Add(message.GetType().Name);
        }

        events.ShouldBe(["stored access", nameof(CredentialsRefreshed), nameof(RecordBatch)]);

        Task StoreAsync(OAuthTokens tokens, CancellationToken ct)
        {
            events.Add($"stored {tokens.AccessToken}");
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task Writes_log_messages_to_the_logger()
    {
        var logger = new ListLogger();

        await foreach (var _ in Yield(new LogMessage(LogLevel.Warning, "Slow response.")).Tap(logger: logger))
        {
        }

        logger.Entries.ShouldBe([(LogLevel.Warning, "Slow response.")]);
    }

    private static async IAsyncEnumerable<SyncMessage> Yield(params SyncMessage[] messages)
    {
        foreach (var message in messages)
        {
            await Task.Yield();
            yield return message;
        }
    }
}
