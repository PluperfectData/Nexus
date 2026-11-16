namespace Nexus.Service.Storage;

/// <summary>Where an app's rows go: its push API's address, and the key the service sends there.</summary>
public sealed class Destination
{
    public const int MaxKeyNameLength = 200;

    public const int MaxKeyPrefixLength = 64;

    public const int MaxKeyProblemLength = 1000;

    public int Id { get; set; }

    public int AppId { get; set; }

    public App? App { get; set; }

    /// <summary>The push API's address, such as <c>http://localhost:6161/ingest</c>.</summary>
    public required string Url { get; set; }

    /// <summary>The push API key, encrypted by ASP.NET Data Protection.</summary>
    public required string Key { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>The key's name at the push API, as it reported at the last check.</summary>
    public string? KeyName { get; set; }

    public string? KeyPrefix { get; set; }

    public DateTime? KeyCheckedAt { get; set; }

    /// <summary>Why the push API refused the key at the last check, or null when it took the key.</summary>
    public string? KeyProblem { get; set; }

    /// <summary>Records what the push API said of the key: its name and prefix, or why it refused the key.</summary>
    public void Checked(Nexus.Push.PushKey? key, string? problem, DateTime at)
    {
        if (key is not null)
        {
            KeyName = key.Name.Length > MaxKeyNameLength ? key.Name[..MaxKeyNameLength] : key.Name;
            KeyPrefix = key.Prefix.Length > MaxKeyPrefixLength ? key.Prefix[..MaxKeyPrefixLength] : key.Prefix;
        }

        KeyProblem = problem is null || problem.Length <= MaxKeyProblemLength ? problem : problem[..MaxKeyProblemLength];
        KeyCheckedAt = at;
    }
}
