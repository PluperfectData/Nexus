namespace Nexus.Service;

/// <summary>The <c>Nexus</c> configuration section.</summary>
public sealed class ServiceSettings
{
    public const string Section = "Nexus";

    /// <summary>Where the service keeps its own files, such as its encryption keys, relative to the content root unless absolute.</summary>
    public string DataDirectory { get; set; } = "data";
}
