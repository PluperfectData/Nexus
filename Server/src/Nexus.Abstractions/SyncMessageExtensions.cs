using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Nexus;

/// <summary>Helpers for a stream of <see cref="SyncMessage"/>.</summary>
public static class SyncMessageExtensions
{
    /// <summary>Passes every message through, after handing refreshed tokens and log lines to the host.</summary>
    public static async IAsyncEnumerable<SyncMessage> Tap(
        this IAsyncEnumerable<SyncMessage> messages,
        Func<OAuthTokens, CancellationToken, Task>? onCredentialsRefreshed = null,
        ILogger? logger = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        await foreach (var message in messages.WithCancellation(ct).ConfigureAwait(false))
        {
            switch (message)
            {
                case CredentialsRefreshed refreshed when onCredentialsRefreshed is not null:
                    await onCredentialsRefreshed(refreshed.Tokens, ct).ConfigureAwait(false);
                    break;

                case LogMessage log when logger is not null:
                    ConnectorLog.Write(logger, log.Level, log.Text);
                    break;
            }

            yield return message;
        }
    }
}
