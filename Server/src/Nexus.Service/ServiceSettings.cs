namespace Nexus.Service;

/// <summary>
/// The <c>Nexus</c> configuration section. Apart from the data folder, each value is one an operator may set in
/// configuration in place of the installer and the Settings page; a value set here wins over the stored one.
/// </summary>
public sealed class ServiceSettings
{
    public const string Section = "Nexus";

    /// <summary>Where the service keeps its own files, such as its settings and encryption keys, relative to the content root unless absolute.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>The client's origin: the one origin CORS allows, and where mailed links point.</summary>
    public string? ClientOrigin { get; set; }

    /// <summary>The https origin providers send a browser back to after signing in, at <c>/oauth/callback</c>.</summary>
    public string? CallbackOrigin { get; set; }

    /// <summary>The address mail comes from, on a domain the mail transport may send as.</summary>
    public string? MailFrom { get; set; }

    public string? MailFromName { get; set; }

    /// <summary>Where a person reads mail sent to Mailpit, when the service reaches Mailpit at another address.</summary>
    public string? MailpitInbox { get; set; }

    /// <summary>How many days the operator log keeps an entry, from 1 to 365.</summary>
    public int? LogDays { get; set; }

    /// <summary>
    /// Whether every request comes through a proxy such as Caddy, whose X-Forwarded-For and X-Forwarded-Proto say who
    /// asked and over what. Only then are those headers believed.
    /// </summary>
    public bool Proxied { get; set; }
}
