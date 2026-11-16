namespace Nexus.Service;

/// <summary>The <c>Nexus</c> configuration section.</summary>
public sealed class ServiceSettings
{
    public const string Section = "Nexus";

    /// <summary>Where the service keeps its own files, such as its encryption keys, relative to the content root unless absolute.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>The client's origin: the one origin CORS allows, and where mailed links point.</summary>
    public string ClientOrigin { get; set; } = "http://localhost:6165";

    /// <summary>The address mail comes from, on a domain the mail transport may send as.</summary>
    public string MailFrom { get; set; } = "nexus@localhost";

    public string MailFromName { get; set; } = "Nexus";
}
