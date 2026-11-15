namespace Nexus.Service.Storage;

/// <summary>Where an app's rows go: its push API's address, and the key the service sends there.</summary>
public sealed class Destination
{
    public int Id { get; set; }

    public int AppId { get; set; }

    public App? App { get; set; }

    /// <summary>The push API's address, such as <c>http://localhost:6161/ingest</c>.</summary>
    public required string Url { get; set; }

    /// <summary>The push API key, encrypted by ASP.NET Data Protection.</summary>
    public required string Key { get; set; }

    public DateTime CreatedAt { get; set; }
}
