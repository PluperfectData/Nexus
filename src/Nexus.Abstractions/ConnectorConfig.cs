using System.Globalization;
using System.Text;

namespace Nexus;

/// <summary>The settings and tokens the host passes on every call.</summary>
/// <param name="Values">Setting values keyed by <see cref="SettingField.Name"/>, secrets included.</param>
/// <param name="Tokens">The current OAuth tokens, for an OAuth connector.</param>
public sealed record ConnectorConfig(IReadOnlyDictionary<string, string?> Values, OAuthTokens? Tokens = null)
{
    // Prints setting names only, so secrets stay out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Values = [").AppendJoin(", ", Values.Keys).Append("], Tokens = ").Append(Tokens);
        return true;
    }
}

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
