using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Nexus.Service;

/// <summary>Encrypts connection settings, tokens, and push keys for the database, with ASP.NET Data Protection.</summary>
public sealed class Secrets(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Nexus.Secrets");

    public string Protect(IReadOnlyDictionary<string, string?> settings) => _protector.Protect(JsonSerializer.Serialize(settings));

    public IReadOnlyDictionary<string, string?> Unprotect(string stored) =>
        JsonSerializer.Deserialize<Dictionary<string, string?>>(_protector.Unprotect(stored))
        ?? throw new InvalidDataException("A stored connection holds no settings.");

    public string ProtectTokens(OAuthTokens tokens) => _protector.Protect(JsonSerializer.Serialize(tokens));

    public OAuthTokens? UnprotectTokens(string? stored) =>
        stored is null ? null : JsonSerializer.Deserialize<OAuthTokens>(_protector.Unprotect(stored));

    public string ProtectText(string text) => _protector.Protect(text);

    public string UnprotectText(string stored) => _protector.Unprotect(stored);
}
