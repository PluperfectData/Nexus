using System.Globalization;
using System.Text;

namespace Nexus;

/// <summary>The settings, tokens, and files the host passes on every call.</summary>
/// <param name="Values">Setting values keyed by <see cref="SettingField.Name"/>, secrets included.</param>
/// <param name="Tokens">The current OAuth tokens, for an OAuth connector.</param>
/// <param name="Files">Uploaded files keyed by the <see cref="SettingField.Name"/> of a <see cref="SettingKind.File"/> field.</param>
/// <param name="Client">The host's OAuth app, for an OAuth connector that refreshes its own tokens.</param>
public sealed record ConnectorConfig(
    IReadOnlyDictionary<string, string?> Values,
    OAuthTokens? Tokens = null,
    IReadOnlyDictionary<string, ConnectorFile>? Files = null,
    OAuthClient? Client = null)
{
    // Prints setting and file names only, so secrets stay out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Values = [").AppendJoin(", ", Values.Keys).Append("], Tokens = ").Append(Tokens)
            .Append(", Files = [").AppendJoin(", ", Files?.Keys ?? Enumerable.Empty<string>()).Append("], Client = ")
            .Append(Client);
        return true;
    }
}

/// <summary>The OAuth app the host registered with a service, such as a Schwab app key and secret.</summary>
/// <param name="ClientId">The app's client id.</param>
/// <param name="ClientSecret">The app's client secret.</param>
public sealed record OAuthClient(string ClientId, string ClientSecret)
{
    // Prints the client id only, so the secret stays out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("ClientId = ").Append(ClientId);
        return true;
    }
}

/// <summary>An uploaded file the host stores and opens on request.</summary>
/// <param name="Name">The file's name as uploaded, such as <c>orders.csv</c>.</param>
/// <param name="OpenAsync">Opens a new stream at the start of the file. The caller disposes each stream it opens.</param>
public sealed record ConnectorFile(string Name, Func<CancellationToken, Task<Stream>> OpenAsync);

/// <summary>OAuth tokens the host stores and passes back on every call.</summary>
/// <param name="AccessToken">The token sent with each request.</param>
/// <param name="RefreshToken">The token used to get a new access token.</param>
/// <param name="ExpiresAt">When <paramref name="AccessToken"/> expires.</param>
public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt)
{
    // Prints the expiry only, so tokens stay out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("ExpiresAt = ").Append(ExpiresAt?.ToString("O", CultureInfo.InvariantCulture));
        return true;
    }
}
